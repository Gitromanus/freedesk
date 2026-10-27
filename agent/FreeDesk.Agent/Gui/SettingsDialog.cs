using System.Drawing;
using System.Windows.Forms;

namespace FreeDesk.Agent.Gui;

/// <summary>
/// Окно настроек: имя устройства, сервер, параметры картинки и способ захвата.
/// Параметры стрима применяются к следующей сессии; сервер — после перезапуска.
/// </summary>
public sealed class SettingsDialog : ModernWindow
{
    private readonly AgentConfig _config;

    private readonly UiInput _nameInput = new("Имя этого устройства");
    private readonly UiInput _serverInput = new("https://…");
    private readonly UiInput _fpsInput = new("12");
    private readonly UiInput _qualityInput = new("80");
    private readonly UiInput _bitrateInput = new("3000");
    private readonly UiInput _widthInput = new("1920");
    private readonly UiInput _passwordInput = new("не задан");
    private readonly UiButton _clearPasswordBtn = new("Сбросить пароль") { Ghost = true, Radius = 10 };
    private readonly UiToggle _trayToggle = new("Сворачивать в трей\nпри закрытии окна");
    private readonly UiToggle _autostartToggle = new("Запускать при входе\nв Windows");
    private readonly UiButton _codecBtn = new("H.264 (openh264)") { Ghost = true, Radius = 10 };
    private readonly UiButton _captureBtn = new("Авто (DXGI → GDI)") { Ghost = true, Radius = 10 };

    private static readonly string[] Codecs = { "h264", "mjpeg" };
    private static readonly string[] CodecLabels = { "H.264 (openh264)", "MJPEG (JPEG по RTP)" };

    private static readonly string[] CaptureModes = { "auto", "dxgi", "gdi" };
    private static readonly string[] CaptureLabels =
    {
        "Авто (DXGI → GDI)",
        "Только DXGI",
        "Только GDI",
    };

    private int _captureIndex;
    private int _codecIndex;
    private bool _clearPassword;
    private readonly string _originalServer;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Card };
    private readonly Panel _footer = new() { Dock = DockStyle.Bottom, Height = 72, BackColor = Ui.Card };

    public bool ServerChanged { get; private set; }

    public SettingsDialog(AgentConfig config)
    {
        _config = config;
        _originalServer = config.Server.TrimEnd('/');

        Text = "Настройки";
        Width = 480;

        // На ноутбуках 800 px по высоте не влезают: ограничиваем по рабочему столу,
        // а содержимое делаем прокручиваемым.
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Height = Math.Min(900, Math.Max(480, workingArea.Height - 60));
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        Padding = new Padding(0, CaptionHeight, 0, 0);

        SetCaption("Настройки");
        AddCaptionButtons(closeOnly: true);

        typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(_content, true);

        Controls.Add(_content);
        Controls.Add(_footer);

        _captureIndex = Math.Clamp(Array.IndexOf(CaptureModes, config.Capture), 0, 2);
        _codecIndex = Math.Clamp(Array.IndexOf(Codecs, VideoCodecNames.Parse(config.Codec).ToWire()), 0, 1);

        const int x = 24;
        var w = Width - 48 - SystemInformation.VerticalScrollBarWidth - 8;
        var y = 12;

        y = AddField("ИМЯ УСТРОЙСТВА", _nameInput, y) + 6;
        _nameInput.InputText = config.Name;

        y = AddField("СЕРВЕР (ПОСЛЕ СМЕНЫ — ПЕРЕЗАПУСК)", _serverInput, y) + 6;
        _serverInput.InputText = config.Server;

        y = AddField("ЧАСТОТА КАДРОВ (1–60)", _fpsInput, y) + 6;
        _fpsInput.InputText = config.Fps.ToString();

        y = AddField("КАЧЕСТВО JPEG (10–100, ТОЛЬКО MJPEG)", _qualityInput, y) + 6;
        _qualityInput.InputText = config.JpegQuality.ToString();

        y = AddField("БИТРЕЙТ H.264, КБИТ/С", _bitrateInput, y) + 6;
        _bitrateInput.InputText = config.BitrateKbps.ToString();

        y = AddField("КОДЕК", _codecBtn, y) + 6;
        _codecBtn.Height = 40;
        _codecBtn.Click += (_, _) =>
        {
            _codecIndex = (_codecIndex + 1) % Codecs.Length;
            _codecBtn.Text = CodecLabels[_codecIndex];
        };
        _codecBtn.Text = CodecLabels[_codecIndex];

        y = AddField("МАКС. ШИРИНА КАДРА, PX (0 — БЕЗ МАСШТАБИРОВАНИЯ)", _widthInput, y) + 6;
        _widthInput.InputText = config.MaxWidth.ToString();

        // Пароль храним только хешем: показать текущий нельзя, только задать новый.
        // Менять и сбрасывать его вправе только тот, кто сидит за этой машиной
        // (зритель такого канала не имеет вообще), поэтому текущий пароль не спрашиваем.
        _passwordInput.Box.UseSystemPasswordChar = true;
        _passwordInput.Box.PlaceholderText = config.HasUnattendedPassword
            ? "задан — введите новый, чтобы сменить"
            : "не задан — подключение по подтверждению";

        y = AddField("ПАРОЛЬ ДЛЯ ДОСТУПА БЕЗ ПОДТВЕРЖДЕНИЯ", _passwordInput, y) + 2;

        if (config.HasUnattendedPassword)
        {
            var note = new Label
            {
                Text = "Пароль можно сменить или сбросить здесь без старого: доступ к настройкам = доступ к машине.",
                Font = new Font("Segoe UI", 8.25f),
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                AutoSize = false,
                Location = new Point(x, y),
                Size = new Size(Math.Max(200, Width - 56), 30),
            };

            _content.Controls.Add(note);
            y += 34;
        }

        _clearPasswordBtn.Location = new Point(x, y);
        _clearPasswordBtn.Size = new Size(180, 34);
        _clearPasswordBtn.Enabled = config.HasUnattendedPassword;
        _clearPasswordBtn.Click += (_, _) =>
        {
            var ok = MessageBox.Show(
                this,
                "Сбросить пароль? Подключения к этой машине будут требовать подтверждения вручную.",
                "FreeDesk",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) == DialogResult.OK;

            if (!ok)
            {
                return;
            }

            _clearPassword = true;
            _passwordInput.InputText = "";
            _clearPasswordBtn.Text = "пароль будет сброшен";
            _clearPasswordBtn.Enabled = false;
        };

        _content.Controls.Add(_clearPasswordBtn);

        // Сохранённые пароли других устройств — тоже личные данные, чистить надо уметь.
        var forgetBtn = new UiButton("Забыть пароли")
        {
            Ghost = true,
            Radius = 10,
            Size = new Size(150, 34),
            Location = new Point(x + 188, y),
            Enabled = config.SavedCredentials.Count > 0,
        };

        forgetBtn.Click += (_, _) =>
        {
            _config.ForgetSavedSecrets();
            _config.Save();
            forgetBtn.Text = "сохранённые пароли удалены";
            forgetBtn.Enabled = false;
        };

        _content.Controls.Add(forgetBtn);
        y += 34 + 6;

        y = AddField("ЗАХВАТ ЭКРАНА", _captureBtn, y) + 6;
        _captureBtn.Height = 40;
        _captureBtn.Click += (_, _) =>
        {
            _captureIndex = (_captureIndex + 1) % CaptureModes.Length;
            _captureBtn.Text = CaptureLabels[_captureIndex];
        };
        _captureBtn.Text = CaptureLabels[_captureIndex];

        // Поведение агента: трей и автозапуск — рядом, чтобы не тянуть диалог вниз.
        var half = (w - 12) / 2;

        _trayToggle.Checked = config.MinimizeToTray;
        _trayToggle.Location = new Point(x, y + 4);
        _trayToggle.Size = new Size(half, 40);

        _autostartToggle.Checked = Autostart.IsEnabled;
        _autostartToggle.Location = new Point(x + half + 12, y + 4);
        _autostartToggle.Size = new Size(half, 40);

        _content.Controls.AddRange(new Control[] { _trayToggle, _autostartToggle });
        y += 40 + 10;

        var save = new UiButton("Сохранить")
        {
            Size = new Size(120, 40),
            TabStop = false,
        };
        save.Click += (_, _) =>
        {
            if (Apply())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        var cancel = new UiButton("Отмена")
        {
            Ghost = true,
            Size = new Size(120, 40),
            TabStop = false,
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };

        int AddField(string caption, Control control, int top)
        {
            var label = new Label
            {
                Text = caption,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Ui.Muted,
                BackColor = Ui.Card,
                Location = new Point(x, top),
                AutoSize = true,
            };

            control.Location = new Point(x, top + 18);
            control.Size = new Size(w, 40);

            _content.Controls.AddRange(new Control[] { label, control });

            return top + 18 + 44;
        }

        _footer.Controls.AddRange(new Control[] { save, cancel });

        // позиции считаем по фактической ширине: до показа панель ещё не разложена
        _footer.Layout += (_, _) =>
        {
            cancel.Location = new Point(_footer.Width - 24 - 120 - 8 - 120, 16);
            save.Location = new Point(_footer.Width - 24 - 120, 16);
        };

        _footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Ui.Line);
            e.Graphics.DrawLine(pen, 0, 0, _footer.Width, 0);
        };
    }

    private bool Apply()
    {
        var name = _nameInput.InputText.Trim();
        if (name.Length == 0)
        {
            Flash("Имя не может быть пустым");
            return false;
        }

        var server = _serverInput.InputText.Trim().TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Flash("Сервер должен быть http(s)://адресом");
            return false;
        }

        if (!TryParseRange(_fpsInput.InputText, 1, 60, out var fps, "частота кадров")) return false;
        if (!TryParseRange(_qualityInput.InputText, 10, 100, out var quality, "качество JPEG")) return false;
        if (!TryParseRange(_bitrateInput.InputText, 200, 50000, out var bitrate, "битрейт H.264")) return false;
        if (!TryParseRange(_widthInput.InputText, 0, 7680, out var width, "ширина кадра")) return false;

        var newPassword = _passwordInput.InputText;
        if (!_clearPassword && newPassword.Length is > 0 and < 6)
        {
            Flash("пароль: не короче 6 символов (или оставьте поле пустым)");
            return false;
        }

        // Смена и сброс доступны без старого пароля: настройки открываются только
        // на самой машине, удалённый зритель такого пути не имеет.

        _config.Name = name;
        _config.Server = server;
        _config.Fps = fps;
        _config.JpegQuality = quality;
        _config.BitrateKbps = bitrate;
        _config.Codec = Codecs[_codecIndex];
        _config.MaxWidth = width;

        if (_clearPassword)
        {
            _config.ClearUnattendedPassword();
        }
        else if (newPassword.Length > 0)
        {
            _config.SetUnattendedPassword(newPassword);
        }

        _config.MinimizeToTray = _trayToggle.Checked;

        if (_autostartToggle.Checked != Autostart.IsEnabled)
        {
            if (_autostartToggle.Checked)
            {
                Autostart.Enable(AgentConfig.Profile);
            }
            else
            {
                Autostart.Disable();
            }
        }
        _config.Capture = CaptureModes[_captureIndex];
        _config.Save();

        ServerChanged = !string.Equals(_config.Server, _originalServer, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private bool TryParseRange(string text, int min, int max, out int value, string what)
    {
        if (int.TryParse(text.Trim(), out value) && value >= min && value <= max)
        {
            return true;
        }

        Flash($"{what}: целое число от {min} до {max}");
        return false;
    }

    private void Flash(string message) => MessageBox.Show(
        this, message, "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
