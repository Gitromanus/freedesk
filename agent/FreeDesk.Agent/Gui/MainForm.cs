using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using FreeDesk.Agent;
using FreeDesk.Agent.Viewer;

namespace FreeDesk.Agent.Gui;

public sealed class MainForm : ModernWindow
{
    private const int SidebarWidth = 340;
    private const int SidebarCollapsedWidth = 56;

    private readonly AgentConfig _config;
    private readonly SignalingClient _client;
    private readonly AgentRuntime _runtime;
    private readonly CancellationTokenSource _cts;

    private readonly DoubleBufferedPanel _root = new();
    private readonly DoubleBufferedPanel _sidebar = new();
    private readonly DoubleBufferedPanel _mainArea = new();
    private readonly DoubleBufferedPanel _hero = new();

    private readonly Label _idLabel = new();
    private readonly Label _statusText = new();
    private readonly UiInput _targetInput = new("9-значный ID");
    private readonly UiButton _connectBtn = new("Подключиться");
    private readonly UiToggle _autoAccept = new("Принимать входящие\nавтоматически");
    private readonly ListBox _recentList = new();
    private readonly Label _recentEmpty = new();
    private readonly CaptionButton _collapseBtn = new(CaptionGlyph.ChevronLeft);
    private readonly CaptionButton _settingsBtn = new(CaptionGlyph.Settings);

    private readonly DoubleBufferedPanel _viewBar = new();
    private readonly Label _viewTitle = new();
    private readonly UiButton _disconnectBtn = new("Отключиться") { Ghost = true, Radius = 8 };

    private ViewerForm? _viewerForm;
    private ViewerSession? _session;
    private bool _connecting;
    private bool _sidebarCollapsed;
    private bool _collapsedForSession;
    private int _hoveredRecent = -1;

    // автопереподключение при обрыве (не при ручном «Отключиться»)
    private string? _lastTarget;
    private int _reconnectAttempts;
    private bool _userDisconnected;

    private readonly string _recentPath;
    private Color _statusColor = Ui.Amber;
    private int _statusDotY;

    public MainForm(AgentConfig config, SignalingClient client, AgentRuntime runtime, CancellationTokenSource cts)
    {
        _config = config;
        _client = client;
        _runtime = runtime;
        _cts = cts;

        _recentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FreeDesk", "recent.json");

        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        Text = $"FreeDesk v{version}";
        Width = 1000;
        Height = 660;
        MinimumSize = new Size(780, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        Padding = new Padding(0, CaptionHeight, 0, 0);

        SetCaption("FreeDesk", "· v" + version);
        AddCaptionButtons();

        _root.Dock = DockStyle.Fill;
        _root.BackColor = Ui.Bg;
        _root.Resize += (_, _) => LayoutRoot();
        _root.Controls.Add(_mainArea);
        _root.Controls.Add(_sidebar);
        Controls.Add(_root);

        BuildSidebar();
        BuildMainArea();
        WireRuntime();
        LayoutRoot();
    }

    /// <summary>
    /// Ручной layout вместо Dock: Fill-панель в паре с Dock.Left из-за порядка
    /// dock-обработки занимала всё окно, и hero центрировался по всему окну,
    /// игнорируя сайдбар. Здесь правая область всегда строго правее панели.
    /// </summary>
    private void LayoutRoot()
    {
        _sidebar.Location = new Point(0, 0);
        _sidebar.Size = new Size(_sidebar.Width, Math.Max(0, _root.Height));

        _mainArea.Location = new Point(_sidebar.Width, 0);
        _mainArea.Size = new Size(Math.Max(0, _root.Width - _sidebar.Width), Math.Max(0, _root.Height));
    }

    // ─────────── боковая панель ───────────

    private void BuildSidebar()
    {
        _sidebar.Width = SidebarWidth;
        _sidebar.BackColor = Ui.CardDeep;
        _sidebar.Paint += DrawSidebarChrome;

        const int x = 28;
        const int w = SidebarWidth - 28 * 2;
        var y = 24;

        var idCaption = Caption("ВАШ ID", y);
        y += 22;

        _idLabel.Text = "--- --- ---";
        _idLabel.Font = new Font("Consolas", 20f, FontStyle.Bold);
        _idLabel.ForeColor = Ui.Ink;
        _idLabel.BackColor = Ui.CardDeep;
        _idLabel.Location = new Point(x, y);
        _idLabel.AutoSize = true;
        _idLabel.Cursor = Cursors.Hand;
        _idLabel.Click += (_, _) => CopyId();
        y += 38;

        var idHint = new Label
        {
            Text = "нажмите на ID, чтобы скопировать",
            Font = new Font("Segoe UI", 8.25f),
            ForeColor = Ui.Muted,
            BackColor = Ui.CardDeep,
            Location = new Point(x, y),
            AutoSize = true,
        };
        y += 30;

        // ── статус: точку рисует сама панель (DrawSidebarChrome) ──
        _statusDotY = y + 3;

        _statusText.Text = "Подключение к серверу…";
        _statusText.Font = new Font("Segoe UI", 9f);
        _statusText.ForeColor = Ui.Muted;
        _statusText.BackColor = Ui.CardDeep;
        _statusText.Location = new Point(x + 22, y);
        _statusText.Width = w - 22;
        _statusText.Height = 20;
        _statusText.AutoEllipsis = true;
        y += 32;

        Separator(y);
        y += 26;

        // ── подключение ──
        _ = Caption("ПОДКЛЮЧЕНИЕ К КОМПЬЮТЕРУ", y);
        y += 22;

        _targetInput.Location = new Point(x, y);
        _targetInput.Size = new Size(w, 44);
        _targetInput.Box.MaxLength = 11; // 9 цифр + пробелы
        y += 56;

        _connectBtn.Location = new Point(x, y);
        _connectBtn.Size = new Size(w, 44);
        _connectBtn.Click += (_, _) => _ = ConnectAsync();
        y += 62;

        _autoAccept.Location = new Point(x, y);
        _autoAccept.Width = w;
        _autoAccept.Height = 40;
        _autoAccept.Checked = _config.AutoAccept;
        _autoAccept.CheckedChanged += (_, _) =>
        {
            _config.AutoAccept = _autoAccept.Checked;
            _config.Save();
        };
        y += 46;

        Separator(y);
        y += 26;

        // ── недавние ──
        _ = Caption("НЕДАВНИЕ", y);
        y += 22;

        _recentList.DrawMode = DrawMode.OwnerDrawFixed;
        _recentList.ItemHeight = 44;
        _recentList.BorderStyle = BorderStyle.None;
        _recentList.BackColor = Ui.CardDeep;
        _recentList.ForeColor = Ui.Ink;
        _recentList.Location = new Point(x, y);
        _recentList.Size = new Size(w, 100);
        _recentList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
        _recentList.DrawItem += DrawRecentItem;
        _recentList.MouseDoubleClick += (_, _) => ConnectSelected();
        _recentList.MouseMove += OnRecentMouseMove;
        _recentList.MouseLeave += (_, _) => SetHoveredRecent(-1);
        _recentList.MouseDown += OnRecentMouseDown;
        Ui.EnableDoubleBuffer(_recentList);

        _recentEmpty.Text = "Здесь появятся устройства,\nк которым вы подключались";
        _recentEmpty.Font = new Font("Segoe UI", 9f);
        _recentEmpty.ForeColor = Ui.Muted;
        _recentEmpty.BackColor = Ui.CardDeep;
        _recentEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _recentEmpty.Location = new Point(x, y + 16);
        _recentEmpty.Size = new Size(w, 48);
        _recentEmpty.Anchor = AnchorStyles.Top | AnchorStyles.Left;

        // ── сворачивание панели и настройки ──
        _collapseBtn.Size = new Size(32, 32);
        _collapseBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _collapseBtn.Click += () => ToggleSidebar();

        _settingsBtn.Size = new Size(32, 32);
        _settingsBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _settingsBtn.Click += () => OpenSettings();

        _sidebar.Resize += (_, _) =>
        {
            _settingsBtn.Location = new Point(_sidebar.Width - _settingsBtn.Width - 12, 48);
            _collapseBtn.Location = new Point(_sidebar.Width - _collapseBtn.Width - 12, 10);
            _recentList.Height = Math.Max(48, _sidebar.Height - _recentList.Top - 20);
            _recentEmpty.Visible = !_sidebarCollapsed && _recentList.Items.Count == 0;
        };

        _sidebar.Controls.AddRange(new Control[]
        {
            idCaption, _idLabel, idHint, _statusText,
            _targetInput, _connectBtn, _autoAccept,
            _recentList, _recentEmpty, _collapseBtn, _settingsBtn,
        });
        _sidebar.BringToFront();

        Label Caption(string text, int top) => new()
        {
            Text = text,
            Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
            ForeColor = Ui.Muted,
            BackColor = Ui.CardDeep,
            Location = new Point(x, top),
            AutoSize = true,
        };

        void Separator(int top) => _separators.Add(top);
    }

    private readonly List<int> _separators = new();

    private void DrawSidebarChrome(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Ui.Line);

        // разделитель с правым краем панели
        e.Graphics.DrawLine(pen, _sidebar.Width - 1, 0, _sidebar.Width - 1, _sidebar.Height);

        // горизонтальные разделители на фиксированных позициях
        foreach (var y in _separators)
        {
            e.Graphics.DrawLine(pen, 28, y, _sidebar.Width - 28, y);
        }

        // цветная точка статуса
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(_statusColor);
        e.Graphics.FillEllipse(dot, 30, _statusDotY, 10, 10);
    }

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;

        if (_sidebarCollapsed)
        {
            _sidebar.Width = SidebarCollapsedWidth;
            _collapseBtn.Glyph = CaptionGlyph.ChevronRight;

            foreach (Control c in _sidebar.Controls)
            {
                if (c != _collapseBtn && c != _settingsBtn) c.Visible = false;
            }
        }
        else
        {
            _sidebar.Width = SidebarWidth;
            _collapseBtn.Glyph = CaptionGlyph.ChevronLeft;

            foreach (Control c in _sidebar.Controls)
            {
                c.Visible = true;
            }
        }

        LayoutRoot();
    }

    // ─────────── список недавних ───────────

    private void SetHoveredRecent(int index)
    {
        if (_hoveredRecent == index) return;
        _hoveredRecent = index;
        _recentList.Invalidate();
    }

    private void OnRecentMouseMove(object? sender, MouseEventArgs e)
    {
        var index = _recentList.IndexFromPoint(e.Location);
        SetHoveredRecent(index >= 0 ? index : -1);
    }

    private void OnRecentMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var index = _recentList.IndexFromPoint(e.Location);
        if (index < 0) return;

        var row = _recentList.GetItemRectangle(index);

        // значки действий в правой части строки: [✎][✕]
        if (e.X >= row.Right - 68 && e.X < row.Right - 40)
        {
            _recentList.SelectedIndex = index;
            RenameSelected();
            return;
        }

        if (e.X >= row.Right - 40)
        {
            _recentList.SelectedIndex = index;
            RemoveSelected();
        }
    }

    private void DrawRecentItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;

        var entry = (RecentEntry)_recentList.Items[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var active = e.Index == _hoveredRecent || (e.State & DrawItemState.Selected) != 0;

        if (active)
        {
            using var card = new SolidBrush(Ui.Card);
            using var path = Ui.RoundPath(new Rectangle(e.Bounds.X, e.Bounds.Y + 2, e.Bounds.Width, e.Bounds.Height - 4), 8);
            g.FillPath(card, path);
        }

        var dotColor = entry.IsOnline ? Ui.Green : Ui.Muted;
        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, e.Bounds.X + 6, e.Bounds.Y + e.Bounds.Height / 2 - 4, 8, 8);

        using var nameFont = new Font("Segoe UI", 9.75f);
        using var idFont = new Font("Consolas", 8.75f);

        var name = string.IsNullOrWhiteSpace(entry.Name) || entry.Name == entry.Id
            ? FormatId(entry.Id)
            : entry.Name;

        // обе надписи центрированы по вертикали строки; под ID отведено 84px,
        // чтобы «248 292 610» (Consolas) влезал целиком
        TextRenderer.DrawText(g, name, nameFont,
            new Rectangle(e.Bounds.X + 22, e.Bounds.Y, e.Bounds.Width - 22 - 130, e.Bounds.Height),
            Ui.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, FormatId(entry.Id), idFont,
            new Rectangle(e.Bounds.Right - 152, e.Bounds.Y, 84, e.Bounds.Height),
            Ui.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // значки «изменить» и «удалить» видны только на активной строке
        if (active)
        {
            using var iconFont = Ui.GlyphFont(9f);
            using var iconBrush = new SolidBrush(Ui.Muted);

            TextRenderer.DrawText(g, Ui.Glyph(CaptionGlyph.Edit), iconFont,
                new Rectangle(e.Bounds.Right - 68, e.Bounds.Y, 28, e.Bounds.Height),
                Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, Ui.Glyph(CaptionGlyph.Delete), iconFont,
                new Rectangle(e.Bounds.Right - 40, e.Bounds.Y, 28, e.Bounds.Height),
                Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ─────────── правая часть ───────────

    private void BuildMainArea()
    {
        _mainArea.BackColor = Ui.Bg;
        _mainArea.Padding = new Padding(24);

        BuildViewBar();

        _hero.Dock = DockStyle.Fill;
        _hero.BackColor = Ui.Bg;
        _hero.Paint += DrawHero;
        _mainArea.Controls.Add(_hero);
    }

    private void DrawHero(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // запас от краёв, чтобы ничего не обрезалось
        var bounds = Rectangle.Inflate(_hero.ClientRectangle, -32, -24);
        var centerX = bounds.X + bounds.Width / 2;

        // монитор
        const int mw = 128, mh = 84;
        var top = bounds.Y + Math.Max(0, (bounds.Height - 300) / 2);
        var screenRect = new Rectangle(centerX - mw / 2, top, mw, mh);

        using (var path = Ui.RoundPath(screenRect, 10))
        {
            using var fill = new SolidBrush(Ui.Card);
            g.FillPath(fill, path);
            using var border = new Pen(Ui.Line, 2f);
            g.DrawPath(border, path);
        }

        var inner = Rectangle.Inflate(screenRect, -8, -8);
        using (var path = Ui.RoundPath(inner, 6))
        {
            using var fill = new SolidBrush(Color.FromArgb(0x18, 0x22, 0x37));
            g.FillPath(fill, path);
        }

        DrawBolt(g, centerX, top + mh / 2 - 8);

        using (var stand = new Pen(Ui.Line, 3f))
        {
            g.DrawLine(stand, centerX, screenRect.Bottom, centerX, screenRect.Bottom + 12);
            stand.StartCap = LineCap.Round;
            stand.EndCap = LineCap.Round;
            g.DrawLine(stand, centerX - 18, screenRect.Bottom + 14, centerX + 18, screenRect.Bottom + 14);
        }

        // тексты — рисуются по прямоугольнику с выравниванием по центру,
        // переносом и эллипсисом: не вылезут и не обрежутся краем
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        var titleRect = new Rectangle(bounds.X, screenRect.Bottom + 36, bounds.Width, 40);
        using (var titleFont = new Font("Segoe UI", 15f, FontStyle.Bold))
        using (var titleBrush = new SolidBrush(Ui.Ink))
        {
            g.DrawString("Готов к подключению", titleFont, titleBrush, titleRect, format);
        }

        var subRect = new Rectangle(bounds.X + 40, titleRect.Bottom + 4, Math.Max(60, bounds.Width - 80), 60);
        using (var subFont = new Font("Segoe UI", 9.75f))
        using (var subBrush = new SolidBrush(Ui.Muted))
        {
            g.DrawString(
                "Передайте ваш ID второй стороне или введите чужой ID слева.\nВходящие запросы можно принимать автоматически.",
                subFont, subBrush, subRect, format);
        }
    }

    private static void DrawBolt(Graphics g, int cx, int cy)
    {
        var points = new[]
        {
            new PointF(cx + 3, cy - 16),
            new PointF(cx - 7, cy + 2),
            new PointF(cx - 1, cy + 2),
            new PointF(cx - 4, cy + 16),
            new PointF(cx + 7, cy - 3),
            new PointF(cx + 1, cy - 3),
        };

        using var path = new GraphicsPath();
        path.AddLines(points);
        path.CloseFigure();

        using var brush = new SolidBrush(Ui.Accent);
        g.FillPath(brush, path);
    }

    /// <summary>Панель над встроенным просмотром: кто на связи и кнопка «Отключиться».</summary>
    private void BuildViewBar()
    {
        _viewBar.Dock = DockStyle.Top;
        _viewBar.Height = 48;
        _viewBar.BackColor = Ui.Card;
        _viewBar.Padding = new Padding(16, 8, 12, 8);
        _viewBar.Visible = false;
        _viewBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Ui.Line);
            e.Graphics.DrawLine(pen, 0, _viewBar.Height - 1, _viewBar.Width, _viewBar.Height - 1);
        };

        _viewTitle.Font = new Font("Segoe UI", 9.75f);
        _viewTitle.ForeColor = Ui.Ink;
        _viewTitle.BackColor = Ui.Card;
        _viewTitle.Dock = DockStyle.Fill;
        _viewTitle.TextAlign = ContentAlignment.MiddleLeft;
        _viewTitle.AutoEllipsis = true;
        _viewTitle.Padding = new Padding(8, 0, 0, 0);

        _disconnectBtn.Size = new Size(124, 32);
        _disconnectBtn.Dock = DockStyle.Right;
        _disconnectBtn.Click += (_, _) => Disconnect();

        _viewBar.Controls.Add(_viewTitle);
        _viewBar.Controls.Add(_disconnectBtn);
        _mainArea.Controls.Add(_viewBar);
        _viewBar.BringToFront();
    }

    // ─────────── статус ───────────

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text, color));
            return;
        }

        _statusText.Text = text;
        _statusColor = color;
        _sidebar.Invalidate();
    }

    // ─────────── жизненный цикл ───────────

    private void WireRuntime()
    {
        _runtime.AcceptPrompt = async item =>
        {
            var accept = await PromptDialog.AskAsync(this, item.From.Name, item.From.PublicId);
            return accept;
        };

        _runtime.StatusChanged += text => SetStatus(text, Ui.Green);

        // Когда наш экран смотрят — окно уходит с пути и возвращается после.
        _runtime.SessionStarted += () => BeginInvoke(() =>
        {
            if (WindowState == FormWindowState.Normal)
            {
                WindowState = FormWindowState.Minimized;
            }
        });

        _runtime.SessionEnded += () => BeginInvoke(() =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
                Activate();
            }
        });

        Load += (_, _) => _ = StartAsync();
        FormClosing += (_, _) => _cts.Cancel();
    }

    private async Task StartAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_config.DeviceToken))
            {
                var registered = await _client.RegisterAsync(
                    _config.Name, $"windows {Environment.OSVersion.Version}", _cts.Token);

                _config.DeviceToken = registered.DeviceToken;
                _config.PublicId = registered.PublicId;
                _config.Save();

                SetStatus("Зарегистрировано, онлайн", Ui.Green);
            }
            else
            {
                await _client.PresenceAsync(_cts.Token);
                SetStatus("Онлайн, ожидание подключений", Ui.Green);
            }

            _idLabel.Text = FormatId(_config.PublicId ?? "?");
            LoadRecent();
            _ = Task.Run(() => _runtime.RunAsync(null, _cts.Token));
        }
        catch (Exception ex)
        {
            SetStatus("Нет связи с сервером: " + ex.Message, Ui.Red);
        }
    }

    // ─────────── подключение ───────────

    private async Task ConnectAsync(string? targetOverride = null)
    {
        if (_connecting) return;

        var target = (targetOverride ?? _targetInput.InputText).Trim().Replace(" ", "");
        if (target.Length != 9 || !long.TryParse(target, out _))
        {
            SetStatus("ID — девять цифр", Ui.Red);
            _targetInput.Box.Focus();
            return;
        }

        _connecting = true;
        _connectBtn.Enabled = false;
        _targetInput.Box.ReadOnly = true;
        SetStatus("Подключение…", Ui.Amber);

        try
        {
            Disconnect(keepStatus: true);

            _session = new ViewerSession(_client, target);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Connected += () => connected.TrySetResult(true);
            _session.StatusChanged += text => SetStatus(text, Ui.Amber);
            _session.Ended += OnUiSessionEnded;
            _session.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using (timeout.Token.Register(() => connected.TrySetResult(false)))
            {
                if (!await connected.Task)
                {
                    throw new TimeoutException("не дождались соединения");
                }
            }

            ShowViewer(target);
            SetStatus("Подключено", Ui.Green);
            _lastTarget = target;
            _reconnectAttempts = 0;
            _userDisconnected = false;

            var name = target;
            try
            {
                var resolved = await _client.ResolveAsync(target, _cts.Token);
                name = string.IsNullOrWhiteSpace(resolved.Name) ? target : resolved.Name;
            }
            catch
            {
            }

            _viewTitle.Text = $"{name} · {FormatId(target)}";
            AddRecent(target, name);
        }
        catch (Exception ex)
        {
            if (_session is not null)
            {
                _session.Ended -= OnUiSessionEnded;
                var failed = _session;
                _session = null;
                await failed.DisposeAsync();
            }

            SetStatus("Ошибка: " + ex.Message, Ui.Red);
        }
        finally
        {
            _connecting = false;
            _connectBtn.Enabled = true;
            _targetInput.Box.ReadOnly = false;
        }
    }

    private void ConnectSelected()
    {
        if (_recentList.SelectedItem is RecentEntry entry)
        {
            _ = ConnectAsync(entry.Id);
        }
    }

    private void ShowViewer(string target)
    {
        _hero.Visible = false;

        // экрану полезнее место — панель сама сворачивается
        if (!_sidebarCollapsed)
        {
            _collapsedForSession = true;
            ToggleSidebar();
        }

        if (_viewerForm is not null)
        {
            _mainArea.Controls.Remove(_viewerForm);
            _viewerForm.Dispose();
        }

        _viewerForm = new ViewerForm(_session!, standalone: false)
        {
            Dock = DockStyle.Fill,
            TopLevel = false,
            FormBorderStyle = FormBorderStyle.None,
        };

        _viewBar.Visible = true;
        _viewTitle.Text = $"Подключение · {FormatId(target)}";
        _mainArea.Controls.Add(_viewerForm);
        _viewerForm.BringToFront();
        _viewBar.BringToFront();
        _viewerForm.Show();
    }

    private void OnSessionEnded()
    {
        _session = null;
        _viewBar.Visible = false;

        if (_viewerForm is not null)
        {
            _mainArea.Controls.Remove(_viewerForm);
            _viewerForm.Dispose();
            _viewerForm = null;
        }

        // панель возвращается, если мы сворачивали её ради просмотра
        if (_collapsedForSession && _sidebarCollapsed)
        {
            _collapsedForSession = false;
            ToggleSidebar();
        }

        _hero.Visible = true;
        _hero.Invalidate();
        SetStatus("Соединение закрыто", Ui.Amber);
    }

    private void Disconnect(bool keepStatus = false)
    {
        _userDisconnected = true;
        _reconnectAttempts = 0;

        if (_session is not null)
        {
            _session.Ended -= OnUiSessionEnded;
            var session = _session;
            _session = null;
            _ = session.DisposeAsync();
        }

        OnSessionEnded();

        if (!keepStatus)
        {
            SetStatus("Отключено", Ui.Amber);
        }
    }

    private void OnUiSessionEnded() => BeginInvoke(() =>
    {
        // связь оборвалась сама — пробуем вернуть, пока не исчерпаны попытки
        if (!_userDisconnected && _viewerForm is not null && _lastTarget is not null &&
            _reconnectAttempts < 3 && !_cts.IsCancellationRequested)
        {
            _reconnectAttempts++;
            SetStatus($"Связь потеряна, переподключение {_reconnectAttempts} из 3…", Ui.Amber);

            var target = _lastTarget;
            Task.Delay(2000).ContinueWith(
                _ => { if (!_cts.IsCancellationRequested) BeginInvoke(() => _ = ConnectAsync(target)); },
                TaskScheduler.Default);

            return;
        }

        OnSessionEnded();
    });

    // ─────────── настройки ───────────

    private void OpenSettings()
    {
        using var dialog = new SettingsDialog(_config);

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (dialog.ServerChanged)
        {
            // на новом сервере этот токен никому не известен — регистрируемся заново
            _config.DeviceToken = null;
            _config.PublicId = null;
            _config.Save();

            MessageBox.Show(
                this,
                "Сервер изменён. Перезапустите приложение — оно зарегистрируется заново и получит новый ID.",
                "FreeDesk",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    // ─────────── буфер обмена ───────────

    private void CopyId()
    {
        if (string.IsNullOrEmpty(_config.PublicId)) return;

        try
        {
            Clipboard.SetText(FormatId(_config.PublicId));
            SetStatus("ID скопирован в буфер обмена", Ui.Green);
        }
        catch
        {
        }
    }

    // ─────────── недавние ───────────

    private void RenameSelected()
    {
        if (_recentList.SelectedItem is not RecentEntry entry) return;

        using var dialog = new PromptDialog(
            "Переименовать устройство", "Имя:",
            entry.Name == entry.Id ? "" : entry.Name, confirmMode: false);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var name = dialog.InputValue.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        entry.Name = name;
        SaveRecent();
        LoadRecent();
    }

    private void RemoveSelected()
    {
        if (_recentList.SelectedItem is not RecentEntry entry) return;
        _recentList.Items.Remove(entry);
        SaveRecent();
        _recentEmpty.Visible = _recentList.Items.Count == 0;
        SetHoveredRecent(-1);
    }

    private void LoadRecent()
    {
        _recentList.BeginUpdate();
        _recentList.Items.Clear();

        var entries = ReadRecent();
        foreach (var entry in entries)
        {
            _recentList.Items.Add(entry);
        }

        _recentList.EndUpdate();
        _recentEmpty.Visible = entries.Count > 0 ? false : !_sidebarCollapsed;
        _hoveredRecent = -1;

        // статус «в сети» подтягиваем в фоне
        _ = Task.Run(async () =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    var resolved = await _client.ResolveAsync(entry.Id, _cts.Token);
                    entry.IsOnline = resolved.Online;
                    if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name == entry.Id)
                    {
                        entry.Name = resolved.Name;
                    }
                }
                catch
                {
                    entry.IsOnline = false;
                }

                BeginInvoke(() =>
                {
                    var idx = _recentList.Items.IndexOf(entry);
                    if (idx >= 0)
                    {
                        _recentList.Items[idx] = entry;
                    }
                });
            }
        });
    }

    private List<RecentEntry> ReadRecent()
    {
        try
        {
            if (!File.Exists(_recentPath)) return new List<RecentEntry>();

            var json = File.ReadAllText(_recentPath);
            return JsonSerializer.Deserialize<List<RecentEntry>>(json) ?? new List<RecentEntry>();
        }
        catch
        {
            return new List<RecentEntry>();
        }
    }

    private void SaveRecent()
    {
        try
        {
            var entries = _recentList.Items.Cast<RecentEntry>().ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_recentPath)!);
            File.WriteAllText(_recentPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void AddRecent(string id, string name)
    {
        var entries = ReadRecent();
        var existing = entries.FirstOrDefault(e => e.Id == id);

        if (existing is not null)
        {
            existing.Name = name;
            existing.LastConnected = DateTime.Now;
        }
        else
        {
            entries.Insert(0, new RecentEntry { Id = id, Name = name, LastConnected = DateTime.Now });
        }

        entries = entries.OrderByDescending(e => e.LastConnected).Take(20).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(_recentPath)!);
        File.WriteAllText(_recentPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));

        LoadRecent();
    }

    private static string FormatId(string id) =>
        id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

    private sealed class RecentEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime LastConnected { get; set; }
        public bool IsOnline { get; set; }

        public override string ToString() =>
            string.IsNullOrWhiteSpace(Name) || Name == Id ? FormatId(Id) : Name;
    }
}
