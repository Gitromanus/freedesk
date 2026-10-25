namespace FreeDesk.Agent.Capture;

/// <summary>
/// Выбор источника захвата. DXGI быстрее и без разрывов, но недоступен в RDP-сессии
/// и на некоторых GPU — поэтому режим «auto» откатывается на GDI.
/// </summary>
public static class CaptureFactory
{
    public static IScreenCapture Create(string mode)
    {
        if (mode != "gdi")
        {
            try
            {
                return new DxgiScreenCapture();
            }
            catch (Exception ex)
            {
                // Явно выбранный dxgi должен падать громко, auto — тихо откатываться.
                if (mode == "dxgi")
                {
                    throw new InvalidOperationException($"dxgi-захват недоступен: {ex.Message}", ex);
                }

                Log.Warn($"dxgi недоступен ({ex.Message}), использую gdi");
            }
        }

        return new GdiScreenCapture();
    }
}
