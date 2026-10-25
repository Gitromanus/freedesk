namespace FreeDesk.Agent.Capture;

/// <summary>
/// Выбор источника захвата. DXGI быстрее и без разрывов, но недоступен в RDP-сессии
/// и на некоторых GPU — поэтому режим «auto» откатывается на GDI.
/// </summary>
public static class CaptureFactory
{
    /// <param name="display">Монитор; null — основной.</param>
    public static IScreenCapture Create(string mode, DisplayInfo? display = null)
    {
        display ??= Displays.Primary();

        // DXGI дублирует вывод адаптера целиком — умеем только основной монитор.
        // Остальные снимаем GDI по координатам монитора.
        if (display.Primary && mode != "gdi")
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
        else if (!display.Primary && mode == "dxgi")
        {
            Log.Warn($"dxgi умеет только основной монитор, для {display} использую gdi");
        }

        return new GdiScreenCapture(display);
    }
}
