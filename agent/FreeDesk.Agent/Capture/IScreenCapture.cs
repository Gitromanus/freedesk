namespace FreeDesk.Agent.Capture;

/// <summary>Источник кадров экрана. Кадр — BGRA, построчно сверху вниз.</summary>
public interface IScreenCapture : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Снимает кадр. Возвращает переиспользуемый буфер — копируйте, если нужно хранить.</summary>
    ReadOnlySpan<byte> CaptureFrame(out int stride);
}
