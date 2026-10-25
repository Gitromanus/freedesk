using System.Drawing;
using System.Text.Json;
using FreeDesk.Agent.Codec;

namespace FreeDesk.Agent;

/// <summary>Монитор на стороне хоста (для кнопок переключения).</summary>
public sealed record RemoteDisplay(int Index, int Width, int Height, bool Primary);

/// <summary>Ответ на запрос пароля: сам пароль и просьба запомнить его для этого устройства.</summary>
public sealed record PasswordReply(string Password, bool Remember);

/// <summary>Сторона зрителя: инициирует сессию, принимает кадры, отправляет ввод.</summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private readonly SignalingClient _client;
    private readonly string _targetId;
    private readonly VideoCodec _codec;
    private readonly bool _passwordFromCli;
    private readonly AgentConfig? _config;
    private string? _password;
    private (string Salt, int Iterations, byte[] Hash)? _pendingCredential;
    private H264Decoder? _decoder;
    private IPeerTransport? _peer;
    private string? _sessionId;
    private long _cursor;
    private bool _offerSent;
    private int _frameCount;
    private long _rxBytes;
    private DateTime _lastKeyframeRequest = DateTime.MinValue;
    private readonly System.Diagnostics.Stopwatch _rxWatch = System.Diagnostics.Stopwatch.StartNew();
    private CancellationTokenSource? _cts;
    private int _started;
    private readonly ClipboardSync _clipboard = new();

    /// <summary>Готовый кадр для показа (уже декодированный).</summary>
    public event Action<Bitmap>? FrameDecoded;

    /// <summary>Список мониторов хоста и активный номер.</summary>
    public event Action<IReadOnlyList<RemoteDisplay>, int>? DisplaysChanged;

    /// <summary>Монитор, на который надо переключиться сразу после подключения (CLI/UI).</summary>
    public int? PreferredDisplay { get; set; }

    public event Action<string>? StatusChanged;
    public event Action? Connected;

    /// <summary>Сессия завершилась (канал закрыт, сервер закончил или ошибка) — UI возвращается к стартовому экрану.</summary>
    public event Action? Ended;

    /// <summary>
    /// Спросить у пользователя пароль устройства (unattended-доступ).
    /// null — пользователь отказался.
    /// </summary>
    public Func<Task<PasswordReply?>>? PasswordPrompt { get; set; }

    public ViewerSession(
        SignalingClient client, string targetId, VideoCodec codec = VideoCodec.Mjpeg, string? password = null,
        AgentConfig? config = null)
    {
        _client = client;
        _targetId = targetId;
        _codec = codec;
        _password = password;
        _passwordFromCli = _password is not null;
        _config = config;
        _decoder = codec == VideoCodec.H264 ? new H264Decoder() : null;
    }

    /// <summary>Запускает цикл сессии; повторный вызов (например, из OnShown встроенной формы) игнорируется.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var request = await _client.RequestAsync(_targetId, ct);
            _sessionId = request.SessionId;
            StatusChanged?.Invoke($"заявка отправлена: {request.Target.Name} ({request.Target.PublicId})");

            while (!ct.IsCancellationRequested)
            {
                var poll = await _client.PollAsync(_sessionId, _cursor, ct);
                _cursor = poll.Cursor;

                foreach (var message in poll.Messages)
                {
                    await HandleSignalAsync(message, ct);
                }

                if (poll.Session.State == "active" && !_offerSent)
                {
                    await SendOfferAsync(ct);
                }

                if (poll.Session.State == "ended")
                {
                    StatusChanged?.Invoke("сессия завершена");
                    Ended?.Invoke();
                    break;
                }

                // текстовый буфер обмена — в обе стороны, пока канал открыт
                if (_peer is { IsConnected: true })
                {
                    var json = _clipboard.PollOutgoingJson();
                    if (json is not null)
                    {
                        _peer.SendControl(json);
                    }
                }

                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("ошибка: " + ex.Message);
            Log.Error(ex.Message);
            Ended?.Invoke();
        }
    }

    private async Task SendOfferAsync(CancellationToken ct)
    {
        var peer = new WebRtcPeer(offerer: true, _codec);
        _peer = peer;

        peer.LocalSignal += signal =>
            _ = _client.SignalAsync(_sessionId!, "ice", signal, CancellationToken.None);

        peer.FrameReceived += frame =>
        {
            _frameCount++;
            _rxBytes += frame.Length;

            if (_rxWatch.Elapsed.TotalSeconds >= 5)
            {
                Log.Info(
                    $"приём: {_frameCount} кадров, {_rxBytes / 1024.0 / 1024.0:F2} МБ за " +
                    $"{_rxWatch.Elapsed.TotalSeconds:F0} с ({_frameCount / _rxWatch.Elapsed.TotalSeconds:F1} fps)");

                _frameCount = 0;
                _rxBytes = 0;
                _rxWatch.Restart();
            }

            OnRemoteFrame(frame);
        };

        peer.Connected += () =>
        {
            SavePendingCredential();
            StatusChanged?.Invoke("канал установлен");
            Connected?.Invoke();
        };

        peer.ControlReceived += json =>
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var root = document.RootElement;

                if (!root.TryGetProperty("t", out var type))
                {
                    return;
                }

                switch (type.GetString())
                {
                    case "cb":
                        _clipboard.Incoming(root);
                        break;

                    case "displays":
                        HandleDisplays(root);
                        break;
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        };

        peer.Closed += reason =>
        {
            StatusChanged?.Invoke($"канал закрыт: {reason}");
            Ended?.Invoke();
        };

        var offer = (Dictionary<string, object?>)await peer.CreateOfferAsync(ct);
        offer["codec"] = _codec.ToWire();
        await _client.SignalAsync(_sessionId!, "offer", offer, ct);
        _offerSent = true;
        StatusChanged?.Invoke("offer отправлен, ждём ответ");
    }

    /// <summary>Хост прислал список мониторов: показываем кнопки переключения.</summary>
    private void HandleDisplays(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var list = new List<RemoteDisplay>();

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("i", out var index) ||
                !item.TryGetProperty("w", out var width) ||
                !item.TryGetProperty("h", out var height))
            {
                continue;
            }

            var primary = item.TryGetProperty("p", out var flag) && flag.ValueKind == JsonValueKind.True;
            list.Add(new RemoteDisplay(index.GetInt32(), width.GetInt32(), height.GetInt32(), primary));
        }

        var active = root.TryGetProperty("active", out var value) ? value.GetInt32() : 0;

        // «--display N»: один раз просим нужный монитор, дальше выбор за кнопками
        if (PreferredDisplay is int preferred && preferred != active)
        {
            PreferredDisplay = null;
            SelectDisplay(preferred);
            return;
        }

        DisplaysChanged?.Invoke(list, active);
    }

    /// <summary>Попросить хост переключиться на другой монитор.</summary>
    public void SelectDisplay(int index)
    {
        var peer = _peer;

        if (peer is null)
        {
            return;
        }

        try
        {
            peer.SendControl(JsonSerializer.Serialize(new { t = "mon", i = index }));
        }
        catch (Exception ex)
        {
            Log.Warn($"не удалось переключить монитор: {ex.Message}");
        }
    }

    /// <summary>Пришёл кадр от хоста: JPEG или H.264 access unit.</summary>
    private void OnRemoteFrame(byte[] frame)
    {
        Bitmap? bitmap;

        if (_decoder is not null)
        {
            try
            {
                bitmap = _decoder.Decode(frame);
            }
            catch (Exception ex)
            {
                Log.Warn($"h264: декодирование упало: {ex.Message}");
                bitmap = null;
            }

            // Кадр не собрался — просим IDR, но не чаще раза в 2 с.
            if (bitmap is null && DateTime.UtcNow - _lastKeyframeRequest > TimeSpan.FromSeconds(2))
            {
                _lastKeyframeRequest = DateTime.UtcNow;
                _peer?.SendControl("{\"t\":\"kf\"}");
            }
        }
        else
        {
            try
            {
                using var stream = new MemoryStream(frame);
                bitmap = new Bitmap(stream);
            }
            catch (ArgumentException)
            {
                bitmap = null;
            }
        }

        if (bitmap is not null)
        {
            FrameDecoded?.Invoke(bitmap);
        }
    }

    private async Task HandleSignalAsync(SignalMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case "answer":
                if (_peer is not null)
                {
                    await _peer.ApplyAnswerAsync(message.Payload, ct);
                    StatusChanged?.Invoke("answer получен, устанавливаем соединение");
                }
                break;

            case "ice":
                if (_peer is not null)
                {
                    await _peer.AddRemoteIceAsync(message.Payload, ct);
                }
                break;

            case "offer":
                // зритель всегда инициирует сам
                break;

            case "challenge":
                await AnswerChallengeAsync(message.Payload, ct);
                break;

            case "auth_failed":
                // пароль из CLI не переспрашиваем — пусть хост добьёт попытки до отказа;
                // введённый вручную забываем, чтобы спросить заново (хост пришлёт новый challenge)
                if (!_passwordFromCli)
                {
                    _password = null;
                }

                Log.Warn("неверный пароль устройства");
                StatusChanged?.Invoke("неверный пароль устройства");
                break;
        }
    }

    /// <summary>
    /// Хост прислал challenge — отвечаем PBKDF2(пароль → хеш, nonce).
    /// Пароль берём из CLI либо спрашиваем у пользователя; сам пароль по сети не идёт.
    /// </summary>
    private async Task AnswerChallengeAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("nonce", out var nonceValue) ||
            !payload.TryGetProperty("salt", out var saltValue) ||
            !payload.TryGetProperty("iterations", out var iterationsValue))
        {
            StatusChanged?.Invoke("устройство прислало некорректный запрос пароля");
            return;
        }

        var nonce = nonceValue.GetString();
        var salt = saltValue.GetString();
        var iterations = iterationsValue.GetInt32();

        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(salt))
        {
            return;
        }

        try
        {
            // Сохранённый ранее хеш (галка «запомнить») — спрашивать нечего.
            if (_config?.TryGetSavedSecret(_targetId, salt) is { } saved)
            {
                var savedProof = UnattendedAuth.ComputeProof(saved, nonce, iterations);
                await _client.SignalAsync(_sessionId!, "auth", new { proof = savedProof }, ct);
                Log.Info("пароль взят из сохранённых для этого устройства");
                StatusChanged?.Invoke("вход по сохранённому паролю");
                return;
            }

            if (string.IsNullOrEmpty(_password) && PasswordPrompt is not null)
            {
                Log.Info("устройство требует пароль — спрашиваю у пользователя");
                StatusChanged?.Invoke("устройство требует пароль");

                var reply = await PasswordPrompt();
                _password = reply?.Password;

                if (reply is { Remember: true } && !string.IsNullOrEmpty(reply.Password))
                {
                    // Сохраним только после успешного подключения (см. SavePendingCredential).
                    _pendingCredential = (
                        salt,
                        iterations,
                        UnattendedAuth.Derive(reply.Password, Convert.FromBase64String(salt), iterations));
                }
            }

            if (string.IsNullOrEmpty(_password))
            {
                Log.Warn(PasswordPrompt is null
                    ? "пароль устройства не введён — в этом режиме его негде спросить (нужен --password)"
                    : "пароль устройства не введён — диалог закрыт без ввода");

                StatusChanged?.Invoke("нет пароля устройства");
                await RejectAsync(ct);
                return;
            }

            var hash = UnattendedAuth.Derive(_password!, Convert.FromBase64String(salt), iterations);
            var proof = UnattendedAuth.ComputeProof(hash, nonce, iterations);

            await _client.SignalAsync(_sessionId!, "auth", new { proof }, ct);
            Log.Info("пароль отправлен, ждём подтверждения");
            StatusChanged?.Invoke("пароль отправлен, ждём подтверждения");
        }
        catch (FormatException)
        {
            StatusChanged?.Invoke("устройство прислало некорректный запрос пароля");
        }
    }

    /// <summary>Сохраняет пароль в профиль — только после того, как хост его принял.</summary>
    private void SavePendingCredential()
    {
        if (_config is null || _pendingCredential is not { } credential)
        {
            return;
        }

        _config.RememberSavedSecret(_targetId, credential.Salt, credential.Iterations, credential.Hash);
        _config.Save();
        _pendingCredential = null;

        Log.Info($"пароль устройства сохранён для {_targetId}");
    }

    /// <summary>Отказаться от сессии (нет пароля, отмена ввода) без исключений наружу.</summary>
    private async Task RejectAsync(CancellationToken ct)
    {
        try
        {
            if (_sessionId is not null)
            {
                // Инициатор не может reject (это право принимающей стороны) — он закрывает заявку через end.
                await _client.AnswerAsync(_sessionId, "end", ct);
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Log.Warn($"заявка не закрыта: {ex.Message}");
        }

        Ended?.Invoke();
    }

    public void SendInput(string json) => _peer?.SendControl(json);

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        if (_peer is not null)
        {
            await _peer.DisposeAsync();
            _peer = null;
        }

        _decoder?.Dispose();
        _decoder = null;
    }
}
