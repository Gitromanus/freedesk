using System.Diagnostics;
using FreeDesk.Agent.Capture;
using FreeDesk.Agent.Codec;
using FreeDesk.Agent.Input;

namespace FreeDesk.Agent;

/// <summary>
/// Резервный канал: когда NAT не пропускает прямое WebRTC-соединение, хост гонит
/// кадры JPEG через сигнальный сервер, а ввод забирает оттуда же.
///
/// Медленно (2–3 кадра/с), зато работает из любой сети: и клиент, и хост сами
/// ходят на сервер по HTTPS, никаких входящих соединений им не нужно.
/// </summary>
public sealed class RelayStreamer : IAsyncDisposable
{
    private readonly SignalingClient _client;
    private readonly string _sessionId;
    private readonly AgentConfig _config;
    private readonly InputInjector _injector;

    private readonly int _fps;
    private readonly long _quality;
    private readonly int _maxWidth;

    private readonly CancellationTokenSource _cts = new();
    private Task? _frames;
    private Task? _controls;

    /// <summary>Сколько кадров реально ушло — по этому числу понятно, живой ли канал.</summary>
    public int SentFrames { get; private set; }

    public RelayStreamer(
        SignalingClient client, string sessionId, AgentConfig config, InputInjector injector,
        int fps = 2, long quality = 50, int maxWidth = 960)
    {
        _client = client;
        _sessionId = sessionId;
        _config = config;
        _injector = injector;

        _fps = Math.Clamp(fps, 1, 10);
        _quality = Math.Clamp(quality, 20, 80);
        _maxWidth = maxWidth <= 0 ? 1024 : Math.Min(maxWidth, 1280);
    }

    public void Start()
    {
        _frames = Task.Run(FramesLoopAsync);
        _controls = Task.Run(ControlsLoopAsync);

        Log.Info("резервный канал: включён (кадры через сервер)");
    }

    private async Task FramesLoopAsync()
    {
        IScreenCapture? capture = null;

        try
        {
            // Только GDI: релей — запасной путь, и часто именно на машинах, где
            // DXGI не работает (RDP-сессия отдаёт чёрные кадры, а создание
            // D3D-устройства может виснуть). BitBlt на 2 fps тянет легко.
            var display = ResolveDisplay();
            capture = new GdiScreenCapture(display ?? Displays.Primary());
            Log.Info($"резервный канал: захват {capture.GetType().Name} {capture.Width}x{capture.Height}");

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / _fps));
            var stopwatch = Stopwatch.StartNew();
            var frames = 0;
            var bytes = 0L;

            while (true)
            {
                bool tick;

                try
                {
                    tick = await timer.WaitForNextTickAsync(_cts.Token);
                }
                catch (ObjectDisposedException)
                {
                    // Гонка dispose с тиком таймера — штатный выход, а не катастрофа.
                    break;
                }

                if (!tick)
                {
                    break;
                }

                var frame = capture.CaptureFrame(out var stride);

                var jpeg = JpegEncoder.Encode(
                    frame, capture.Width, capture.Height, stride, _quality, _maxWidth);

                await _client.RelayFrameAsync(_sessionId, jpeg, _cts.Token);

                frames++;
                bytes += jpeg.Length;
                SentFrames++;

                if (stopwatch.Elapsed.TotalSeconds >= 10)
                {
                    Log.Info(
                        $"резервный канал: {frames / stopwatch.Elapsed.TotalSeconds:F1} fps, " +
                        $"{bytes / Math.Max(1, frames) / 1024} КБ/кадр");

                    frames = 0;
                    bytes = 0;
                    stopwatch.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"резервный канал (кадры): {ex.Message}");
        }
        finally
        {
            capture?.Dispose();
        }
    }

    private async Task ControlsLoopAsync()
    {
        long offset = 0;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var batch = await _client.RelayControlPullAsync(_sessionId, offset, _cts.Token);
                offset = batch.Offset;

                if (batch.Events is { Count: > 0 })
                {
                    foreach (var json in batch.Events)
                    {
                        _injector.Handle(json);
                    }
                }

                // Темп задаёт long-poll (wait=5): без событий цикл просто ждёт на сервере.
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error($"резервный канал (ввод): {ex.Message}");
        }
    }

    private DisplayInfo? ResolveDisplay()
    {
        var displays = Displays.All();
        var index = Math.Max(0, _config.Display);

        return index < displays.Count ? displays[index] : null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        foreach (var task in new[] { _frames, _controls })
        {
            if (task is not null)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // остановка по таймауту — не повод падать
                }
            }
        }

        _cts.Dispose();
    }
}
