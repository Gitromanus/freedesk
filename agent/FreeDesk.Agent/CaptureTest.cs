using System.Diagnostics;
using FreeDesk.Agent.Capture;
using FreeDesk.Agent.Codec;

namespace FreeDesk.Agent;

/// <summary>Проверка конвейера захвата: снимает N кадров, кодирует и сохраняет последний.</summary>
public static class CaptureTest
{
    public static Task RunAsync(int frames, long quality, int maxWidth, string captureMode = "auto")
    {
        using var capture = CaptureFactory.Create(captureMode);
        var outputPath = Path.Combine(Path.GetTempPath(), "freedesk-frame.jpg");

        Log.Info($"захват: {capture.GetType().Name} {capture.Width}x{capture.Height}, кадров {frames}, качество {quality}, ширина {maxWidth}");

        byte[]? lastFrame = null;
        var stopwatch = Stopwatch.StartNew();

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
                return Task.CompletedTask;
            }

            lastFrame = JpegEncoder.Encode(
                frame, capture.Width, capture.Height, stride, quality, maxWidth);
        }

        stopwatch.Stop();

        if (lastFrame is null)
        {
            Log.Error("кадры не получены");
            return Task.CompletedTask;
        }

        File.WriteAllBytes(outputPath, lastFrame);

        var perFrame = stopwatch.Elapsed.TotalMilliseconds / frames;
        var fps = 1000.0 / perFrame;

        Console.WriteLine();
        Console.WriteLine($"  кадров:        {frames}");
        Console.WriteLine($"  на кадр:       {perFrame:F1} мс (захват + JPEG)");
        Console.WriteLine($"  максимум:      {fps:F1} fps");
        Console.WriteLine($"  размер кадра:  {lastFrame.Length / 1024} КБ");
        Console.WriteLine($"  файл:          {outputPath}");
        Console.WriteLine();

        return Task.CompletedTask;
    }
}
