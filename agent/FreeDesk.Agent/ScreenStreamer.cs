using System.Diagnostics;
using FreeDesk.Agent.Capture;
using FreeDesk.Agent.Codec;

namespace FreeDesk.Agent;

/// <summary>
/// Пока сессия активна — снимает экран и гонит JPEG-кадры в транспорт.
/// Статичные кадры не отправляются: сравнивается отпечаток пикселей с предыдущим
/// кадром, поэтому неподвижный рабочий стол почти не тратит трафик.
/// </summary>
public sealed class ScreenStreamer : IAsyncDisposable
{
    /// <summary>Как часто пересылать последний кадр, даже если экран не менялся.</summary>
    private static readonly TimeSpan StaticRefresh = TimeSpan.FromSeconds(5);

    /// <summary>Столько ошибок захвата подряд — переключаемся на GDI навсегда.</summary>
    private const int MaxConsecutiveErrors = 3;

    private readonly IPeerTransport _transport;
    private readonly string _captureMode;
    private readonly int _fps;
    private readonly long _quality;
    private readonly int _maxWidth;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private IScreenCapture? _capture;
    private byte[]? _lastFrame;
    private DateTime _lastSentAt;
    private int _consecutiveErrors;

    public ScreenStreamer(IPeerTransport transport, int fps, long quality, int maxWidth, string captureMode = "auto")
    {
        _transport = transport;
        _fps = Math.Clamp(fps, 1, 60);
        _quality = quality;
        _maxWidth = Math.Max(0, maxWidth);
        _captureMode = captureMode;
    }

    public void Start()
    {
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            _capture = CreateCapture(_captureMode);
            Log.Info($"захват: {_capture.GetType().Name} {_capture.Width}x{_capture.Height}");

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / _fps));
            var stopwatch = Stopwatch.StartNew();
            var frames = 0;
            var skipped = 0;
            var bytes = 0L;

            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try
                {
                    var frame = _capture.CaptureFrame(out var stride);

                    // Кадр не изменился и последний отправляли недавно — пропускаем
                    // кодирование и отправку. По истечении StaticRefresh пересылаем
                    // прежний кадр: так зритель восстанавливается после потери
                    // RTP-пакета, даже если экран неподвижен.
                    if (IsUnchanged(frame) && DateTime.UtcNow - _lastSentAt < StaticRefresh)
                    {
                        skipped++;
                    }
                    else
                    {
                        var jpeg = JpegEncoder.Encode(
                            frame, _capture.Width, _capture.Height, stride, _quality, _maxWidth);

                        _transport.SendFrame(jpeg, (uint)Math.Round(90000.0 / _fps));
                        Remember(frame);
                        _lastSentAt = DateTime.UtcNow;
                        _consecutiveErrors = 0;

                        frames++;
                        bytes += jpeg.Length;
                    }
                }
                catch (Exception ex)
                {
                    RecreateCaptureAfterError(ex);
                }

                if (stopwatch.Elapsed.TotalSeconds >= 5)
                {
                    var seconds = stopwatch.Elapsed.TotalSeconds;

                    Log.Info(
                        $"поток: {frames / seconds:F1} fps, " +
                        $"{(frames > 0 ? bytes / frames / 1024 : 0)} КБ/кадр, " +
                        $"{bytes * 8 / seconds / 1_000_000:F1} Мбит/с" +
                        (skipped > 0 ? $", пропущено статичных: {skipped}" : ""));

                    frames = 0;
                    skipped = 0;
                    bytes = 0;
                    stopwatch.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
        catch (Exception ex)
        {
            Log.Error($"поток прерван: {ex.Message}");
        }
        finally
        {
            _capture?.Dispose();
        }
    }

    private bool IsUnchanged(ReadOnlySpan<byte> frame) =>
        _lastFrame is { Length: > 0 } && _lastFrame.AsSpan().SequenceEqual(frame);

    private void Remember(ReadOnlySpan<byte> frame)
    {
        if (_lastFrame is null || _lastFrame.Length != frame.Length)
        {
            _lastFrame = new byte[frame.Length];
        }

        frame.CopyTo(_lastFrame);
    }

    private IScreenCapture CreateCapture(string mode)
    {
        try
        {
            return CaptureFactory.Create(mode);
        }
        catch (Exception ex)
        {
            // Даже явно выбранный режим не должен убивать поток: откатываемся на GDI.
            Log.Warn($"{ex.Message}, откатываюсь на gdi");
            return CaptureFactory.Create("gdi");
        }
    }

    private void RecreateCaptureAfterError(Exception ex)
    {
        _consecutiveErrors++;

        // Систематически падающий DXGI (нестандартный драйвер, RDP-сессия)
        // бессмысленно пересоздавать — после трёх ошибок подряд уходим на GDI.
        var mode = _consecutiveErrors >= MaxConsecutiveErrors && _captureMode != "gdi"
            ? "gdi"
            : _captureMode;

        Log.Warn($"ошибка захвата: {ex.Message}, пересоздаю ({mode})");

        try
        {
            _capture?.Dispose();
        }
        catch
        {
        }

        _capture = CreateCapture(mode);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
