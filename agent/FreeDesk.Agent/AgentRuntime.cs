using System.Collections.Concurrent;
using System.Text.Json;
using FreeDesk.Agent.Codec;
using FreeDesk.Agent.Files;
using FreeDesk.Agent.Input;

namespace FreeDesk.Agent;

/// <summary>
/// Основной цикл агента: держит устройство онлайн, принимает заявки, поднимает
/// WebRTC-сессию и стримит экран, пока зритель подключён.
/// </summary>
public sealed class AgentRuntime
{
    private readonly AgentConfig _config;
    private readonly SignalingClient _client;
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();
    private readonly Dictionary<string, IncomingItem> _pending = new();
    private readonly Dictionary<string, PendingAuth> _auth = new();
    private DateTime _lastPresence = DateTime.MinValue;
    private readonly HashSet<string> _prompted = new();

    /// <summary>Вызывается при входящей заявке без пароля. Верните true для принятия.</summary>
    public Func<IncomingItem, Task<bool>>? AcceptPrompt { get; set; }

    /// <summary>Текстовый статус для UI (подключение/отключение).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Сессия началась (хост отдаёт экран) — UI может свернуться.</summary>
    public event Action? SessionStarted;

    /// <summary>Сессия закончилась — UI восстанавливается.</summary>
    public event Action? SessionEnded;

    /// <summary>Ход передачи файлов в сессии: имя, передано байт, всего байт.
    /// Пустое имя — передач закончилась, индикатор можно спрятать.</summary>
    public event Action<string, long, long>? FileTransferActivity;

    /// <summary>Файл, который надо отдать зрителю сразу после подключения (--send-file).</summary>
    public string? AutoSendFile { get; set; }

    public AgentRuntime(AgentConfig config, SignalingClient client)
    {
        _config = config;
        _client = client;
    }

    public async Task RunAsync(string? connectTo, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(connectTo))
        {
            await ConnectAsync(connectTo.Trim(), ct);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PresenceIfDueAsync(ct);
                await HandleIncomingAsync(ct);
                await HandleAuthAsync(ct);
                await HandleKeyPressesAsync(ct);
                await PollSessionsAsync(ct);
                SyncClipboards();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ApiException ex)
            {
                Log.Warn(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                Log.Warn($"сеть недоступна: {ex.Message}");
            }

            try
            {
                // 2 секунды вместо 1: вместе с long-poll сессии это даёт ~30 запросов в минуту
                // вместо 120, а заявка всё равно замечается за пару секунд.
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _config.PollSeconds)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var session in _sessions.Values.ToList())
        {
            await CloseSessionAsync(session);
        }
    }

    public async Task ConnectAsync(string targetId, CancellationToken ct)
    {
        var result = await _client.RequestAsync(targetId, ct);
        Log.Info($"заявка отправлена: {result.Target.Name} ({result.Target.PublicId}), сессия {result.SessionId}");

        var session = new ActiveSession
        {
            Id = result.SessionId,
            Role = "host",
            PeerName = result.Target.Name,
        };

        // Инициатор — он же оффер: экран показывает сторона, которая приняла заявку.
        AttachTransport(session, offerer: true, VideoCodecNames.Parse(_config.Codec));
        _sessions[result.SessionId] = session;
    }

    private async Task PresenceIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastPresence < TimeSpan.FromSeconds(_config.PresenceSeconds))
        {
            return;
        }

        await _client.PresenceAsync(ct);
        _lastPresence = DateTime.UtcNow;
    }

    private async Task HandleIncomingAsync(CancellationToken ct)
    {
        var inbox = await _client.IncomingAsync(ct);
        var seen = new HashSet<string>();

        foreach (var item in inbox.Incoming)
        {
            seen.Add(item.SessionId);

            if (_sessions.ContainsKey(item.SessionId) || _pending.ContainsKey(item.SessionId) ||
                _auth.ContainsKey(item.SessionId))
            {
                continue;
            }

            // Пароль устройства и подтверждение владельца работают параллельно:
            // зритель входит и по паролю (challenge), и по согласию человека —
            // кто решит первым, тот и пускает.
            if (_config.GetUnattendedSecret() is { } secret)
            {
                await SendChallengeAsync(item, secret, ct);
                _ = PromptOwnerForAsync(item);
                continue;
            }

            if (AcceptPrompt is not null)
            {
                if (_prompted.Contains(item.SessionId))
                {
                    Log.Info($"повторный запрос проигнорирован: {item.SessionId}");
                    continue;
                }

                _prompted.Add(item.SessionId);
                Log.Info($"запрос подтверждения: {item.From.Name} ({item.SessionId})");

                var accept = await AcceptPrompt(item);
                if (accept)
                {
                    await AcceptAsync(item, ct);
                }
                else
                {
                    await _client.AnswerAsync(item.SessionId, "reject", ct);
                }

                continue;
            }

            _pending[item.SessionId] = item;
            Log.Info($"входящий запрос: {item.From.Name} ({item.From.PublicId}) — Enter принять, R отклонить");
        }

        foreach (var stale in _pending.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _pending.Remove(stale);
        }

        foreach (var stale in _auth.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _auth.Remove(stale);
        }
    }

    /// <summary>Отправляет зрителю challenge: nonce + параметры PBKDF2.</summary>
    private async Task SendChallengeAsync(IncomingItem item, UnattendedAuth.Secret secret, CancellationToken ct)
    {
        var auth = new PendingAuth
        {
            Item = item,
            Nonce = UnattendedAuth.NewNonce(),
            ExpiresAt = DateTime.UtcNow.AddSeconds(60),
        };

        _auth[item.SessionId] = auth;
        await SendChallengeMessageAsync(item.SessionId, auth, secret, ct);

        Log.Info($"запрос пароля устройства: {item.From.Name} ({item.From.PublicId})");
    }

    private async Task SendChallengeMessageAsync(
        string sessionId, PendingAuth auth, UnattendedAuth.Secret secret, CancellationToken ct)
    {
        await _client.SignalAsync(sessionId, "challenge", new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["nonce"] = auth.Nonce,
            ["salt"] = Convert.ToBase64String(secret.Salt),
            ["iterations"] = secret.Iterations,
        }, ct);
    }

    /// <summary>
    /// Параллельно с challenge спрашивает владельца «пустить/не пустить»:
    /// сессия принимается тем путём, который сработает первым.
    /// </summary>
    private async Task PromptOwnerForAsync(IncomingItem item)
    {
        try
        {
            if (AcceptPrompt is null || !_prompted.Add(item.SessionId))
            {
                return;
            }

            Log.Info($"запрос подтверждения: {item.From.Name} ({item.SessionId})");

            var accept = await AcceptPrompt(item);

            // пока окно было открыто, сессию мог принять пароль
            if (_sessions.ContainsKey(item.SessionId))
            {
                return;
            }

            _auth.Remove(item.SessionId);
            _prompted.Remove(item.SessionId);

            if (accept)
            {
                await AcceptAsync(item, CancellationToken.None);
            }
            else
            {
                Log.Info($"отклонено владельцем: {item.From.Name} ({item.From.PublicId})");
                await TryAnswerAsync(item.SessionId, "reject", CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"диалог подтверждения: {ex.Message}");
        }
    }

    /// <summary>Ждёт ответ на challenge; проверяет proof и принимает сессию сама.</summary>
    private async Task HandleAuthAsync(CancellationToken ct)
    {
        foreach (var (sessionId, auth) in _auth.ToList())
        {
            if (DateTime.UtcNow > auth.ExpiresAt)
            {
                _auth.Remove(sessionId);

                // Диалог владельца ещё открыт — сессию не рвём: примет владелец.
                if (_prompted.Contains(sessionId))
                {
                    Log.Info($"пароль не введён, ждём решения владельца: {auth.Item.From.Name}");
                    continue;
                }

                Log.Warn($"пароль не введён за минуту: {auth.Item.From.Name}");
                await TryAnswerAsync(sessionId, "reject", ct);
                continue;
            }

            var poll = await _client.PollAsync(sessionId, auth.Cursor, ct);
            auth.Cursor = poll.Cursor;

            if (poll.Session.State == "ended")
            {
                _auth.Remove(sessionId);
                continue;
            }

            foreach (var message in poll.Messages)
            {
                if (message.Type != "auth")
                {
                    continue;
                }

                var proof = message.Payload.ValueKind == JsonValueKind.Object &&
                            message.Payload.TryGetProperty("proof", out var value)
                    ? value.GetString()
                    : null;

                var secret = _config.GetUnattendedSecret();
                var accepted = secret is not null && proof is not null &&
                               UnattendedAuth.Verify(secret, auth.Nonce, proof);

                if (accepted)
                {
                    _auth.Remove(sessionId);
                    Log.Info($"пароль принят: {auth.Item.From.Name} ({auth.Item.From.PublicId})");
                    await AcceptAsync(auth.Item, ct);
                    break;
                }

                auth.Attempts++;
                Log.Warn($"неверный пароль ({auth.Attempts}/3): {auth.Item.From.Name}");

                if (auth.Attempts >= 3)
                {
                    _auth.Remove(sessionId);
                    await TryAnswerAsync(sessionId, "reject", ct);
                    break;
                }

                // даём ещё попытку: новый nonce, чтобы старый proof не переиспользовали
                auth.Nonce = UnattendedAuth.NewNonce();
                auth.ExpiresAt = DateTime.UtcNow.AddSeconds(60);

                await _client.SignalAsync(sessionId, "auth_failed", new { attempts = auth.Attempts }, ct);

                if (_config.GetUnattendedSecret() is { } nextSecret)
                {
                    await SendChallengeMessageAsync(sessionId, auth, nextSecret, ct);
                }
            }
        }
    }

    /// <summary>Ответ на заявку, когда сессия могла уже закрыться на сервере.</summary>
    private async Task TryAnswerAsync(string sessionId, string action, CancellationToken ct)
    {
        try
        {
            await _client.AnswerAsync(sessionId, action, ct);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Log.Warn($"сессия {sessionId}: {action} не отправлен ({ex.Message})");
        }
    }

    private async Task HandleKeyPressesAsync(CancellationToken ct)
    {
        if (AcceptPrompt is not null)
        {
            return;
        }

        if (Console.IsInputRedirected || _pending.Count == 0)
        {
            return;
        }

        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                foreach (var item in _pending.Values.ToList())
                {
                    await AcceptAsync(item, ct);
                }

                _pending.Clear();
                return;
            }

            if (key.Key == ConsoleKey.R)
            {
                foreach (var item in _pending.Values.ToList())
                {
                    await _client.AnswerAsync(item.SessionId, "reject", ct);
                    Log.Info($"отклонено: {item.From.Name} ({item.From.PublicId})");
                }

                _pending.Clear();
                return;
            }
        }
    }

    private async Task AcceptAsync(IncomingItem item, CancellationToken ct)
    {
        // мог быть принят параллельным путём (пароль или владелец)
        if (_sessions.ContainsKey(item.SessionId))
        {
            return;
        }

        // заглушка сразу: между answer и заполнением сессии нет зазора для
        // повторного прихода той же заявки из HandleIncoming
        _sessions[item.SessionId] = new ActiveSession
        {
            Id = item.SessionId,
            Role = "guest",
            PeerName = item.From.Name,
            State = "active",
        };

        _auth.Remove(item.SessionId);
        _prompted.Remove(item.SessionId);

        await _client.AnswerAsync(item.SessionId, "accept", ct);
        Log.Info($"подключение принято: {item.From.Name} ({item.From.PublicId}), сессия {item.SessionId}");

        // Транспорт создаём при получении offer: только тогда известен кодек зрителя
        // (его трек должен совпасть с нашей m-line).
        _sessions[item.SessionId] = new ActiveSession
        {
            Id = item.SessionId,
            Role = "guest",
            PeerName = item.From.Name,
            State = "active",
        };

        var accepted = _sessions[item.SessionId];
        _ = Task.Run(() => PollSessionLoopAsync(accepted, ct));
    }

    private void AttachTransport(ActiveSession session, bool offerer, VideoCodec codec)
    {
        var peer = new WebRtcPeer(offerer, codec);
        session.Transport = peer;
        session.Sender = new FileSender(peer, text => StatusChanged?.Invoke(text));

        // Хост видит передачу файлов у себя: и то, что забирает зритель,
        // и то, что ему привозят, — процесс не должен идти вскрытую.
        session.Sender.SendProgress += (name, sent, total) =>
            FileTransferActivity?.Invoke(name, sent, total);
        session.Files.Progress += (_, name, written, total, _) =>
            FileTransferActivity?.Invoke(name, written, total);

        peer.LocalSignal += signal =>
            _ = _client.SignalAsync(session.Id, "ice", signal, CancellationToken.None);

        peer.Connected += () =>
        {
            if (offerer)
            {
                // Сторона зрителя: экран не отдаёт, только принимает кадры.
                Log.Info($"канал установлен: {session.PeerName}");
                return;
            }
            Log.Info($"канал установлен: {session.PeerName}");
            session.Streamer = new ScreenStreamer(
                peer, _config.Fps, _config.JpegQuality, _config.MaxWidth, _config.Capture, _config.BitrateKbps,
                _config.Display, session.DataChannelVideo);
            session.Streamer.Start();
            StatusChanged?.Invoke($"Подключено: {session.PeerName}");
            SessionStarted?.Invoke();

            // --send-file на стороне хоста: отдать файл подключившемуся зрителю
            if (!string.IsNullOrEmpty(AutoSendFile))
            {
                var path = AutoSendFile;
                _ = Task.Run(() => SendFileToViewerAsync(path));
            }
        };

        peer.ControlReceived += json =>
        {
            if (offerer)
            {
                return;
            }

            if (session.InputCount < 5)
            {
                session.InputCount++;
                Log.Info($"ввод: {json}");
            }

            HandleControl(session, json);
        };

        // Чанки файлов приходят бинарём: пишем на диск без разбора JSON.
        peer.BinaryReceived += data =>
        {
            if (!FileWire.TryUnframe(data, out var transferId, out var chunk))
            {
                return;
            }

            var file = session.Files.Write(transferId, chunk);

            if (file is not null)
            {
                Log.Info($"файл получен: {file.Path} ({file.Size / 1024} КБ)");
                StatusChanged?.Invoke($"Получен файл: {file.Name}");
            }
        };

        peer.Closed += reason =>
        {
            Log.Info($"канал закрыт ({session.PeerName}): {reason}");

            // Релей живёт независимо от WebRTC: если он работает, сессию не гасим —
            // падение ICE не должно убивать работающий резервный канал.
            // Конец сессии придёт от зрителя (state=ended) или из цикла опроса.
            if (session.Relay is not null)
            {
                Log.Info("продолжаем через резервный канал");
                return;
            }

            _ = Task.Run(() => EndSessionAsync(session));
        };
    }

    /// <summary>Канал оборвался — гасим стример и сообщаем серверу, что сессия закончена.</summary>
    private async Task EndSessionAsync(ActiveSession session)
    {
        if (Interlocked.Exchange(ref session.EndedFlag, 1) == 1)
        {
            return;
        }

        await CloseSessionAsync(session);
        _sessions.TryRemove(session.Id, out _);

        StatusChanged?.Invoke("Онлайн. Ожидание подключений...");
        SessionEnded?.Invoke();

        try
        {
            await _client.AnswerAsync(session.Id, "end", CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"сессия не закрыта на сервере: {ex.Message}");
        }
    }

    private async Task PollSessionsAsync(CancellationToken ct)
    {
        // Оставлено для совместимости: сейчас каждая сессия опрашивается своей задачей
        // (PollSessionLoopAsync), чтобы long-poll одной не тормозил приём заявок.
        await Task.CompletedTask;
    }

    /// <summary>
    /// Свой цикл на сессию: long-poll 8 с — сигналы приходят сразу,
    /// но сервер не долбится пустыми запросами.
    /// </summary>
    private async Task PollSessionLoopAsync(ActiveSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _sessions.ContainsKey(session.Id))
            {
                PollResult poll;

                try
                {
                    poll = await _client.PollAsync(session.Id, session.Cursor, ct, waitSeconds: 8);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ApiException ex) when (ex.Code is "forbidden" or "not_found" or "session_ended")
                {
                    Log.Info($"сессия закрыта сервером: {session.PeerName}");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"poll {session.PeerName}: {ex.Message}");
                    await Task.Delay(2000, ct).ContinueWith(_ => { });
                    continue;
                }

                session.Cursor = poll.Cursor;
                session.State = poll.Session.State;

                foreach (var message in poll.Messages)
                {
                    await HandleSignalAsync(session, message, ct);
                }

                if (session.State == "active" && session.Role == "host" && !session.OfferSent && session.Transport is not null)
                {
                    var offer = await session.Transport.CreateOfferAsync(ct);
                    await _client.SignalAsync(session.Id, "offer", offer, ct);
                    session.OfferSent = true;
                    Log.Info($"offer отправлен: {session.PeerName}");
                }

                if (session.State == "ended")
                {
                    Log.Info($"сессия завершена: {session.PeerName}");
                    await CloseSessionAsync(session);
                    _sessions.TryRemove(session.Id, out _);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"цикл сессии прерван: {ex.Message}");
        }
    }

    private async Task HandleSignalAsync(ActiveSession session, SignalMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case "offer":
                var offerCodec = ReadCodec(message.Payload);

                // Браузерный зритель: видео пойдёт JPEG-ом по data channel.
                // SDP при этом договариваем как H.264 — браузер его и предлагает.
                var dataChannelVideo = offerCodec == VideoCodec.JpgData;

                if (dataChannelVideo)
                {
                    offerCodec = VideoCodec.H264;
                }

                session.DataChannelVideo = dataChannelVideo;

                // Кодек выбрал зритель; если у нас нет кодека — лучше закрыть сессию
                // с внятной причиной, чем показывать чёрный/пустой экран.
                if (offerCodec == VideoCodec.H264 && !dataChannelVideo && !H264Decoder.IsAvailable())
                {
                    Log.Error("нет openh264.dll — H.264 невозможен, закрываю сессию");
                    StatusChanged?.Invoke("Нет openh264.dll рядом с exe — положите файл и повторите");
                    await TryAnswerAsync(session.Id, "end", ct);
                    break;
                }

                AttachTransportIfNeeded(session, offerCodec);
                var answer = await session.Transport!.CreateAnswerAsync(message.Payload, ct);

                // Сообщаем зрителю, какой режим мы поняли: новый веб-клиент по этому полю
                // отличает «хост старый» от «что-то с сетью».
                if (answer is Dictionary<string, object?> answerFields)
                {
                    answerFields["echo"] = session.DataChannelVideo ? "jpgdata" : offerCodec.ToWire();
                }

                await _client.SignalAsync(session.Id, "answer", answer, ct);
                Log.Info($"offer получен ({offerCodec.ToWire()}), answer отправлен: {session.PeerName}");
                break;

            case "answer":
                if (session.Transport is not null)
                {
                    await session.Transport.ApplyAnswerAsync(message.Payload, ct);
                }
                break;

            // Зритель просит резервный канал: P2P не сошёлся, гоним кадры через сервер.
            case "relay":
                var enableRelay = message.Payload.ValueKind == JsonValueKind.Object &&
                                  message.Payload.TryGetProperty("on", out var relayOn) &&
                                  relayOn.ValueKind == JsonValueKind.True;

                if (enableRelay && session.Relay is null)
                {
                    session.Relay = new RelayStreamer(_client, session.Id, _config, session.Injector);
                    session.Relay.Start();
                }
                else if (!enableRelay && session.Relay is not null)
                {
                    await session.Relay.DisposeAsync();
                    session.Relay = null;
                    Log.Info("резервный канал: выключен");
                }
                break;

            case "ice":
                if (session.Transport is not null)
                {
                    await session.Transport.AddRemoteIceAsync(message.Payload, ct);
                }
                break;

            case "bye":
                Log.Info($"peer попрощался: {session.PeerName}");
                break;

            case "auth":
            case "challenge":
            case "auth_failed":
                // обработаны в HandleAuthAsync ещё до принятия заявки
                break;

            default:
                Log.Warn($"неизвестный тип сигнала: {message.Type}");
                break;
        }
    }

    private void HandleControl(ActiveSession session, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("t", out var type) && type.GetString() == "cb")
            {
                session.Clipboard.Incoming(root);
                return;
            }

            // зритель не смог декодировать кадр — просит ключевой
            if (root.TryGetProperty("t", out type) && type.GetString() == "kf")
            {
                session.Streamer?.RequestKeyframe();
                return;
            }

            // зритель выбрал другой монитор
            if (root.TryGetProperty("t", out type) && type.GetString() == "mon" &&
                root.TryGetProperty("i", out var displayIndex) && displayIndex.TryGetInt32(out var display))
            {
                Log.Info($"зритель переключает монитор: #{display}");
                session.Streamer?.SwitchDisplay(display);
                return;
            }

            // обратная связь по каналу: сколько зритель реально получает
            if (root.TryGetProperty("t", out type) && type.GetString() == "stat")
            {
                var fps = root.TryGetProperty("fps", out var fpsValue) && fpsValue.TryGetDouble(out var f) ? f : 0;
                var kbps = root.TryGetProperty("kbps", out var kbpsValue) && kbpsValue.TryGetDouble(out var k) ? k : 0;

                session.Streamer?.ReportViewStats(fps, kbps);
                return;
            }

            // файлообмен: сначала ответ на нашу отправку, иначе — входящее
            if (root.TryGetProperty("t", out type) && type.GetString() == "file")
            {
                if (session.Sender?.HandleControl(root) != true)
                {
                    HandleFile(session, root);
                }

                return;
            }
        }
        catch (JsonException)
        {
        }

        session.Injector.Handle(json);
    }

    /// <summary>Листинг каталога для файловой панели. Пустой путь — корни дисков.</summary>
    private static object BuildListing(string? requested)
    {
        var path = string.IsNullOrWhiteSpace(requested) ? FileWire.Downloads : requested;

        try
        {
            // Пустой запрос — список дисков (для панели «удалённый компьютер»).
            if (string.IsNullOrWhiteSpace(requested))
            {
                var roots = new List<object>();

                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    var label = drive.DriveType == DriveType.Fixed
                        ? $"{drive.Name} ({drive.VolumeLabel})"
                        : $"{drive.Name} ({drive.DriveType.ToString().ToLower()})";

                    roots.Add(new { n = label, d = true, s = 0L, p = drive.Name });
                }

                // Рабочий стол хоста — панель открывает его по умолчанию.
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                return new { t = "file", op = "list", path = "", parent = "", desktop, entries = roots };
            }

            if (Directory.Exists(path))
            {
                var entries = new List<object>();

                foreach (var dir in Directory.EnumerateDirectories(path).Take(200))
                {
                    var info = new DirectoryInfo(dir);

                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    entries.Add(new { n = info.Name, d = true, s = 0L, p = info.FullName });
                }

                foreach (var file in Directory.EnumerateFiles(path).Take(300))
                {
                    var info = new FileInfo(file);

                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    entries.Add(new { n = info.Name, d = false, s = info.Length, p = info.FullName });
                }

                return new
                {
                    t = "file",
                    op = "list",
                    path,
                    parent = Directory.GetParent(path)?.FullName,
                    entries,
                };
            }

            // Не каталог — показываем его папку: зритель выберет файл из списка
            if (File.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                return BuildListing(directory);
            }
        }
        catch (Exception ex)
        {
            return new { t = "file", op = "list", error = ex.Message };
        }

        return new { t = "file", op = "list", path, error = "каталог недоступен" };
    }

    /// <summary>Есть ли подключённый зритель (наш экран сейчас смотрят).</summary>
    public bool HasActiveViewer =>
        _sessions.Values.Any(s => s.Streamer is not null && s.Transport is { IsConnected: true });

    /// <summary>Отправить файл подключённому зрителю — «скачать» с удалённой машины.</summary>
    public async Task<bool> SendFileToViewerAsync(string path, CancellationToken ct = default)
    {
        var session = _sessions.Values.FirstOrDefault(
            s => s.Streamer is not null && s.Transport is { IsConnected: true } && s.Sender is not null);

        if (session?.Sender is null)
        {
            return false;
        }

        await session.Sender.SendAsync(path, null, ct);
        return true;
    }

    /// <summary>Предложение файла: принимаем сразу (сессия уже подтверждена) и пишем в Downloads\FreeDesk.</summary>
    private void HandleFile(ActiveSession session, JsonElement root)
    {
        var op = root.TryGetProperty("op", out var opValue) ? opValue.GetString() : null;
        var id = root.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var parsed) ? parsed : 0;

        switch (op)
        {
            case "offer":
                var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? "file" : "file";
                var size = root.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
                var dir = root.TryGetProperty("dir", out var dirValue) && dirValue.ValueKind == JsonValueKind.String
                    ? dirValue.GetString()
                    : null;

                if (session.Files.TryStart(id, name, size, out var error, dir))
                {
                    session.Transport?.SendControl(JsonSerializer.Serialize(new { t = "file", op = "accept", id }));
                    Log.Info($"приём файла: {name} ({size / 1024} КБ)");
                    StatusChanged?.Invoke($"Получение файла: {name}");
                }
                else
                {
                    session.Transport?.SendControl(JsonSerializer.Serialize(new { t = "file", op = "reject", id, reason = error }));
                    Log.Warn($"файл отклонён: {error}");
                }

                break;

            case "done":
                Log.Info($"передача файла {id} завершена");
                break;

            case "download":
                // зритель просит конкретный файл с этой машины
                var remotePath = root.TryGetProperty("path", out var pathValue) ? pathValue.GetString() : null;

                if (string.IsNullOrWhiteSpace(remotePath) || !File.Exists(remotePath))
                {
                    session.Transport?.SendControl(JsonSerializer.Serialize(new
                    {
                        t = "file",
                        op = "error",
                        reason = "файл не найден: " + remotePath,
                    }));

                    Log.Warn($"запрошен несуществующий файл: {remotePath}");
                    break;
                }

                Log.Info($"зритель скачивает: {remotePath}");
                var saveDir = root.TryGetProperty("dir", out var saveValue) && saveValue.ValueKind == JsonValueKind.String
                    ? saveValue.GetString()
                    : null;
                _ = Task.Run(() => session.Sender?.SendAsync(remotePath, saveDir));
                break;

            case "browse":
                // зритель листает файловую систему этой машины
                var browsePath = root.TryGetProperty("path", out var browseValue) ? browseValue.GetString() : null;
                session.Transport?.SendControl(JsonSerializer.Serialize(BuildListing(browsePath)));
                break;

            case "cancel":
                session.Files.Abort(id);
                Log.Warn($"передача файла {id} отменена");
                break;
        }
    }

    /// <summary>Отправляет peer'у изменившийся локальный буфер обмена (раз в цикл).</summary>
    private void SyncClipboards()
    {
        foreach (var session in _sessions.Values)
        {
            // буфер отдаёт только та сторона, чей экран смотрят (не инициатор)
            if (session.Transport is not { IsConnected: true } || session.Role == "host")
            {
                continue;
            }

            var json = session.Clipboard.PollOutgoingJson();
            if (json is not null)
            {
                Log.Info($"буфер обмена → peer ({json.Length} байт)");
                session.Transport.SendControl(json);
            }
        }
    }

    private void AttachTransportIfNeeded(ActiveSession session, VideoCodec codec)
    {
        if (session.Transport is null)
        {
            AttachTransport(session, offerer: session.Role == "host", codec);
        }
    }

    /// <summary>Кодек, который зритель указал в offer; старые клиенты поля не шлют.</summary>
    private static VideoCodec ReadCodec(JsonElement payload) =>
        payload.TryGetProperty("codec", out var value)
            ? VideoCodecNames.Parse(value.GetString())
            : VideoCodec.Mjpeg;

    private async Task CloseSessionAsync(ActiveSession session)
    {
        session.Files.Dispose();

        if (session.Relay is not null)
        {
            await session.Relay.DisposeAsync();
            session.Relay = null;
        }

        if (session.Streamer is not null)
        {
            await session.Streamer.DisposeAsync();
            session.Streamer = null;
        }

        if (session.Transport is not null)
        {
            await session.Transport.DisposeAsync();
            session.Transport = null;
        }
    }

    private sealed class PendingAuth
    {
        public required IncomingItem Item { get; init; }
        public required string Nonce { get; set; }
        public required DateTime ExpiresAt { get; set; }
        public long Cursor { get; set; }
        public int Attempts { get; set; }
    }

    private sealed class ActiveSession
    {
        public required string Id { get; init; }
        public required string Role { get; init; }
        public required string PeerName { get; init; }
        public string State { get; set; } = "pending";
        public long Cursor { get; set; }
        public bool OfferSent { get; set; }
        public IPeerTransport? Transport { get; set; }
        public ScreenStreamer? Streamer { get; set; }

        /// <summary>Резервный канал через сервер — включается, когда P2P не сошёлся.</summary>
        public RelayStreamer? Relay { get; set; }
        public int InputCount { get; set; }
        public int EndedFlag;
        public InputInjector Injector { get; } = new();
        public ClipboardSync Clipboard { get; } = new();
        public FileReceiver Files { get; } = new();
        public FileSender? Sender { get; set; }

        /// <summary>Кадры идут JPEG-ом по data channel (браузерный зритель), а не по RTP.</summary>
        public bool DataChannelVideo { get; set; }
    }
}
