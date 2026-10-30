using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;
using FreeDesk.Agent.Gui;
using CaptionButton = FreeDesk.Agent.Gui.CaptionButton;

namespace FreeDesk.Agent.Viewer;

/// <summary>
/// Окно просмотра. Поверх картинки — компактная плавающая панель со значками
/// (мониторы, свернуть панель, отключиться), как в AnyDesk; она не отнимает
/// место у изображения и сворачивается в один значок.
/// </summary>
public sealed class ViewerForm : ModernWindow
{
    private readonly ViewerSession _session;
    private FileTransferWindow? _transferWindow;

    private readonly Panel _canvas = new() { Dock = DockStyle.Fill, BackColor = Ui.Bg };

    /// <summary>Одна полоса: мониторы + отключение + сворачивание (крайнее справа).</summary>
    private readonly DoubleBufferedPanel _bar = new();

    /// <summary>Свёрнутое состояние: один язычок-шеврон.</summary>
    private readonly DoubleBufferedPanel _mini = new();

    private readonly List<CaptionButton> _displayButtons = new();
    private readonly CaptionButton _collapseBtn = new(CaptionGlyph.ChevronUp) { Size = new Size(26, 24) };
    private readonly CaptionButton _powerBtn = new(CaptionGlyph.Power) { Size = new Size(26, 24) };
    private readonly CaptionButton _fileBtn = new(CaptionGlyph.File) { Size = new Size(26, 24) };
    private readonly CaptionButton _expandBtn = new(CaptionGlyph.ChevronDown) { Size = new Size(28, 22) };

    private readonly Label _toast = new();
    private readonly System.Windows.Forms.Timer _toastTimer = new();

    private IReadOnlyList<RemoteDisplay> _displays = Array.Empty<RemoteDisplay>();
    private int _activeDisplay;
    private bool _barCollapsed;

    private Bitmap? _current;
    private Bitmap? _pending;
    private int _scheduled;
    private int _rendered;
    private bool _dirty;
    private PointF? _pendingMove;
    private bool _fitted;

    /// <summary>Подогнать окно под разрешение удалённого экрана при подключении
    /// (больше моего — сжать, меньше — растянуть до оригинала). Из настроек.</summary>
    public bool AutoFitWindow { get; set; } = true;

    /// <summary>Нажали «отключиться» во встроенном режиме — решает владелец окна.</summary>
    public event Action? DisconnectRequested;

    /// <param name="standalone">Отдельное окно (свой заголовок) или встроенное в MainForm.</param>
    public ViewerForm(ViewerSession session, bool standalone = true)
    {
        _session = session;

        // Встроенное в MainForm окно не является top-level — диалог пароля там не покажется,
        // им занимается сам MainForm.
        if (standalone)
        {
            _session.PasswordPrompt ??= () => PromptDialog.AskPasswordAsync(this, "Удалённое устройство");
        }

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

        Controls.Add(_canvas);

        BuildToolbar();
        BuildToast();

        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseDown += (_, e) => SendButton(e.Button, down: true);
        _canvas.MouseUp += (_, e) => SendButton(e.Button, down: false);
        _canvas.MouseWheel += (_, e) => SendInput(new { t = "w", d = e.Delta });

        _canvas.Paint += (_, e) =>
        {
            if (_current is null) return;

            var rect = ImageRectangle();

            // Дешёвая интерполяция: HighQualityBicubic на каждом кадре забивал
            // поток интерфейса — на живом экране клики по вкладкам и панели
            // «пропадали». На 1:1 фильтр не нужен вовсе.
            e.Graphics.InterpolationMode =
                rect.Width == _current.Width && rect.Height == _current.Height
                    ? InterpolationMode.NearestNeighbor
                    : InterpolationMode.Bilinear;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(_current, rect);
        };

        _session.FrameDecoded += OnFrame;
        _session.StatusChanged += OnStatus;
        _session.DisplaysChanged += OnDisplays;

        var displayTimer = new System.Windows.Forms.Timer { Interval = 33 };
        displayTimer.Tick += (_, _) =>
        {
            // Движение мыши уходит не чаще 30 раз в секунду: в релей-режиме
            // каждое движение — отдельный HTTPS-запрос, лить их сотнями нельзя.
            if (_pendingMove is { } move && !IsDisposed)
            {
                _pendingMove = null;
                SendInput(new { t = "m", x = (double) move.X, y = (double) move.Y });
            }

            if (_dirty && !IsDisposed)
            {
                _dirty = false;
                _canvas.Invalidate();
            }
        };
        displayTimer.Start();
        Disposed += (_, _) => displayTimer.Stop();

        Resize += (_, _) => LayoutOverlays();
    }

    private void BuildToolbar()
    {
        _bar.BackColor = Ui.Card;
        _bar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // тень + светлая кромка: полоса не теряется на тёмном удалённом экране
            using var shadow = new Pen(Color.FromArgb(90, 0, 0, 0), 2f);
            e.Graphics.DrawPath(shadow, Ui.RoundPath(new Rectangle(1, 2, _bar.Width - 3, _bar.Height - 3), 10));

            using var pen = new Pen(Color.FromArgb(0x4C, 0x62, 0x86));
            using var path = Ui.RoundPath(new Rectangle(0, 0, _bar.Width - 1, _bar.Height - 1), 10);
            e.Graphics.DrawPath(pen, path);
        };

        _collapseBtn.TabStop = false;
        _collapseBtn.Click += () => SetBarCollapsed(!_barCollapsed);

        _powerBtn.TabStop = false;
        _powerBtn.Click += RequestDisconnect;

        _fileBtn.TabStop = false;
        _fileBtn.Click += ShowTransferWindow;

        // порядок: [мониторы…] [файл] [отключиться] [свернуть]
        _bar.Controls.Add(_fileBtn);
        _bar.Controls.Add(_powerBtn);
        _bar.Controls.Add(_collapseBtn);

        _mini.BackColor = Ui.CardDeep;
        _mini.Visible = false;
        _mini.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Ui.Line);
            using var path = Ui.RoundPath(new Rectangle(0, 0, _mini.Width - 1, _mini.Height - 1), 8);
            e.Graphics.DrawPath(pen, path);
        };

        _expandBtn.TabStop = false;
        _expandBtn.Click += () => SetBarCollapsed(false);
        _mini.Controls.Add(_expandBtn);

        // Панель живёт внутри канваса: он рисует видео, и только такой порядок
        // гарантирует, что полоса всегда поверх картинки.
        _canvas.Controls.Add(_bar);
        _canvas.Controls.Add(_mini);
        _bar.BringToFront();
        _mini.BringToFront();
    }

    private void BuildToast()
    {
        _toast.AutoSize = false;
        _toast.BackColor = Ui.CardDeep;
        _toast.ForeColor = Ui.Muted;
        _toast.Font = new Font("Segoe UI", 9f);
        _toast.TextAlign = ContentAlignment.MiddleLeft;
        _toast.Padding = new Padding(10, 0, 10, 0);
        _toast.Visible = false;
        _toast.Cursor = Cursors.Default;

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.Visible = false;
        };

        Controls.Add(_toast);
        _toast.BringToFront();
    }

    private void SetBarCollapsed(bool collapsed)
    {
        _barCollapsed = collapsed;

        // Свёрнуто — прячем всю полосу, остаётся только шеврон разворота.
        _bar.Visible = !collapsed;
        _mini.Visible = collapsed;

        LayoutOverlays();
    }

    /// <summary>Отступ плавающей полосы от верхнего края: чуть ниже заголовка, но не «в воздухе».</summary>
    private const int BarTop = 10;

    /// <summary>Плавающая полоса поверх картинки: мониторы, отключение, сворачивание.</summary>
    private void LayoutOverlays()
    {
        var x = 6;

        foreach (var chip in _displayButtons)
        {
            chip.Location = new Point(x, 5);
            x += chip.Width + 2;
        }

        if (_displayButtons.Count > 0)
        {
            x += 4;
        }

        _fileBtn.Location = new Point(x, 5);
        x += _fileBtn.Width + 2;

        _powerBtn.Location = new Point(x, 5);
        x += _powerBtn.Width + 2;

        _collapseBtn.Location = new Point(x, 5);
        x += _collapseBtn.Width + 6;

        _bar.Size = new Size(x, 34);
        _bar.Location = new Point(Math.Max(0, (_canvas.ClientSize.Width - _bar.Width) / 2), BarTop);

        _bar.Region?.Dispose();
        _bar.Region = new Region(Ui.RoundPath(new Rectangle(0, 0, _bar.Width, _bar.Height), 10));

        // Язычок свёрнутого состояния — там же, где была полоса.
        _mini.Size = new Size(32, 24);
        _mini.Location = new Point(Math.Max(0, (_canvas.ClientSize.Width - _mini.Width) / 2), BarTop);
        _expandBtn.Location = new Point(2, 1);

        _mini.Region?.Dispose();
        _mini.Region = new Region(Ui.RoundPath(new Rectangle(0, 0, _mini.Width, _mini.Height), 8));

        _toast.Location = new Point(12, Math.Max(12, ClientSize.Height - 34));
        _toast.Size = new Size(Math.Min(560, Math.Max(200, ClientSize.Width - 24)), 24);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Полоса видна всегда: на ней есть «отключиться» и «свернуть».
        _bar.Visible = !_barCollapsed;
        _mini.Visible = _barCollapsed;

        LayoutOverlays();
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

        BeginInvoke(() =>
        {
            if (IsDisposed) return;

            _toast.Text = text;
            _toast.Visible = true;
            _toast.BringToFront();

            var isProblem = text.Contains("ошиб", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("неверн", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("нет пароля", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("закрыт", StringComparison.OrdinalIgnoreCase);

            _toastTimer.Stop();
            _toastTimer.Interval = isProblem ? 8000 : 4000;
            _toastTimer.Start();
        });
    }

    /// <summary>Хост пришёл со списком мониторов — рисуем чипы 1/2/… на панели.</summary>
    private void OnDisplays(IReadOnlyList<RemoteDisplay> displays, int active)
    {
        if (IsDisposed) return;

        BeginInvoke(() =>
        {
            if (IsDisposed) return;

            _displays = displays;
            _activeDisplay = active;
            RebuildDisplayButtons();

            // Родное разрешение узнали — раз подгоняем окно (один раз за сессию).
            if (!_fitted && AutoFitWindow && displays.Count > 0)
            {
                _fitted = true;
                var display = displays.FirstOrDefault(d => d.Index == active) ?? displays[0];
                FitWindowTo(display.Width, display.Height);
            }
        });
    }

    /// <summary>Окно под удалённый экран: больше моего — сжимаем до рабочего стола,
    /// меньше — растягиваем до оригинала.</summary>
    private void FitWindowTo(int remoteWidth, int remoteHeight)
    {
        if (remoteWidth <= 0 || remoteHeight <= 0 || WindowState != FormWindowState.Normal)
        {
            return;
        }

        var work = Screen.FromControl(this).WorkingArea;
        var scale = Math.Min(1.0, Math.Min(work.Width / (double) remoteWidth, work.Height / (double) remoteHeight));
        var canvas = new Size((int) Math.Round(remoteWidth * scale), (int) Math.Round(remoteHeight * scale));

        if (TopLevel)
        {
            // Отдельное окно (--view): клиентская область = экран + своя шапка.
            ClientSize = new Size(canvas.Width, canvas.Height + CaptionHeight);
        }
        else if (ParentForm is MainForm main)
        {
            main.FitViewerTo(canvas, work);
        }

        Location = new Point(
            Math.Max(work.Left, work.Left + (work.Width - Width) / 2),
            Math.Max(work.Top, work.Top + (work.Height - Height) / 2));
    }

    private void RebuildDisplayButtons()
    {
        foreach (var button in _displayButtons)
        {
            _bar.Controls.Remove(button);
            button.Dispose();
        }

        _displayButtons.Clear();

        // Один монитор — переключать нечего.
        if (_displays.Count > 1)
        {
            foreach (var display in _displays)
            {
                var chip = new CaptionButton(CaptionGlyph.Monitor)
                {
                    Label = (display.Index + 1).ToString(),
                    Size = new Size(26, 24),
                    TabStop = false,
                    Active = display.Index == _activeDisplay,
                };

                chip.Click += () =>
                {
                    _session.SelectDisplay(display.Index);
                    _canvas.Focus();
                };

                _bar.Controls.Add(chip);
                _displayButtons.Add(chip);
            }
        }

        _bar.Visible = !_barCollapsed;
        _mini.Visible = _barCollapsed;

        LayoutOverlays();
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
    }

    /// <summary>Двухпанельное окно: файлы обеих машин, отправить/забрать, проценты.</summary>
    private void ShowTransferWindow()
    {
        if (_transferWindow is null || _transferWindow.IsDisposed)
        {
            _transferWindow = new FileTransferWindow(_session);
            _transferWindow.Show(this);
        }
        else
        {
            _transferWindow.Activate();
        }

        Focus();
    }

    private void RequestDisconnect()    {
        if (DisconnectRequested is { } handler)        {
            handler();
            return;
        }

        _ = _session.DisposeAsync();
        Close();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        var rect = ImageRectangle();

        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Не шлём сразу: последняя позиция уйдёт с ближайшим тиком таймера.
        _pendingMove = new PointF(
            (float) Math.Clamp((e.X - rect.X) / (double) rect.Width, 0, 1),
            (float) Math.Clamp((e.Y - rect.Y) / (double) rect.Height, 0, 1));
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

    /// <summary>
    /// Локальные горячие клавиши (в удалённую машину не уходят): Ctrl+Alt+B — панель,
    /// Ctrl+Alt+D — отключиться, Ctrl+Alt+1..9 — монитор. Через ProcessCmdKey:
    /// иначе Alt+клавиша уходит в обработку мнемоник и OnKeyDown её не видит.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if ((keyData & Keys.Control) == Keys.Control && (keyData & Keys.Alt) == Keys.Alt)
        {
            var key = keyData & Keys.KeyCode;

            if (key == Keys.B)
            {
                SetBarCollapsed(!_barCollapsed);
                return true;
            }

            if (key == Keys.D)
            {
                RequestDisconnect();
                return true;
            }

            var digit = key - Keys.D1;
            if (digit is >= 0 and <= 8 && _displays.Count > digit)
            {
                _session.SelectDisplay(_displays[digit].Index);
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Control && e.Alt)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        SendInput(new { t = "k", vk = e.KeyValue, d = true });
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Control && e.Alt)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

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
