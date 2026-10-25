using System.Diagnostics;
using FreeDesk.Agent.Capture;
using FreeDesk.Agent.Codec;

namespace FreeDesk.Agent;

/// <summary>Проверка конвейера захвата: снимает N кадров, кодирует и сохраняет последний.</summary>
public static class CaptureTest
{
    public static Task RunAsync(int frames, long quality, int maxWidth, string captureMode, string codec = "mjpeg")
    {
        using var capture = CaptureFactory.Create(captureMode);
        var outputPath = Path.Combine(Path.GetTempPath(), codec == "h264" ? "freedesk-frame.h264" : "freedesk-frame.jpg");

        Log.Info($"захват: {capture.GetType().Name} {capture.Width}x{capture.Height}, кадров {frames}, " +
                 $"кодек {codec}, качество {quality}, ширина {maxWidth}");

        long lastSize;
        var stopwatch = Stopwatch.StartNew();

        if (codec == "h264")
        {
            lastSize = RunH264(capture, frames, maxWidth, stopwatch, outputPath);
        }
        else
        {
            lastSize = RunMjpeg(capture, frames, quality, maxWidth, stopwatch, outputPath);
        }

        stopwatch.Stop();

        if (lastSize < 0)
        {
            return Task.CompletedTask;
        }

        var perFrame = stopwatch.Elapsed.TotalMilliseconds / frames;
        var fps = 1000.0 / perFrame;

        Console.WriteLine();
        Console.WriteLine($"  кадров:        {frames}");
        Console.WriteLine($"  на кадр:       {perFrame:F1} мс (захват + кодирование)");
        Console.WriteLine($"  максимум:      {fps:F1} fps");
        Console.WriteLine($"  размер кадра:  {lastSize / 1024} КБ");
        Console.WriteLine($"  файл:          {outputPath}");
        Console.WriteLine();

        return Task.CompletedTask;
    }

    private static long RunMjpeg(
        Capture.IScreenCapture capture, int frames, long quality, int maxWidth, Stopwatch stopwatch, string outputPath)
    {
        byte[]? lastFrame = null;

        for (var i = 0; i < frames; i++)
        {
            ReadOnlySpan<byte> frame;
            int stride;

            try
            {
                frame = capture.CaptureFrame(out stride);
            }
            catch (Exception ex)
            {
                Log.Error($"захват упал на кадре {i + 1}: {ex.Message}");
                Log.Info("в режиме auto стример откатится на gdi");
                return -1;
            }

            lastFrame = JpegEncoder.Encode(
                frame, capture.Width, capture.Height, stride, quality, maxWidth);
        }

        if (lastFrame is null)
        {
            Log.Error("кадры не получены");
            return -1;
        }

        File.WriteAllBytes(outputPath, lastFrame);
        return lastFrame.Length;
    }

    private static long RunH264(
        Capture.IScreenCapture capture, int frames, int maxWidth, Stopwatch stopwatch, string outputPath)
    {
        // картинка после масштабирования — те же размеры, что и в реальном стриме
        var scaledWidth = maxWidth > 0 && capture.Width > maxWidth
            ? maxWidth
            : capture.Width;
        var scaledHeight = Math.Max(2, (int)Math.Round(capture.Height * (double)scaledWidth / capture.Width));

        long total = 0;
        long count = 0;
        long last = 0;
        long keys = 0;
        using var encoder = new H264Encoder(scaledWidth, scaledHeight, 30, 2500);
        using var decoder = new H264Decoder();
        using var stream = new MemoryStream();
        var decoded = 0;
        System.Drawing.Bitmap? lastBitmap = null;

        for (var i = 0; i < frames; i++)
        {
            ReadOnlySpan<byte> frame;
            int stride;

            try
            {
                frame = capture.CaptureFrame(out stride);
            }
            catch (Exception ex)
            {
                Log.Error($"захват упал на кадре {i + 1}: {ex.Message}");
                return -1;
            }

            // h264-энкодер получает уже масштабированный кадр: как в реальном
            // стриме, прогоняем через тот же путь масштабирования
            var bgra = encoder.Fit(frame, capture.Width, capture.Height, stride);

            stopwatch.Start();
            var encoded = encoder.Encode(bgra, encoder.Width * 4, i * 1000L / 30, out var keyframe);
            stopwatch.Stop();

            if (encoded is not null)
            {
                total += encoded.Length;
                count++;
                last = encoded.Length;
                stream.Write(encoded);

                if (keyframe)
                {
                    keys++;
                }

                // проверяем, что поток реально декодируется: это ловит и ошибки
                // сборки access unit, и рассинхрон структуры энкодера
                var frameBitmap = decoder.Decode(encoded);
                if (frameBitmap is not null)
                {
                    decoded++;
                    lastBitmap?.Dispose();
                    lastBitmap = frameBitmap;
                }
            }
        }

        if (count == 0)
        {
            Log.Error("h264: ни одного фрейма на выходе");
            return -1;
        }

        File.WriteAllBytes(outputPath, stream.ToArray());

        var pngPath = Path.ChangeExtension(outputPath, ".png");
        lastBitmap?.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);

        Log.Info($"h264: фреймов {count}/{frames} (ключевых {keys}, декодировано {decoded}), " +
                 $"средний {total / count / 1024} КБ/кадр, последний {last / 1024} КБ, байт всего {total}");

        if (lastBitmap is not null)
        {
            Log.Info($"h264: кадр {lastBitmap.Width}x{lastBitmap.Height}, PNG {pngPath}");
            lastBitmap.Dispose();
        }

        return last;
    }
}
