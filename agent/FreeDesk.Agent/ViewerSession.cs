using System.Drawing;
using System.Text.Json;
using FreeDesk.Agent.Codec;
using FreeDesk.Agent.Files;

namespace FreeDesk.Agent;

/// <summary>Монитор на стороне хоста (для кнопок переключения).</summary>
public sealed record RemoteDisplay(int Index, int Width, int Height, bool Primary);

/// <summary>Элемент удалённого каталога (для выбора файла к скачиванию).</summary>
public sealed record RemoteEntry(string Name, bool IsDirectory, long Size = 0, string? FullName = null)
{
    public override string ToString() => (IsDirectory ? "[ ] " : "    ") + Name;
}

/// <summary>Ответ хоста на листинг каталога (Desktop — путь к его рабочему столу).</summary>
public sealed record RemoteListing(string Path, string? Parent, IReadOnlyList<RemoteEntry> Entries, string? Error, string? Desktop = null);

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
    private long _totalFrames;
    private bool _noFramesWarned;
    private FileSender? _fileSender;
    private FileReceiver? _fileReceiver;
    private TaskCompletionSource<RemoteListing?>? _browse;
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

    // ── резервный канал (кадры и ввод через сервер) ──
    private bool _relayActive;
    private int _relaySeq;
    private DateTime _lastFrameAt = DateTime.MinValue;

    /// <summary>Принудительно включить резервный канал (для проверки и для упрямых NAT).</summary>
    public bool ForceRelay { get; set; }
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
                var poll = await _client.PollAsync(_sessionId, _cursor, ct, waitSeconds: 8);
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
        catch (ApiException ex)
        {
            // Технические коды сервера — в понятные человеку причины.
            var reason = ex.Code switch
            {
                "not_found" => "устройство не найдено",
                "offline" => "устройство не в сети",
                "busy" => "устройство занято",
                "unauthorized" or "forbidden" => "сервер отклонил запрос",
                _ => "сервер: " + ex.Message,
            };

            StatusChanged?.Invoke("ошибка: " + reason);
            Log.Error(ex.Message);
            Ended?.Invoke();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("ошибка: " + ex.Message);
            Log.Error(ex.Message);
            Ended?.Invoke();
        }
    }

    /// <summary>Работает ли резервный канал (передача файлов по data channel недоступна).</summary>
    public bool RelayActive => _relayActive;

    /// <summary>ID хоста, к которому подключены.</summary>
    public string TargetId => _targetId;

    /// <summary>Жив ли прямой data channel (для него и для передачи файлов).</summary>
    public bool IsConnected => _peer is { IsConnected: true };

    /// <summary>Прогресс отправки файла на хост: имя, отправлено, всего.</summary>
    public event Action<string, long, long>? UploadProgress;

    /// <summary>Прогресс приёма файла с хоста: id, имя, принято, всего, путь (когда готов).</summary>
    public event Action<int, string, long, long, string?>? DownloadProgress;

    private async Task SendOfferAsync(CancellationToken ct)
    {
        var peer = new WebRtcPeer(offerer: true, _codec);
        _peer = peer;

        _fileReceiver = new FileReceiver();
        _fileSender = new FileSender(peer, text => StatusChanged?.Invoke(text));

        _fileSender.SendProgress += (name, sent, total) => UploadProgress?.Invoke(name, sent, total);
        _fileReceiver.Progress += (id, name, written, total, path) => DownloadProgress?.Invoke(id, name, written, total, path);

        peer.BinaryReceived += data =>
        {
            if (_fileReceiver is null || !FileWire.TryUnframe(data, out var transferId, out var chunk))
            {
                return;
            }

            var received = _fileReceiver.Write(transferId, chunk);

            if (received is not null)
            {
                Log.Info($"файл получен: {received.Path} ({received.Size / 1024} КБ)");
                StatusChanged?.Invoke($"получен файл: {received.Name}");
            }
        };

        peer.LocalSignal += signal =>
            _ = _client.SignalAsync(_sessionId!, "ice", signal, CancellationToken.None);

        peer.FrameReceived += frame =>
        {
            _frameCount++;
            _rxBytes += frame.Length;

            if (_rxWatch.Elapsed.TotalSeconds >= 5)
            {
                var seconds = Math.Max(0.1, _rxWatch.Elapsed.TotalSeconds);
                var fps = Math.Round(_frameCount / seconds, 1);
                var kbps = Math.Round(_rxBytes * 8 / 1000.0 / seconds);

                Log.Info(
                    $"приём: {_frameCount} кадров, {_rxBytes / 1024.0 / 1024.0:F2} МБ за " +
                    $"{_rxWatch.Elapsed.TotalSeconds:F0} с ({_frameCount / _rxWatch.Elapsed.TotalSeconds:F1} fps)");

                // Обратная связь по каналу: хост подстроит битрейт под реальный приём.
                try
                {
                    _peer?.SendControl(JsonSerializer.Serialize(new { t = "stat", fps, kbps }));
                }
                catch (Exception ex)
                {
                    Log.Warn($"статистика не отправлена: {ex.Message}");
                }

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

            _ = WarnIfNoFramesAsync();
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

                    case "file":
                        // сначала — ответ на нашу отправку, иначе — входящее предложение
                        if (_fileSender?.HandleControl(root) != true)
                        {
                            HandleIncomingFile(root);
                        }

                        break;
                }
            }
            catch (System.Text.Json.JsonException)
            {
            }
        };

        peer.Closed += reason =>
        {
            // Релей живёт независимо от WebRTC: падение ICE не должно убивать
            // сессию, в которой уже работает (или вот-вот включится) резервный
            // канал, и не должно пугать статусом «канал закрыт».
            if (_relayActive)
            {
                Log.Info("прямое соединение закрыто, продолжаем через резервный канал");
                StatusChanged?.Invoke("прямое соединение не удалось — работаем через сервер");
                return;
            }

            StatusChanged?.Invoke($"канал закрыт: {reason}");

            // Сторож ещё мог не успеть включить релей — даём ему 4 секунды.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);

                    if (!_relayActive)
                    {
                        Ended?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, ct);
        };

        var offer = (Dictionary<string, object?>)await peer.CreateOfferAsync(ct);
        offer["codec"] = _codec.ToWire();
        await _client.SignalAsync(_sessionId!, "offer", offer, ct);
        _offerSent = true;
        StatusChanged?.Invoke("offer отправлен, ждём ответ");

        // Если P2P не сложится — через 8 секунд поднимаем резервный канал.
        _ = Task.Run(() => RelayWatchdogAsync(ct), ct);
    }

    /// <summary>
    /// Сторож: если кадры не пошли (NAT не пропустил прямое соединение),
    /// переключаемся на доставку через сервер.
    /// </summary>
    private async Task RelayWatchdogAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ForceRelay ? 1 : 8);

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);

                if (_lastFrameAt > DateTime.UtcNow.AddSeconds(-2))
                {
                    return; // кадры идут — резерв не нужен
                }
            }

            if (_lastFrameAt > DateTime.UtcNow.AddSeconds(-2))
            {
                return;
            }

            await EnableRelayAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"резервный канал: {ex.Message}");
        }
    }

    /// <summary>Включить резервный канал: попросить хост гнать кадры через сервер и принимать их.</summary>
    private async Task EnableRelayAsync(CancellationToken ct)
    {
        if (_relayActive || _sessionId is null)
        {
            return;
        }

        _relayActive = true;
        await _client.SignalAsync(_sessionId, "relay", new { on = true }, ct);
        StatusChanged?.Invoke("прямое соединение не прошло — работаем через сервер (медленнее)");
        Log.Info("резервный канал: включён");

        _ = Task.Run(() => RelayReceiveLoopAsync(ct), ct);
    }

    private async Task RelayReceiveLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        var received = 0;
        var dead = false;
        var startedAt = DateTime.UtcNow;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested && _relayActive && _sessionId is not null)
        {
            try
            {
                // Один запрос на цикл: сервер сам скажет, новый ли кадр.
                var (jpeg, seq) = await _client.RelayFrameAsync(_sessionId, _relaySeq, ct);

                if (jpeg is { Length: > 0 })
                {
                    using var stream = new MemoryStream(jpeg);
                    using var image = Image.FromStream(stream);
                    var bitmap = new Bitmap(image);

                    _relaySeq = seq;
                    _lastFrameAt = DateTime.UtcNow;
                    _totalFrames++;
                    FrameDecoded?.Invoke(bitmap);

                    received++;

                    if (received == 1)
                    {
                        Log.Info($"резервный кадр: {bitmap.Width}x{bitmap.Height}");

                        // Кадры релея = сессия состоялась: поднимаем «Connected»,
                        // иначе окно просмотра так и не создаётся (ConnectAsync
                        // ждёт именно этого события) и вкладка висит жёлтой.
                        StatusChanged?.Invoke("подключено через резервный канал");
                        Connected?.Invoke();
                    }

                    if (watch.Elapsed.TotalSeconds >= 10)
                    {
                        Log.Info($"резервный канал: получено {received / watch.Elapsed.TotalSeconds:F1} fps");
                        received = 0;
                        watch.Restart();
                    }
                }
                else if (_lastFrameAt < DateTime.UtcNow.AddSeconds(-15)
                         && (DateTime.UtcNow - startedAt).TotalSeconds > 15)
                {
                    // Релей включён, но хост не гонит кадры (старая сборка,
                    // упал, выключился) — не висим вечно на чёрном экране.
                    StatusChanged?.Invoke("резервный канал не отвечает");
                    Log.Warn("резервный канал: кадров нет 15 секунд");
                    dead = true;
                    break;
                }

                failures = 0;

                // Темп задаёт long-poll на сервере: вернулся сразу — есть новый кадр,
                // подождал 5 секунд — экран не менялся, запрашиваем снова.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Warn($"резервный канал: {ex.Message}");

                if (failures >= 5)
                {
                    StatusChanged?.Invoke("резервный канал не отвечает");
                    dead = true;
                    break;
                }

                await Task.Delay(1000, ct);
            }
        }

        if (dead)
        {
            await EndOnServerAsync();
            Ended?.Invoke();
        }
    }

    /// <summary>Сообщить серверу, что сессия окончена (хост в релей-режиме узнаёт
    /// о закрытии только отсюда — мёртвый P2P ему ничего не скажет).</summary>
    private async Task EndOnServerAsync()
    {
        if (_sessionId is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _client.AnswerAsync(_sessionId, "end", timeout.Token);
        }
        catch (Exception ex)
        {
            Log.Warn($"сессия не закрыта на сервере: {ex.Message}");
        }
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

    /// <summary>Отправить файл на удалённую машину (targetDir — открытая там папка).</summary>
    public Task SendFileAsync(string path, string? targetDir = null, CancellationToken ct = default) =>
        _fileSender?.SendAsync(path, targetDir, ct) ?? Task.CompletedTask;

    /// <summary>Список каталога на хосте — для диалога выбора файла.</summary>
    public async Task<RemoteListing?> BrowseAsync(string? path, CancellationToken ct = default)
    {
        var peer = _peer;

        if (peer is null || !peer.IsConnected)
        {
            return null;
        }

        var tcs = new TaskCompletionSource<RemoteListing?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _browse = tcs;

        peer.SendControl(JsonSerializer.Serialize(new { t = "file", op = "browse", path }));

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_browse, tcs))
            {
                _browse = null;
            }
        }
    }

    private void HandleListing(JsonElement root)
    {
        var path = root.TryGetProperty("path", out var pathValue) ? pathValue.GetString() ?? "" : "";
        var parent = root.TryGetProperty("parent", out var parentValue) && parentValue.ValueKind == JsonValueKind.String
            ? parentValue.GetString()
            : null;
        var error = root.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.String
            ? errorValue.GetString()
            : null;
        var desktop = root.TryGetProperty("desktop", out var desktopValue) && desktopValue.ValueKind == JsonValueKind.String
            ? desktopValue.GetString()
            : null;

        var entries = new List<RemoteEntry>();

        if (root.TryGetProperty("entries", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var name = item.TryGetProperty("n", out var nameValue) ? nameValue.GetString() : null;

                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var isDirectory = item.TryGetProperty("d", out var dirValue) && dirValue.ValueKind == JsonValueKind.True;
                var size = item.TryGetProperty("s", out var sizeValue) && sizeValue.TryGetInt64(out var parsed) ? parsed : 0;
                var fullName = item.TryGetProperty("p", out var entryPath) && entryPath.ValueKind == JsonValueKind.String
                    ? entryPath.GetString()
                    : null;
                entries.Add(new RemoteEntry(name, isDirectory, size, fullName));
            }
        }

        _browse?.TrySetResult(new RemoteListing(path, parent, entries, error, desktop));
    }

    /// <summary>Попросить у хоста файл по пути (saveDir — куда положить у себя).</summary>
    public void RequestDownload(string remotePath, string? saveDir = null)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return;
        }

        var peer = _peer;

        if (peer is null || !peer.IsConnected)
        {
            StatusChanged?.Invoke("нет соединения");
            return;
        }

        StatusChanged?.Invoke($"запрашиваю файл: {Path.GetFileName(remotePath)}");

        object request = string.IsNullOrWhiteSpace(saveDir)
            ? (object) new { t = "file", op = "download", path = remotePath }
            : new { t = "file", op = "download", path = remotePath, dir = saveDir };

        peer.SendControl(JsonSerializer.Serialize(request));
    }

    /// <summary>Прервать входящую передачу и сказать хосту, чтобы тот остановился.</summary>
    public void AbortIncoming(int id)
    {
        _fileReceiver?.Abort(id);

        var peer = _peer;

        if (peer is not null && peer.IsConnected)
        {
            peer.SendControl(JsonSerializer.Serialize(new { t = "file", op = "cancel", id }));
        }
    }

    /// <summary>Хост предложил файл — принимаем и пишем в Downloads\FreeDesk.</summary>
    private void HandleIncomingFile(JsonElement root)
    {
        var op = root.TryGetProperty("op", out var opValue) ? opValue.GetString() : null;
        var id = root.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var parsed) ? parsed : 0;

        switch (op)
        {
            case "offer":
                var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "file" : "file";
                var size = root.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                var incomingDir = root.TryGetProperty("dir", out var incomingDirValue) && incomingDirValue.ValueKind == JsonValueKind.String
                    ? incomingDirValue.GetString()
                    : null;

                string? startError = null;
                var started = _fileReceiver is not null && _fileReceiver.TryStart(id, name, size, out startError, incomingDir);

                if (started)
                {
                    _peer?.SendControl(JsonSerializer.Serialize(new { t = "file", op = "accept", id }));
                    StatusChanged?.Invoke($"получение файла: {name}");
                }
                else
                {
                    var rejectReason = startError ?? "приём недоступен";
                    _peer?.SendControl(JsonSerializer.Serialize(new { t = "file", op = "reject", id, reason = rejectReason }));
                    Log.Warn($"файл отклонён: {rejectReason}");
                }

                break;

            case "done":
                Log.Info($"передача файла {id} завершена");
                break;

            case "list":
                HandleListing(root);
                break;

            case "cancel":
                _fileReceiver?.Abort(id);
                Log.Warn($"передача файла {id} отменена");
                break;

            case "error":
                var errorText = root.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
                Log.Warn($"файл не отправлен: {errorText}");
                StatusChanged?.Invoke(errorText ?? "удалённая сторона не смогла отдать файл");
                break;
        }
    }

    /// <summary>Подключились, но кадров нет — подсказываем, где искать причину.</summary>
    private async Task WarnIfNoFramesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));

        if (_totalFrames > 0 || _noFramesWarned)
        {
            return;
        }

        _noFramesWarned = true;
        StatusChanged?.Invoke("кадры не приходят: проверьте openh264.dll на хосте и RDP-сессию");
        Log.Warn("за 6 секунд не пришло ни одного кадра");
    }

    /// <summary>Пришёл кадр от хоста: JPEG или H.264 access unit.</summary>
    private void OnRemoteFrame(byte[] frame)
    {
        _totalFrames++;

        // Байты идут — сторож релея видит, что прямое соединение живо.
        _lastFrameAt = DateTime.UtcNow;
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
                    : "пароль устройства не введён — ждём решения владельца");

                // Не отклоняем: владелец на хосте мог получить окно «пустить/не пустить».
                // Отклонит — poll увидит ended; разрешит — придёт offer и подключимся.
                StatusChanged?.Invoke("ожидание решения владельца…");
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

    public void SendInput(string json)
    {
        if (_relayActive && _sessionId is not null)
        {
            // Резервный канал: ввод идёт тем же HTTPS-путём, что и кадры.
            _ = _client.RelayControlPushAsync(_sessionId, json, _cts?.Token ?? CancellationToken.None)
                .ContinueWith(t => Log.Warn($"резервный ввод: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
            return;
        }

        _peer?.SendControl(json);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        // В релей-режиме мёртвый P2P не скажет хосту, что мы ушли, — говорим сами.
        await EndOnServerAsync();

        if (_peer is not null)
        {
            await _peer.DisposeAsync();
            _peer = null;
        }

        _decoder?.Dispose();
        _decoder = null;

        _fileReceiver?.Dispose();
        _fileReceiver = null;
    }
}
