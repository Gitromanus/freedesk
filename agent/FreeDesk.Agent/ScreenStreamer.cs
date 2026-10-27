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

    /// <summary>Ниже этого битрейт не опускаем: картинка станет нечитаемой.</summary>
    private const int MinBitrateKbps = 400;

    private readonly IPeerTransport _transport;
    private readonly string _captureMode;
    private readonly int _fps;
    private readonly long _quality;
    private readonly int _maxWidth;
    private readonly int _bitrateKbps;
    private int _targetKbps;
    private DateTime _lastRateChange = DateTime.MinValue;

    /// <summary>Сколько кадров в секунду реально ушло в сеть за последнее окно (5 с).</summary>
    private double _sentFps;
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

    /// <summary>Браузерный режим: кадры JPEG уходят бинарём по data channel вместо RTP.</summary>
    private readonly bool _jpegOverDataChannel;
    private int _frameSeq;

    public ScreenStreamer(
        IPeerTransport transport, int fps, long quality, int maxWidth, string captureMode = "auto",
        int bitrateKbps = 3000, int displayIndex = 0, bool jpegOverDataChannel = false)
    {
        _transport = transport;
        _fps = Math.Clamp(fps, 1, 60);
        _quality = quality;
        _maxWidth = Math.Max(0, maxWidth);
        _captureMode = captureMode;
        _bitrateKbps = Math.Clamp(bitrateKbps, 200, 50_000);
        _targetKbps = _bitrateKbps;
        _displayIndex = Math.Max(0, displayIndex);
        _jpegOverDataChannel = jpegOverDataChannel;

        // Браузерный режим: JPEG идёт по SCTP, где реально ~1 МБ/с.
        // Без ограничений кадр 1920×1080 даёт 15 Мбит/с — телефон такое не переварит.
        if (_jpegOverDataChannel)
        {
            _fps = Math.Min(_fps, 10);
            _quality = Math.Min(_quality, 60);
            _maxWidth = _maxWidth <= 0 ? 1280 : Math.Min(_maxWidth, 1280);
        }
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

    /// <summary>
    /// Отчёт зрителя о приёме (раз в 5 с): подстраиваем битрейт под канал.
    /// Снижаем, когда зритель явно не успевает и недобирает байты (сеть);
    /// поднимаем, когда частота полная и поток реально грузит канал.
    /// </summary>
    public void ReportViewStats(double viewerFps, double viewerKbps)
    {
        if (_encoder is null || viewerFps <= 0)
        {
            return;
        }

        Log.Info($"зритель: {viewerFps:F1} fps, {viewerKbps:F0} кбит/с " +
                 $"(отправлено {_sentFps:F1} fps, битрейт {_targetKbps} кбит/с)");

        var now = DateTime.UtcNow;
        if (now - _lastRateChange < TimeSpan.FromSeconds(3))
        {
            return;
        }

        // Сравниваем с тем, сколько мы реально отправили: на статичном экране
        // энкодер отдаёт единичные кадры, и низкий fps зрителя — не потеря сети.
        var sentFps = Math.Max(1.0, _sentFps);
        var next = _targetKbps;

        if (viewerFps < sentFps * 0.6 && viewerKbps < _targetKbps * 0.7)
        {
            next = Math.Max(MinBitrateKbps, (int)(_targetKbps * 0.75));
        }
        else if (viewerFps > sentFps * 0.85 && viewerKbps > _targetKbps * 0.5 && _targetKbps < _bitrateKbps)
        {
            next = Math.Min(_bitrateKbps, (int)(_targetKbps * 1.2));
        }

        if (next == _targetKbps)
        {
            return;
        }

        try
        {
            _encoder.SetBitrate(next);
            Log.Info($"битрейт: {_targetKbps} → {next} кбит/с " +
                     $"(зритель {viewerFps:F1} fps, {viewerKbps:F0} кбит/с)");

            _targetKbps = next;
            _lastRateChange = now;
        }
        catch (Exception ex)
        {
            Log.Warn($"битрейт не изменён: {ex.Message}");
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

            if (_transport.Codec == VideoCodec.H264 && !_jpegOverDataChannel)
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

                    if (_encoder is null || _jpegOverDataChannel)
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
                        $"поток ({(_jpegOverDataChannel ? "jpgdata" : _transport.Codec.ToWire())}): {frames / seconds:F1} fps, " +
                        $"{perFrame} КБ/кадр, {bytes * 8 / seconds / 1_000_000:F1} Мбит/с" +
                        (skipped > 0 ? $", пропущено статичных: {skipped}" : ""));

                    _sentFps = frames / seconds;

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

    /// <summary>
    /// Браузерный режим: пакет [0xF1][резерв][seq:2 LE][длина:4 LE][JPEG].
    /// Формат читает m.html; порядок байт — little-endian, как в DataView.
    /// </summary>
    private void SendJpegOverChannel(byte[] jpeg)
    {
        var packet = new byte[8 + jpeg.Length];
        packet[0] = 0xF1;

        var seq = (ushort)(_frameSeq++ & 0xFFFF);
        packet[2] = (byte)(seq & 0xFF);
        packet[3] = (byte)(seq >> 8);

        var length = jpeg.Length;
        packet[4] = (byte)(length & 0xFF);
        packet[5] = (byte)((length >> 8) & 0xFF);
        packet[6] = (byte)((length >> 16) & 0xFF);
        packet[7] = (byte)((length >> 24) & 0xFF);

        Buffer.BlockCopy(jpeg, 0, packet, 8, jpeg.Length);
        _transport.SendBinary(packet);
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

        if (_jpegOverDataChannel)
        {
            SendJpegOverChannel(jpeg);
        }
        else
        {
            _transport.SendFrame(jpeg, Duration);
        }
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

        _encoder = new H264Encoder(targetWidth, targetHeight, _fps, _targetKbps);
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
