using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FreeDesk.Agent.Gui;

namespace FreeDesk.Agent.Gui;

/// <summary>
/// Полоска вкладок подключений. Каждая вкладка — отдельный дочерний контрол:
/// так клики и наведение обрабатывает сама Windows, без ручного расчёта попаданий.
/// </summary>
public sealed class SessionTabStrip : DoubleBufferedPanel
{
    public sealed class TabInfo
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public Color Dot { get; init; } = Ui.Muted;
        public bool Active { get; init; }
    }

    private const int ChipHeight = 30;
    private const int Gap = 6;
    private const int SidePadding = 4;

    private readonly List<TabChip> _chips = new();
    private TabChip? _plus;

    public event Action<int>? TabActivated;
    public event Action<int>? TabClosed;
    public event Action? NewTabClicked;

    public SessionTabStrip()
    {
        Height = ChipHeight + 8;
        BackColor = Ui.Bg;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // двойной клик не должен «съедать» второй MouseUp: каждый клик = активация
        SetStyle(ControlStyles.StandardDoubleClick, false);
    }

    public void SetTabs(IReadOnlyList<TabInfo> tabs, int activeIndex)
    {
        SuspendLayout();

        foreach (var chip in _chips)
        {
            Controls.Remove(chip);
            chip.Dispose();
        }

        _chips.Clear();

        if (_plus is not null)
        {
            Controls.Remove(_plus);
            _plus.Dispose();
            _plus = null;
        }

        var x = SidePadding;

        for (var i = 0; i < tabs.Count; i++)
        {
            var index = i;
            var info = tabs[i];

            var chip = new TabChip(info.Title, info.Subtitle, info.Dot, info.Active)
            {
                Location = new Point(x, 4),
            };

            chip.Activated += () => TabActivated?.Invoke(index);
            chip.Closed += () => TabClosed?.Invoke(index);

            Controls.Add(chip);
            _chips.Add(chip);

            x += chip.Width + Gap;
        }

        if (tabs.Count > 0)
        {
            _plus = new TabChip("+", "", Ui.Muted, false, isPlus: true)
            {
                Location = new Point(x, 4),
                Width = ChipHeight,
            };

            _plus.Activated += () => NewTabClicked?.Invoke();
            Controls.Add(_plus);
        }

        ResumeLayout();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var background = new SolidBrush(Ui.Bg);
        g.FillRectangle(background, ClientRectangle);

        using var line = new Pen(Ui.Line);
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>Одна вкладка: точка состояния, имя, ID и крестик закрытия.</summary>
public sealed class TabChip : Control
{
    private readonly string _title;
    private readonly string _subtitle;
    private readonly Color _dot;
    private readonly bool _active;
    private readonly bool _isPlus;

    private bool _hover;
    private bool _hoverClose;

    public event Action? Activated;
    public event Action? Closed;

    public TabChip(string title, string subtitle, Color dot, bool active, bool isPlus = false)
    {
        _title = title;
        _subtitle = subtitle;
        _dot = dot;
        _active = active;
        _isPlus = isPlus;

        Height = isPlus ? 30 : 30;
        Width = isPlus ? 30 : MeasureWidth(title);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // два быстрых клика = два клика, а не один double-click без MouseUp
        SetStyle(ControlStyles.StandardDoubleClick, false);
        Cursor = Cursors.Hand;
    }

    private static int MeasureWidth(string title)
    {
        using var font = new Font("Segoe UI", 9f);
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);

        var width = (int)Math.Ceiling(g.MeasureString(title, font).Width) + 62;
        return Math.Clamp(width, 126, 250);
    }

    private Rectangle CloseRect => new(Width - 32, 3, 26, 24);

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _hoverClose = false;
        Cursor = Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var overClose = !_isPlus && CloseRect.Contains(e.Location);

        if (overClose != _hoverClose)
        {
            _hoverClose = overClose;
            Cursor = overClose ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Log.Info($"вкладка: клик «{_title}» в {(CloseRect.Contains(e.Location) ? "крестик" : "тело")}");

        if (!ClientRectangle.Contains(e.Location))
        {
            return;
        }

        if (!_isPlus && CloseRect.Contains(e.Location))
        {
            Closed?.Invoke();
            return;
        }

        Activated?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        using (var path = Ui.RoundPath(rect, 8))
        {
            using var fill = new SolidBrush(_active ? Ui.CardDeep : Ui.Card);
            g.FillPath(fill, path);

            if (_hover && !_active)
            {
                using var hover = new SolidBrush(Ui.HoverOverlay);
                g.FillPath(hover, path);
            }

            using var pen = _active ? new Pen(Ui.Accent, 2f) : new Pen(Ui.Line);
            g.DrawPath(pen, path);
        }

        using var font = new Font("Segoe UI", 9f);
        using var subFont = new Font("Segoe UI", 7.75f);
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        if (_isPlus)
        {
            using var center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            using var brush = new SolidBrush(_hover ? Ui.Ink : Ui.Muted);
            g.DrawString("+", font, brush, new Rectangle(0, 0, Width, Height), center);
            return;
        }

        using (var dot = new SolidBrush(_dot))
        {
            g.FillEllipse(dot, 11, Height / 2 - 4, 8, 8);
        }

        var textLeft = 26;
        var textWidth = Width - 26 - 36;
        var hasSubtitle = !string.IsNullOrWhiteSpace(_subtitle);

        if (hasSubtitle)
        {
            using var titleBrush = new SolidBrush(_active ? Ui.Ink : Ui.Muted);
            g.DrawString(_title, font, titleBrush, new Rectangle(textLeft, 3, textWidth, 14), format);

            using var subBrush = new SolidBrush(Ui.Muted);
            g.DrawString(_subtitle, subFont, subBrush, new Rectangle(textLeft, 15, textWidth, 12), format);
        }
        else
        {
            using var titleBrush = new SolidBrush(_active ? Ui.Ink : Ui.Muted);
            g.DrawString(_title, font, titleBrush, new Rectangle(textLeft, 0, textWidth, Height), format);
        }

        // крестик показываем на активной вкладке и при наведении
        if (_active || _hoverClose)
        {
            var closeRect = CloseRect;

            if (_hoverClose)
            {
                using var path = Ui.RoundPath(closeRect, 5);
                using var hover = new SolidBrush(Ui.CloseRed);
                g.FillPath(hover, path);
            }

            using var center = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            using var brush = new SolidBrush(_hoverClose ? Color.White : Ui.Muted);
            g.DrawString("✕", font, brush, closeRect, center);
        }
    }
}
