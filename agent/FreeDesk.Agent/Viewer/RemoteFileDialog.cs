using System.Drawing;
using System.Windows.Forms;
using FreeDesk.Agent.Gui;

namespace FreeDesk.Agent.Viewer;

/// <summary>
/// Выбор файла на удалённой машине: листаем каталоги хоста и качаем выбранный —
/// вместо ручного ввода пути.
/// </summary>
public sealed class RemoteFileDialog : ModernWindow
{
    private readonly ViewerSession _session;
    private readonly Label _pathLabel = new();
    private readonly ListBox _list = new();
    private readonly UiButton _upBtn = new("Вверх") { Ghost = true, Radius = 8 };
    private readonly UiButton _downloadBtn = new("Скачать");
    private readonly string _saveTo;

    private string? _current;
    private string? _parent;

    public RemoteFileDialog(ViewerSession session, string saveTo)
    {
        _session = session;
        _saveTo = saveTo;

        Text = "Скачать файл";
        Width = 560;
        Height = 480;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Ui.Card;
        Padding = new Padding(0, CaptionHeight, 0, 0);
        ControlBox = false;

        SetCaption("Скачать файл");
        AddCaptionButtons(closeOnly: true);

        _pathLabel.Dock = DockStyle.Top;
        _pathLabel.Height = 40;
        _pathLabel.Padding = new Padding(16, 8, 16, 0);
        _pathLabel.ForeColor = Ui.Muted;
        _pathLabel.AutoEllipsis = true;
        _pathLabel.Text = "Загрузка…";

        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Ui.Field;
        _list.ForeColor = Ui.Ink;
        _list.Font = new Font("Segoe UI", 9.75f);
        _list.IntegralHeight = false;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Ui.Card };

        _upBtn.Size = new Size(96, 36);
        _upBtn.Location = new Point(16, 14);
        _upBtn.Click += (_, _) => _ = LoadAsync(_parent ?? _current);

        _downloadBtn.Size = new Size(120, 36);
        _downloadBtn.Location = new Point(Width - 16 - 120 - 8 - 120, 14);
        _downloadBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _downloadBtn.Enabled = false;
        _downloadBtn.Click += (_, _) => Accept();

        var cancel = new UiButton("Отмена") { Ghost = true, Radius = 8, Size = new Size(120, 36) };
        cancel.Location = new Point(Width - 16 - 120, 14);
        cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        cancel.Click += (_, _) => Close();

        footer.Controls.AddRange(new Control[] { _upBtn, _downloadBtn, cancel });

        Controls.Add(_list);
        Controls.Add(_pathLabel);
        Controls.Add(footer);

        _list.SelectedIndexChanged += (_, _) =>
        {
            _downloadBtn.Enabled = _list.SelectedItem is RemoteEntry { IsDirectory: false };
        };

        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItem is not RemoteEntry entry)
            {
                return;
            }

            if (entry.IsDirectory)
            {
                _ = LoadAsync(Path.Combine(_current ?? "", entry.Name));
            }
            else
            {
                Accept();
            }
        };

        Shown += (_, _) => _ = LoadAsync(null);
    }

    /// <summary>Куда сохранится файл — показываем сразу, чтобы не искать потом.</summary>
    public string? SelectedPath { get; private set; }

    private void Accept()
    {
        if (_list.SelectedItem is not RemoteEntry { IsDirectory: false } entry || _current is null)
        {
            return;
        }

        SelectedPath = Path.Combine(_current, entry.Name);
        Close();
    }

    private async Task LoadAsync(string? path)
    {
        var listing = await _session.BrowseAsync(path);

        if (IsDisposed)
        {
            return;
        }

        if (listing is null)
        {
            _pathLabel.Text = "не удалось получить список файлов";
            return;
        }

        _current = listing.Path;
        _parent = listing.Parent;

        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var entry in listing.Entries)
        {
            _list.Items.Add(entry);
        }

        _list.EndUpdate();

        _pathLabel.Text = listing.Error is null
            ? $"{listing.Path}   →   сохранится в {_saveTo}"
            : $"{listing.Path} — {listing.Error}";

        _downloadBtn.Enabled = false;
    }
}
