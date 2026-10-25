using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;
using FreeDesk.Agent.Gui;

namespace FreeDesk.Agent.Viewer;

public sealed class ViewerForm : ModernWindow
{
    private readonly ViewerSession _session;
    private readonly Panel _canvas = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };
    private readonly Panel _displayBar = new() { Dock = DockStyle.Bottom, Height = 42, BackColor = Ui.Card, Visible = false };
    private readonly List<UiButton> _displayButtons = new();
    private IReadOnlyList<RemoteDisplay> _displays = Array.Empty<RemoteDisplay>();
    private int _activeDisplay;
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 30,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(16, 0, 16, 0),
        ForeColor = Ui.Muted,
        BackColor = Ui.Card,
        Font = new Font("Segoe UI", 9f),
    };

    private Bitmap? _current;
    private Bitmap? _pending;
    private int _scheduled;
    private int _rendered;
    private bool _dirty;

    /// <param name="standalone">Отдельное окно (свой заголовок) или встроенное в MainForm.</param>
    public ViewerForm(ViewerSession session, bool standalone = true)
    {
        _session = session;
        _session.PasswordPrompt ??= () => PromptDialog.AskPasswordAsync(this, "Удалённое устройство");

        Text = "FreeDesk — просмотр";
        Width = 1280;
        Height = 800;
        BackColor = Ui.Bg;

        if (standalone)
        {
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(0, CaptionHeight, 0, 0);
            SetCaption("FreeDesk", "· просмотр удалённого экрана");
            AddCaptionButtons();
        }

        typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(_canvas, true);

        _status.Paint += (_, e) =>
        {
            using var pen = new Pen(Ui.Line);
            e.Graphics.DrawLine(pen, 0, 0, _status.Width, 0);
        };

        _displayBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Ui.Line);
            e.Graphics.DrawLine(pen, 0, 0, _displayBar.Width, 0);
        };

        typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(_displayBar, true);

        // порядок добавления = порядок стыковки: полоса мониторов выше строки статуса
        Controls.Add(_canvas);
        Controls.Add(_displayBar);
        Controls.Add(_status);

        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseDown += (_, e) => SendButton(e.Button, down: true);
        _canvas.MouseUp += (_, e) => SendButton(e.Button, down: false);
        _canvas.MouseWheel += (_, e) => SendInput(new { t = "w", d = e.Delta });

        _canvas.Paint += (_, e) =>
        {
            if (_current is null) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            var rect = ImageRectangle();
            e.Graphics.DrawImage(_current, rect);
        };

        _session.FrameDecoded += OnFrame;
        _session.StatusChanged += OnStatus;
        _session.DisplaysChanged += OnDisplays;

        var displayTimer = new System.Windows.Forms.Timer { Interval = 33 };
        displayTimer.Tick += (_, _) =>
        {
            if (_dirty && !IsDisposed)
            {
                _dirty = false;
                _canvas.Invalidate();
            }
        };
        displayTimer.Start();
        Disposed += (_, _) => displayTimer.Stop();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _session.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ = _session.DisposeAsync();
        _current?.Dispose();
        Interlocked.Exchange(ref _pending, null)?.Dispose();
        base.OnFormClosed(e);
    }

    private void OnStatus(string text)
    {
        if (IsDisposed) return;
        BeginInvoke(() => _status.Text = text);
    }

    /// <summary>Хост пришёл со списком мониторов — рисуем кнопки переключения.</summary>
    private void OnDisplays(IReadOnlyList<RemoteDisplay> displays, int active)
    {
        if (IsDisposed) return;

        BeginInvoke(() =>
        {
            if (IsDisposed) return;

            _displays = displays;
            _activeDisplay = active;
            RebuildDisplayButtons();
        });
    }

    private void RebuildDisplayButtons()
    {
        foreach (var button in _displayButtons)
        {
            _displayBar.Controls.Remove(button);
            button.Dispose();
        }

        _displayButtons.Clear();

        // Один монитор — переключать нечего, полоса только крадёт место.
        if (_displays.Count <= 1)
        {
            _displayBar.Visible = false;
            return;
        }

        _displayBar.Visible = true;
        var x = 12;

        foreach (var display in _displays)
        {
            var active = display.Index == _activeDisplay;
            var button = new UiButton($"Монитор {display.Index + 1}")
            {
                Ghost = !active,
                Radius = 8,
                TabStop = false,
                Size = new Size(118, 28),
                Location = new Point(x, 7),
                TextColor = active ? Color.White : Ui.Muted,
            };

            button.Click += (_, _) =>
            {
                _session.SelectDisplay(display.Index);
                _canvas.Focus();
            };

            _displayBar.Controls.Add(button);
            _displayButtons.Add(button);
            x += button.Width + 8;
        }
    }

    private void OnFrame(Bitmap bitmap)
    {
        if (IsDisposed)
        {
            bitmap.Dispose();
            return;
        }

        var previous = Interlocked.Exchange(ref _pending, bitmap);
        previous?.Dispose();

        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
        {
            try
            {
                BeginInvoke(PumpFrame);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _scheduled, 0);
            }
        }
    }

    private void PumpFrame()
    {
        Interlocked.Exchange(ref _scheduled, 0);

        var bitmap = Interlocked.Exchange(ref _pending, null);
        if (bitmap is null) return;

        if (_rendered == 0)
        {
            Log.Info($"первый кадр: {bitmap.Width}x{bitmap.Height}");
        }

        _rendered++;

        var previous = _current;
        _current = bitmap;
        previous?.Dispose();
        _dirty = true;

        _status.Text = $"Подключено · {_rendered} кадров · {bitmap.Width}×{bitmap.Height}";
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        var rect = ImageRectangle();

        if (rect.Width <= 0 || rect.Height <= 0) return;

        var x = Math.Clamp((double)(e.X - rect.X) / rect.Width, 0, 1);
        var y = Math.Clamp((double)(e.Y - rect.Y) / rect.Height, 0, 1);

        SendInput(new { t = "m", x, y });
    }

    private void SendButton(MouseButtons button, bool down)
    {
        var name = button switch
        {
            MouseButtons.Right => "right",
            MouseButtons.Middle => "middle",
            _ => "left",
        };

        SendInput(new { t = "mb", b = name, d = down });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        SendInput(new { t = "k", vk = e.KeyValue, d = true });
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        SendInput(new { t = "k", vk = e.KeyValue, d = false });
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void SendInput(object message)
    {
        _session.SendInput(JsonSerializer.Serialize(message));
    }

    private Rectangle ImageRectangle()
    {
        var box = _canvas.ClientRectangle;

        if (_current is null || _current.Width <= 0 || _current.Height <= 0)
        {
            return box;
        }

        var imageAspect = (double)_current.Width / _current.Height;
        var boxAspect = (double)box.Width / box.Height;

        if (imageAspect > boxAspect)
        {
            var height = (int)(box.Width / imageAspect);
            return new Rectangle(box.X, box.Y + (box.Height - height) / 2, box.Width, height);
        }

        var width = (int)(box.Height * imageAspect);
        return new Rectangle(box.X + (box.Width - width) / 2, box.Y, width, box.Height);
    }
}
