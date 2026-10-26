using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FreeDesk.Agent.Gui;

/// <summary>Палитра и общие элементы интерфейса (тёмная тема в стиле сайта).</summary>
public static class Ui
{
    public static readonly Color Bg = Color.FromArgb(0x0F, 0x17, 0x2A);
    public static readonly Color Card = Color.FromArgb(0x1E, 0x29, 0x3B);
    public static readonly Color CardDeep = Color.FromArgb(0x16, 0x20, 0x36);
    public static readonly Color Field = Color.FromArgb(0x0B, 0x12, 0x20);
    public static readonly Color Line = Color.FromArgb(0x2A, 0x38, 0x50);
    public static readonly Color Accent = Color.FromArgb(0x63, 0x66, 0xF1);
    public static readonly Color AccentHover = Color.FromArgb(0x81, 0x8C, 0xF8);
    public static readonly Color Ink = Color.FromArgb(0xF1, 0xF5, 0xF9);
    public static readonly Color Muted = Color.FromArgb(0x94, 0xA3, 0xB8);
    public static readonly Color Green = Color.FromArgb(0x22, 0xC5, 0x5E);
    public static readonly Color Amber = Color.FromArgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Red = Color.FromArgb(0xEF, 0x44, 0x44);
    public static readonly Color CloseRed = Color.FromArgb(0xE8, 0x11, 0x23);
    public static readonly Color HoverOverlay = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);

    private static bool? _hasMdl2;

    /// <summary>Есть ли системный шрифт иконок (Win10+) — иначе текстовые глифы.</summary>
    public static bool HasMdl2
    {
        get
        {
            if (_hasMdl2 is null)
            {
                try
                {
                    using var family = new FontFamily("Segoe MDL2 Assets");
                    _hasMdl2 = family.IsStyleAvailable(FontStyle.Regular);
                }
                catch
                {
                    _hasMdl2 = false;
                }
            }

            return _hasMdl2.Value;
        }
    }

    public static GraphicsPath RoundPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        if (d > bounds.Width) d = bounds.Width;
        if (d > bounds.Height) d = bounds.Height;

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    /// <summary>Включает двойную буферизацию у любого контрола (панели, списки).</summary>
    public static void EnableDoubleBuffer(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(control, true);
    }

    /// <summary>Кнопка-глиф для панели заголовка: свернуть/развернуть/закрыть.</summary>
    public static string Glyph(CaptionGlyph glyph)
    {
        if (HasMdl2)
        {
            return glyph switch
            {
                CaptionGlyph.Minimize => "\uE921",
                CaptionGlyph.Maximize => "\uE922",
                CaptionGlyph.Restore => "\uE923",
                CaptionGlyph.Close => "\uE8BB",
                CaptionGlyph.ChevronLeft => "\uE76B",
                CaptionGlyph.ChevronRight => "\uE76C",
                CaptionGlyph.ChevronDown => "\uE70D",
                CaptionGlyph.ChevronUp => "\uE70E",
                CaptionGlyph.Edit => "\uE70F",
                CaptionGlyph.Delete => "\uE74D",
                CaptionGlyph.Settings => "\uE713",
                CaptionGlyph.Monitor => "\uE7F4",
                CaptionGlyph.Power => "\uE7E8",
                CaptionGlyph.File => "\uE898",
                _ => "?",
            };
        }

        return glyph switch
        {
            CaptionGlyph.Minimize => "\u2014",
            CaptionGlyph.Maximize => "\u25A2",
            CaptionGlyph.Restore => "\u2750",
            CaptionGlyph.Close => "\u2715",
            CaptionGlyph.ChevronLeft => "\u2039",
            CaptionGlyph.ChevronRight => "\u203A",
            CaptionGlyph.ChevronDown => "\u25BE",
            CaptionGlyph.ChevronUp => "\u25B4",
            CaptionGlyph.Edit => "\u270E",
            CaptionGlyph.Delete => "\u2715",
            CaptionGlyph.Settings => "\u2699",
            CaptionGlyph.Monitor => "\u25A3",
            CaptionGlyph.Power => "\u23FB",
            CaptionGlyph.File => "\u2191",
            _ => "?",
        };
    }

    public static Font GlyphFont(float size) =>
        new(HasMdl2 ? "Segoe MDL2 Assets" : "Segoe UI", size);
}

public enum CaptionGlyph
{
    Minimize,
    Maximize,
    Restore,
    Close,
    ChevronLeft,
    ChevronRight,
    ChevronDown,
    ChevronUp,
    Edit,
    Delete,
    Settings,
    Monitor,
    Power,
    File,
}

/// <summary>Панель с двойной буферизацией: без мерцания при перерисовке.</summary>
public class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }
}

/// <summary>
/// Окно без системной рамки, но с нативным поведением: перетаскивание, ресайз
/// за края, Aero Snap и двойной клик по заголовку — через WM_NCHITTEST.
/// </summary>
public abstract class ModernWindow : Form
{
    public const int CaptionHeight = 40;
    private const int EdgeSize = 6;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private readonly List<CaptionButton> _buttons = new();
    private CaptionButton? _maxButton;

    private string _title = "";
    private string _titleSuffix = "";

    protected ModernWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        BackColor = Ui.Bg;
        Font = new Font("Segoe UI", 9.75f);
        KeyPreview = true;

        _resizeFilter = new ResizeFilter(this);
    }

    /// <summary>Заголовок: «{title} {suffix}», suffix рисуется приглушённым.</summary>
    protected void SetCaption(string title, string suffix = "")
    {
        _title = title;
        _titleSuffix = suffix;
        Invalidate(new Rectangle(0, 0, Width, CaptionHeight));
    }

    /// <summary>Добавляет кнопки свернуть/развернуть/закрыть в правый угол заголовка.</summary>
    /// <param name="closeOnly">Только «закрыть» — для диалогов, которым нечего разворачивать.</param>
    protected void AddCaptionButtons(bool closeOnly = false)
    {
        if (closeOnly)
        {
            var onlyClose = new CaptionButton(CaptionGlyph.Close) { IsClose = true };
            onlyClose.Click += Close;

            _buttons.Add(onlyClose);
            Controls.Add(onlyClose);
            LayoutCaptionButtons();
            return;
        }

        var min = new CaptionButton(CaptionGlyph.Minimize);
        var max = new CaptionButton(CaptionGlyph.Maximize);
        var close = new CaptionButton(CaptionGlyph.Close) { IsClose = true };

        min.Click += () => WindowState = FormWindowState.Minimized;
        max.Click += ToggleMaximized;
        close.Click += Close;

        _buttons.AddRange(new[] { min, max, close });
        _maxButton = max;

        Controls.AddRange(_buttons.Cast<Control>().ToArray());
        LayoutCaptionButtons();
    }

    protected void ToggleMaximized()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;

        Log.Info($"окно: {WindowState} {Bounds.Width}x{Bounds.Height}");
    }

    private void LayoutCaptionButtons()
    {
        var x = ClientSize.Width;
        var y = (CaptionHeight - CaptionButton.BarHeight) / 2;

        for (var i = _buttons.Count - 1; i >= 0; i--)
        {
            var button = _buttons[i];
            x -= button.Width;
            button.Location = new Point(x, y);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCaptionButtons();

        if (_maxButton is not null)
        {
            _maxButton.Glyph = WindowState == FormWindowState.Maximized
                ? CaptionGlyph.Restore
                : CaptionGlyph.Maximize;
        }

        Invalidate(new Rectangle(0, 0, Width, CaptionHeight + 1));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Application.AddMessageFilter(_resizeFilter);

        // Скруглённые углы Win11; на Win10 атрибут игнорируется — останутся прямые.
        var preference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        EnsureOnScreen();
        UpdateMaximizedBounds();
    }

    /// <summary>
    /// Windows помнит позицию окна: после смены конфигурации мониторов (или RDP)
    /// окно может оказаться на несуществующем экране — ничего не видно и не нажать.
    /// Центрируем на основном экране, а частично вылезшее — подтягиваем внутрь.
    /// </summary>
    private void EnsureOnScreen()
    {
        var area = Screen.FromRectangle(Bounds).WorkingArea;

        if (!area.IntersectsWith(Bounds) || Bounds.Width < 100 || Bounds.Height < 80)
        {
            var primary = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(
                primary.X + Math.Max(0, (primary.Width - Width) / 2),
                primary.Y + Math.Max(0, (primary.Height - Height) / 2));
            return;
        }

        var x = Math.Clamp(Bounds.X, area.X, Math.Max(area.X, area.Right - Width));
        var y = Math.Clamp(Bounds.Y, area.Y, Math.Max(area.Y, area.Bottom - Height));

        if (x != Bounds.X || y != Bounds.Y)
        {
            Location = new Point(x, y);
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (WindowState == FormWindowState.Normal)
        {
            UpdateMaximizedBounds();
        }
    }

    /// <summary>Безрамочное окно при развороте перекрывает панель задач — ограничиваем.</summary>
    private void UpdateMaximizedBounds()
    {
        var area = Screen.FromControl(this).WorkingArea;
        MaximizedBounds = new Rectangle(
            area.X + 1, area.Y + 1,
            Math.Max(0, area.Width - 2), Math.Max(0, area.Height - 2));
    }

    private int _resizeEdge;
    private bool _resizing;
    private Point _resizeStartCursor;
    private Rectangle _resizeStartBounds;
    private readonly ResizeFilter _resizeFilter;

    /// <summary>Тянем ли окно за кромку прямо сейчас.</summary>
    internal bool IsResizing => _resizing;

    internal void BeginResize(int edge)
    {
        _resizeEdge = edge;
        _resizing = true;
        _resizeStartCursor = Cursor.Position;
        _resizeStartBounds = Bounds;
        Capture = true;
    }

    internal void EndResize()
    {
        _resizing = false;
        _resizeEdge = 0;
        Capture = false;
    }

    /// <summary>
    /// Фильтр сообщений: клики по кромке уходят дочерним панелям (canvas, сайдбар),
    /// поэтому ловим их до того, как они дойдут до контролов.
    /// </summary>
    private sealed class ResizeFilter : IMessageFilter
    {
        private readonly ModernWindow _owner;
        private int _lastCursorEdge = -1;

        public ResizeFilter(ModernWindow owner) => _owner = owner;

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_LBUTTONDOWN = 0x201;
            const int WM_MOUSEMOVE = 0x200;
            const int WM_LBUTTONUP = 0x202;

            if (_owner.IsDisposed || !_owner.IsHandleCreated)
            {
                return false;
            }

            if (m.Msg == WM_LBUTTONDOWN && _owner.WindowState == FormWindowState.Normal)
            {
                if (!_owner.Bounds.Contains(Cursor.Position))
                {
                    return false;
                }

                var edge = _owner.EdgeAt(_owner.PointToClient(Cursor.Position));

                if (edge != 0)
                {
                    _owner.BeginResize(edge);
                    return true;
                }
            }
            else if (m.Msg == WM_MOUSEMOVE && _owner.IsResizing)
            {
                _owner.ResizeTo(Cursor.Position);
                return true;
            }
            else if (m.Msg == WM_MOUSEMOVE)
            {
                // Курсор-стрелки на кромках: сообщения о мыши получают дочерние
                // контролы, поэтому ставим курсор здесь.
                var edge = _owner.WindowState == FormWindowState.Maximized ||
                           !_owner.Bounds.Contains(Cursor.Position)
                    ? 0
                    : _owner.EdgeAt(_owner.PointToClient(Cursor.Position));

                if (edge != _lastCursorEdge)
                {
                    _lastCursorEdge = edge;
                    Cursor.Current = CursorForEdge(edge);
                }
            }
            else if (m.Msg == WM_LBUTTONUP && _owner.IsResizing)
            {
                _owner.EndResize();
                return true;
            }

            return false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;

        // Растягивание, курсор и захват кромок — в ResizeFilter: сюда клики по кромке
        // не доходят, их получают дочерние панели.

        if (m.Msg == WM_NCHITTEST)
        {
            // Координаты знаковые: на мультипмониторной конфигурации бывают отрицательные.
            var lParam = m.LParam.ToInt64();
            var screenPoint = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
            var p = PointToClient(screenPoint);

            // Кромки не отдаём системе (нет WS_THICKFRAME) — растягиваем сами.
            if (WindowState != FormWindowState.Maximized && EdgeAt(p) != 0)
            {
                m.Result = (IntPtr)HTCLIENT;
                return;
            }

            if (p.Y < CaptionHeight && p.X >= 0 && p.X < ClientSize.Width)
            {
                m.Result = (IntPtr)HTCAPTION;
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Application.RemoveMessageFilter(_resizeFilter);
        base.OnHandleDestroyed(e);
    }

    /// <summary>Какая кромка/угол окна под точкой (битовая маска 1=лево, 2=право, 4=верх, 8=низ).</summary>
    private int EdgeAt(Point p)
    {
        var edge = 0;

        if (p.X <= EdgeSize) edge |= 1;
        else if (p.X >= ClientSize.Width - EdgeSize) edge |= 2;

        if (p.Y <= EdgeSize) edge |= 4;
        else if (p.Y >= ClientSize.Height - EdgeSize) edge |= 8;

        return edge;
    }

    private static Cursor CursorForEdge(int edge) => edge switch
    {
        1 or 2 => Cursors.SizeWE,
        4 or 8 => Cursors.SizeNS,
        5 or 10 => Cursors.SizeNWSE,
        6 or 9 => Cursors.SizeNESW,
        _ => Cursors.Default,
    };

    /// <summary>Ручное растягивание безрамочного окна за кромки/углы.</summary>
    private void ResizeTo(Point cursor)
    {
        var dx = cursor.X - _resizeStartCursor.X;
        var dy = cursor.Y - _resizeStartCursor.Y;

        var left = _resizeStartBounds.Left;
        var top = _resizeStartBounds.Top;
        var right = _resizeStartBounds.Right;
        var bottom = _resizeStartBounds.Bottom;

        if ((_resizeEdge & 1) != 0) left += dx;
        if ((_resizeEdge & 2) != 0) right += dx;
        if ((_resizeEdge & 4) != 0) top += dy;
        if ((_resizeEdge & 8) != 0) bottom += dy;

        var minWidth = Math.Max(320, MinimumSize.Width);
        var minHeight = Math.Max(220, MinimumSize.Height);

        if (right - left < minWidth)
        {
            if ((_resizeEdge & 1) != 0) left = right - minWidth;
            else right = left + minWidth;
        }

        if (bottom - top < minHeight)
        {
            if ((_resizeEdge & 4) != 0) top = bottom - minHeight;
            else bottom = top + minHeight;
        }

        Bounds = new Rectangle(left, top, right - left, bottom - top);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var x = 16f;
        const float centerY = CaptionHeight / 2f;

        if (_title.Length > 0)
        {
            using var bold = new Font(Font, FontStyle.Bold);
            x = DrawText(g, _title, bold, Ui.Ink, x, centerY);
        }

        if (_titleSuffix.Length > 0)
        {
            _ = DrawText(g, "  " + _titleSuffix, Font, Ui.Muted, x, centerY);
        }

        using var pen = new Pen(Ui.Line);
        g.DrawLine(pen, 0, CaptionHeight - 1, Width, CaptionHeight - 1);
    }

    private static float DrawText(Graphics g, string text, Font font, Color color, float x, float centerY)
    {
        var size = g.MeasureString(text, font);
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, centerY - size.Height / 2f);
        return x + size.Width;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// Кнопка панели заголовка: компактная, скруглённая подсветка при наведении.
/// </summary>
public sealed class CaptionButton : Control
{
    public const int BarHeight = 30;

    private bool _hover;
    private bool _pressed;
    private CaptionGlyph _glyph;

    public CaptionButton(CaptionGlyph glyph)
    {
        _glyph = glyph;
        Size = new Size(40, BarHeight);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public CaptionGlyph Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
        }
    }

    public bool IsClose { get; init; }

    /// <summary>Текст вместо глифа — для компактных чипов (номера мониторов).</summary>
    public string? Label { get; set; }

    /// <summary>Подсветка выбранного чипа.</summary>
    public bool Active { get; set; }

    public new event Action? Click;

    protected override void OnMouseEnter(EventArgs e)
    {        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Click?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var radius = Label is null ? 6 : 5;

        if (Active)
        {
            var selected = new Rectangle(1, 1, Width - 3, Height - 3);
            using var selectedPath = Ui.RoundPath(selected, radius);
            using var accent = new SolidBrush(Ui.Accent);
            g.FillPath(accent, selectedPath);
        }
        else if (_hover)
        {
            var rect = new Rectangle(1, 1, Width - 3, Height - 3);
            using var path = Ui.RoundPath(rect, radius);
            using var brush = new SolidBrush(IsClose ? Ui.CloseRed : Ui.HoverOverlay);
            g.FillPath(brush, path);
        }

        var color = Active ? Color.White : !_hover ? Ui.Muted : _pressed ? Ui.Muted : Color.White;
        var text = Label ?? Ui.Glyph(Glyph);

        using var font = Label is null ? Ui.GlyphFont(8f) : new Font("Segoe UI", 8.25f, FontStyle.Bold);
        var size = g.MeasureString(text, font);
        using var textBrush = new SolidBrush(color);
        g.DrawString(text, font, textBrush, (Width - size.Width) / 2f, (Height - size.Height) / 2f);
    }
}

/// <summary>Кастомная плоская кнопка со скруглением и состояниями hover/pressed/disabled.</summary>
public sealed class UiButton : Control
{
    private bool _hover;
    private bool _pressed;

    public UiButton(string text)
    {
        Text = text;
        Height = 42;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public Color Fill { get; set; } = Ui.Accent;

    public Color FillHover { get; set; } = Ui.AccentHover;

    public Color FillPressed { get; set; } = Color.FromArgb(0x4F, 0x46, 0xE5);

    public Color DisabledFill { get; set; } = Ui.Line;

    public Color TextColor { get; set; } = Color.White;

    public Color DisabledText { get; set; } = Ui.Muted;

    /// <summary>Призрачная кнопка: фон карточки + тонкая рамка.</summary>
    public bool Ghost { get; set; }

    public int Radius { get; set; } = 10;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Ui.RoundPath(bounds, Radius);

        if (Ghost)
        {
            using var bg = new SolidBrush(_hover ? Ui.Card : Ui.CardDeep);
            g.FillPath(bg, path);
            using var border = new Pen(Ui.Line);
            g.DrawPath(border, path);
        }
        else
        {
            Color fill;
            if (!Enabled) fill = DisabledFill;
            else if (_pressed) fill = FillPressed;
            else if (_hover) fill = FillHover;
            else fill = Fill;

            using var bg = new SolidBrush(fill);
            g.FillPath(bg, path);
        }

        var textColor = !Enabled ? DisabledText : TextColor;
        TextRenderer.DrawText(
            g, Text, Font, bounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Тумблер-переключатель в тёмной теме.</summary>
public sealed class UiToggle : Control
{
    private bool _hover;
    private bool _checked;

    public UiToggle(string text)
    {
        Text = text;
        Height = 36;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 9f);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // двухстрочный текст справа от тумблера
        var textRect = new Rectangle(48, 0, Width - 48, Height);
        TextRenderer.DrawText(
            g, Text, Font, textRect,
            _hover ? Ui.Ink : Ui.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        // дорожка
        const int trackWidth = 38, trackHeight = 20;
        var track = new Rectangle(0, (Height - trackHeight) / 2, trackWidth, trackHeight);
        using var trackPath = Ui.RoundPath(track, trackHeight / 2);

        using (var fill = new SolidBrush(_checked ? Ui.Accent : Ui.Field))
        {
            g.FillPath(fill, trackPath);
        }

        using (var border = new Pen(_checked ? Ui.Accent : Ui.Line))
        {
            g.DrawPath(border, trackPath);
        }

        // ручка: справа = включено
        const int knobSize = 14;
        var knobX = _checked ? track.Right - knobSize - 3 : track.X + 3;
        var knob = new Rectangle(knobX, track.Y + (trackHeight - knobSize) / 2, knobSize, knobSize);
        using var knobFill = new SolidBrush(Color.White);
        g.FillEllipse(knobFill, knob);
    }
}

/// <summary>Поле ввода: скруглённая подложка, рамка подсвечивается при фокусе.</summary>
public sealed class UiInput : Panel
{
    public readonly TextBox Box = new();

    private bool _focused;

    public UiInput(string placeholder = "")
    {
        Height = 44;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        Box.BorderStyle = BorderStyle.None;
        Box.Font = new Font("Segoe UI", 11f);
        Box.ForeColor = Ui.Ink;
        Box.BackColor = Ui.Field;
        Box.PlaceholderText = placeholder;

        Box.Enter += (_, _) => { _focused = true; Invalidate(); };
        Box.Leave += (_, _) => { _focused = false; Invalidate(); };

        Resize += (_, _) => LayoutBox();
        Controls.Add(Box);
        LayoutBox();
    }

    /// <summary>TextBox однострочный и сам подбирает высоту — центрируем вручную.</summary>
    private void LayoutBox()
    {
        var pad = 14;
        Box.Location = new Point(pad, Math.Max(0, (Height - Box.PreferredHeight) / 2));
        Box.Size = new Size(Math.Max(0, Width - pad * 2), Box.PreferredHeight);
    }

    public string InputText
    {
        get => Box.Text;
        set => Box.Text = value;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Ui.RoundPath(bounds, 10);

        using var fill = new SolidBrush(Ui.Field);
        g.FillPath(fill, path);

        using var border = new Pen(_focused ? Ui.Accent : Ui.Line, _focused ? 1.6f : 1f);
        g.DrawPath(border, path);
    }
}
