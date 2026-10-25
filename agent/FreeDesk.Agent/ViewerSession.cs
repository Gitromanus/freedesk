using System.Drawing;
using FreeDesk.Agent.Codec;

namespace FreeDesk.Agent;

/// <summary>Сторона зрителя: инициирует сессию, принимает кадры, отправляет ввод.</summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private readonly SignalingClient _client;
    private readonly string _targetId;
    private readonly VideoCodec _codec;
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

    public event Action<string>? StatusChanged;
    public event Action? Connected;

    /// <summary>Сессия завершилась (канал закрыт, сервер закончил или ошибка) — UI возвращается к стартовому экрану.</summary>
    public event Action? Ended;

    public ViewerSession(SignalingClient client, string targetId, VideoCodec codec = VideoCodec.Mjpeg)
    {
        _client = client;
        _targetId = targetId;
        _codec = codec;
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
            StatusChanged?.Invoke("канал установлен");
            Connected?.Invoke();
        };

        peer.ControlReceived += json =>
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.TryGetProperty("t", out var type) && type.GetString() == "cb")
                {
                    _clipboard.Incoming(root);
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
        }
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
