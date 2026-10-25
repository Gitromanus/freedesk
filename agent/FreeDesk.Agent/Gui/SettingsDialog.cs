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
    private readonly string _originalServer;

    public bool ServerChanged { get; private set; }

    public SettingsDialog(AgentConfig config)
    {
        _config = config;
        _originalServer = config.Server.TrimEnd('/');

        Text = "Настройки";
        Width = 480;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        Padding = new Padding(0, CaptionHeight, 0, 0);

        SetCaption("Настройки");
        AddCaptionButtons();

        _captureIndex = Math.Clamp(Array.IndexOf(CaptureModes, config.Capture), 0, 2);
        _codecIndex = Math.Clamp(Array.IndexOf(Codecs, VideoCodecNames.Parse(config.Codec).ToWire()), 0, 1);

        const int x = 24;
        var w = Width - 48;
        var y = CaptionHeight + 12;

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

        y = AddField("ЗАХВАТ ЭКРАНА", _captureBtn, y) + 6;
        _captureBtn.Height = 40;
        _captureBtn.Click += (_, _) =>
        {
            _captureIndex = (_captureIndex + 1) % CaptureModes.Length;
            _captureBtn.Text = CaptureLabels[_captureIndex];
        };
        _captureBtn.Text = CaptureLabels[_captureIndex];

        var save = new UiButton("Сохранить")
        {
            Location = new Point(Width - 24 - 120, Height - 24 - 40),
            Size = new Size(120, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
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
            Location = new Point(Width - 24 - 120 - 8 - 120, Height - 24 - 40),
            Size = new Size(120, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
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

            Controls.AddRange(new Control[] { label, control });

            return top + 18 + 44;
        }

        Controls.AddRange(new Control[] { save, cancel });
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

        _config.Name = name;
        _config.Server = server;
        _config.Fps = fps;
        _config.JpegQuality = quality;
        _config.BitrateKbps = bitrate;
        _config.Codec = Codecs[_codecIndex];
        _config.MaxWidth = width;
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
