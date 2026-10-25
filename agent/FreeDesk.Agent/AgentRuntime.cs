using System.Collections.Concurrent;
using System.Text.Json;
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
    private DateTime _lastPresence = DateTime.MinValue;
    private readonly HashSet<string> _prompted = new();

    /// <summary>Вызывается при входящей заявке, если AutoAccept=false. Верните true для принятия.</summary>
    public Func<IncomingItem, Task<bool>>? AcceptPrompt { get; set; }

    /// <summary>Текстовый статус для UI (подключение/отключение).</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Сессия началась (хост отдаёт экран) — UI может свернуться.</summary>
    public event Action? SessionStarted;

    /// <summary>Сессия закончилась — UI восстанавливается.</summary>
    public event Action? SessionEnded;

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
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _config.PollSeconds)), ct);
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
        AttachTransport(session, offerer: true);
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

            if (_sessions.ContainsKey(item.SessionId) || _pending.ContainsKey(item.SessionId))
            {
                continue;
            }

            if (_config.AutoAccept)
            {
                await AcceptAsync(item, ct);
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
        await _client.AnswerAsync(item.SessionId, "accept", ct);
        Log.Info($"подключение принято: {item.From.Name} ({item.From.PublicId}), сессия {item.SessionId}");

        var session = new ActiveSession
        {
            Id = item.SessionId,
            Role = "guest",
            PeerName = item.From.Name,
            State = "active",
        };

        AttachTransport(session, offerer: false);
        _sessions[item.SessionId] = session;
    }

    private void AttachTransport(ActiveSession session, bool offerer)
    {
        var peer = new WebRtcPeer(offerer);
        session.Transport = peer;

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
                peer, _config.Fps, _config.JpegQuality, _config.MaxWidth, _config.Capture);
            session.Streamer.Start();
            StatusChanged?.Invoke($"Подключено: {session.PeerName}");
            SessionStarted?.Invoke();
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

        peer.Closed += reason =>
        {
            Log.Info($"канал закрыт ({session.PeerName}): {reason}");
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
        foreach (var session in _sessions.Values.ToList())
        {
            var poll = await _client.PollAsync(session.Id, session.Cursor, ct);
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
            }
        }
    }

    private async Task HandleSignalAsync(ActiveSession session, SignalMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case "offer":
                AttachTransportIfNeeded(session);
                var answer = await session.Transport!.CreateAnswerAsync(message.Payload, ct);
                await _client.SignalAsync(session.Id, "answer", answer, ct);
                Log.Info($"offer получен, answer отправлен: {session.PeerName}");
                break;

            case "answer":
                if (session.Transport is not null)
                {
                    await session.Transport.ApplyAnswerAsync(message.Payload, ct);
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
        }
        catch (JsonException)
        {
        }

        session.Injector.Handle(json);
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

    private void AttachTransportIfNeeded(ActiveSession session)
    {
        if (session.Transport is null)
        {
            AttachTransport(session, offerer: session.Role == "host");
        }
    }

    private async Task CloseSessionAsync(ActiveSession session)
    {
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
        public int InputCount { get; set; }
        public int EndedFlag;
        public InputInjector Injector { get; } = new();
        public ClipboardSync Clipboard { get; } = new();
    }
}
