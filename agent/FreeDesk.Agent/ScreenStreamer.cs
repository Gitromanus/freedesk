using System.Diagnostics;
using FreeDesk.Agent.Capture;
using FreeDesk.Agent.Codec;

namespace FreeDesk.Agent;

/// <summary>
/// Пока сессия активна — снимает экран и гонит кадры в транспорт.
/// H.264 (openh264): поток интерфреймов, ключевой кадр раз в 5 с и по запросу
/// зрителя. MJPEG: статичные кадры не отправляются вовсе (сравнение пикселей).
/// </summary>
public sealed class ScreenStreamer : IAsyncDisposable
{
    /// <summary>Как часто пересылать последний кадр, даже если экран не менялся.</summary>
    private static readonly TimeSpan StaticRefresh = TimeSpan.FromSeconds(5);

    /// <summary>Как часто форсировать IDR: зритель восстанавливается после потери пакетов.</summary>
    private static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(5);

    /// <summary>Столько ошибок захвата подряд — переключаемся на GDI навсегда.</summary>
    private const int MaxConsecutiveErrors = 3;

    private readonly IPeerTransport _transport;
    private readonly string _captureMode;
    private readonly int _fps;
    private readonly long _quality;
    private readonly int _maxWidth;
    private readonly int _bitrateKbps;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private IScreenCapture? _capture;
    private H264Encoder? _encoder;
    private byte[]? _lastFrame;
    private DateTime _lastSentAt;
    private DateTime _lastKeyframeAt = DateTime.MinValue;
    private int _consecutiveErrors;
    private string? _effectiveMode;
    private int _displayIndex;
    private int _requestedDisplay = -1;

    public ScreenStreamer(
        IPeerTransport transport, int fps, long quality, int maxWidth, string captureMode = "auto",
        int bitrateKbps = 3000, int displayIndex = 0)
    {
        _transport = transport;
        _fps = Math.Clamp(fps, 1, 60);
        _quality = quality;
        _maxWidth = Math.Max(0, maxWidth);
        _captureMode = captureMode;
        _bitrateKbps = Math.Clamp(bitrateKbps, 200, 50_000);
        _displayIndex = Math.Max(0, displayIndex);
    }

    public void Start()
    {
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>
    /// Переключить монитор (команда зрителя). Применяется в цикле перед захватом,
    /// чтобы не спорить с потоком за GDI/DXGI-ресурсы.
    /// </summary>
    public void SwitchDisplay(int index)
    {
        if (index >= 0)
        {
            _requestedDisplay = index;
        }
    }

    /// <summary>Зритель не смог декодировать кадр — просим энкодер выдать IDR.</summary>
    public void RequestKeyframe()
    {
        try
        {
            _encoder?.ForceKeyframe();
        }
        catch (Exception ex)
        {
            Log.Warn($"h264: не удалось запросить IDR: {ex.Message}");
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            _capture = CreateCapture(_effectiveMode ?? _captureMode);
            Log.Info($"захват: {_capture.GetType().Name} {_capture.Width}x{_capture.Height} " +
                     $"(монитор #{_displayIndex}), кодек {_transport.Codec.ToWire()}");

            if (_transport.Codec == VideoCodec.H264)
            {
                // создаём сразу: если openh264.dll нет, узнаем об этом в логе, а не в тишине
                EnsureEncoder(_capture.Width, _capture.Height);
            }

            SendDisplays();

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / _fps));
            var stopwatch = Stopwatch.StartNew();
            var frames = 0;
            var skipped = 0;
            var bytes = 0L;

            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try
                {
                    ApplyDisplaySwitch();

                    var frame = _capture!.CaptureFrame(out var stride);

                    if (_encoder is null)
                    {
                        SendMjpeg(frame, stride, ref frames, ref bytes, ref skipped);
                    }
                    else
                    {
                        SendH264(frame, stride, ref frames, ref bytes);
                    }
                }
                catch (Exception ex)
                {
                    RecreateCaptureAfterError(ex);
                }

                if (stopwatch.Elapsed.TotalSeconds >= 5)
                {
                    var seconds = stopwatch.Elapsed.TotalSeconds;
                    var perFrame = frames > 0 ? bytes / frames / 1024 : 0;

                    Log.Info(
                        $"поток ({_transport.Codec.ToWire()}): {frames / seconds:F1} fps, " +
                        $"{perFrame} КБ/кадр, {bytes * 8 / seconds / 1_000_000:F1} Мбит/с" +
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
            _encoder?.Dispose();
        }
    }

    private void SendMjpeg(ReadOnlySpan<byte> frame, int stride, ref int frames, ref long bytes, ref int skipped)
    {
        // Кадр не изменился и последний отправляли недавно — пропускаем
        // кодирование и отправку. По истечении StaticRefresh пересылаем
        // прежний кадр: так зритель восстанавливается после потери
        // RTP-пакета, даже если экран неподвижен.
        if (IsUnchanged(frame) && DateTime.UtcNow - _lastSentAt < StaticRefresh)
        {
            skipped++;
            return;
        }

        var jpeg = JpegEncoder.Encode(frame, _capture!.Width, _capture.Height, stride, _quality, _maxWidth);

        _transport.SendFrame(jpeg, Duration);
        Remember(frame);
        _lastSentAt = DateTime.UtcNow;
        _consecutiveErrors = 0;

        frames++;
        bytes += jpeg.Length;
    }

    private void SendH264(ReadOnlySpan<byte> frame, int stride, ref int frames, ref long bytes)
    {
        var encoder = EnsureEncoder(_capture!.Width, _capture.Height);

        if (DateTime.UtcNow - _lastKeyframeAt >= KeyframeInterval)
        {
            encoder.ForceKeyframe();
            _lastKeyframeAt = DateTime.UtcNow;
        }

        // Энкодер масштабирует сам: вход — исходный кадр захвата.
        var fitted = encoder.Fit(frame, _capture.Width, _capture.Height, stride);
        var accessUnit = encoder.Encode(fitted, encoder.Width * 4, Environment.TickCount64, out _);

        if (accessUnit is null)
        {
            return;
        }

        _transport.SendFrame(accessUnit, Duration);
        _lastSentAt = DateTime.UtcNow;
        _consecutiveErrors = 0;

        frames++;
        bytes += accessUnit.Length;
    }

    /// <summary>Энкодер под текущий размер кадра: после смены разрешения его надо пересоздать.</summary>
    private H264Encoder EnsureEncoder(int width, int height)
    {
        var (targetWidth, targetHeight) = FitSize(width, height, _maxWidth);
        targetWidth &= ~1;
        targetHeight &= ~1;

        if (_encoder is { } current && current.Width == targetWidth && current.Height == targetHeight)
        {
            return current;
        }

        if (_encoder is not null)
        {
            Log.Info($"h264: размер кадра изменился на {targetWidth}x{targetHeight}, пересоздаю энкодер");
            _encoder.Dispose();
        }

        _encoder = new H264Encoder(targetWidth, targetHeight, _fps, _bitrateKbps);
        _lastKeyframeAt = DateTime.UtcNow;
        return _encoder;
    }

    private uint Duration => (uint)Math.Round((double)WebRtcPeer.VideoClockRate / _fps);

    private static (int Width, int Height) FitSize(int width, int height, int maxWidth)
    {
        if (maxWidth <= 0 || width <= maxWidth)
        {
            return (width, height);
        }

        var scaled = Math.Max(2, (int)Math.Round(height * (double)maxWidth / width));
        return (maxWidth, scaled);
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

    /// <summary>Список мониторов и активный — зрителю для кнопок переключения.</summary>
    private void SendDisplays()
    {
        var items = Displays.All()
            .Select(d => new { i = d.Index, w = d.Width, h = d.Height, p = d.Primary })
            .ToArray();

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            t = "displays",
            items,
            active = _displayIndex,
        });

        _transport.SendControl(json);
    }

    /// <summary>Пересоздаёт захват под запрошенный монитор (вызывается только из цикла).</summary>
    private void ApplyDisplaySwitch()
    {
        var requested = _requestedDisplay;
        if (requested < 0 || requested == _displayIndex)
        {
            return;
        }

        _requestedDisplay = -1;

        var display = Displays.ByIndex(requested);
        Log.Info($"переключение на монитор #{display.Index}: {display.Width}x{display.Height} @ {display.X},{display.Y}");

        try
        {
            _capture?.Dispose();
        }
        catch
        {
        }

        _displayIndex = display.Index;
        _lastFrame = null;
        _consecutiveErrors = 0;
        _capture = CreateCapture(_effectiveMode ?? _captureMode);

        // кадр сменился целиком — зрителю нужен ключевой
        _lastKeyframeAt = DateTime.UtcNow;
        _encoder?.ForceKeyframe();

        SendDisplays();
    }

    private IScreenCapture CreateCapture(string mode)
    {
        var display = Displays.ByIndex(_displayIndex);

        try
        {
            return CaptureFactory.Create(mode, display);
        }
        catch (Exception ex)
        {
            // Даже явно выбранный режим не должен убивать поток: откатываемся на GDI.
            Log.Warn($"{ex.Message}, откатываюсь на gdi");
            _effectiveMode = "gdi";
            return CaptureFactory.Create("gdi", display);
        }
    }

    private void RecreateCaptureAfterError(Exception ex)
    {
        _consecutiveErrors++;

        // Пустые кадры DXGI — не сбой драйвера, а постоянное свойство RDP-сессии:
        // пересоздавать бессмысленно, сразу уходим на GDI и больше не возвращаемся.
        var blank = ex.Message.Contains("пустые", StringComparison.OrdinalIgnoreCase);

        var mode = _effectiveMode ?? _captureMode;
        if ((blank || _consecutiveErrors >= MaxConsecutiveErrors) && mode != "gdi")
        {
            mode = "gdi";
        }

        Log.Warn($"ошибка захвата: {ex.Message}, пересоздаю ({mode})");

        try
        {
            _capture?.Dispose();
        }
        catch
        {
        }

        _effectiveMode = mode;
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
