using System.Windows.Forms;

namespace FreeDesk.Agent.Capture;

/// <summary>Один монитор в виртуальных координатах рабочего стола.</summary>
public sealed record DisplayInfo(int Index, int X, int Y, int Width, int Height, bool Primary)
{
    public override string ToString() => $"#{Index} {Width}x{Height} @ {X},{Y}{(Primary ? " (основной)" : "")}";
}

/// <summary>
/// Список мониторов. Порядок детерминированный: основной всегда первый,
/// остальные — слева-направо и сверху-вниз, чтобы номер монитора не «прыгал»
/// между сессиями и одинаково понимался хостом и зрителем.
/// </summary>
public static class Displays
{
    public static IReadOnlyList<DisplayInfo> All()
    {
        var screens = Screen.AllScreens
            .OrderByDescending(s => s.Primary)
            .ThenBy(s => s.Bounds.X)
            .ThenBy(s => s.Bounds.Y)
            .ToList();

        var result = new List<DisplayInfo>(screens.Count);

        for (var i = 0; i < screens.Count; i++)
        {
            var bounds = screens[i].Bounds;
            result.Add(new DisplayInfo(i, bounds.X, bounds.Y, bounds.Width, bounds.Height, screens[i].Primary));
        }

        return result;
    }

    /// <summary>Монитор по номеру; неизвестный номер — основной (индекс 0).</summary>
    public static DisplayInfo ByIndex(int index)
    {
        var all = All();

        if (all.Count == 0)
        {
            throw new InvalidOperationException("не удалось перечислить мониторы");
        }

        return all.FirstOrDefault(d => d.Index == index) ?? all[0];
    }

    public static DisplayInfo Primary()
    {
        var all = All();
        return all.FirstOrDefault(d => d.Primary) ?? all[0];
    }
}
