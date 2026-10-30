using System.IO;
using FreeDesk.Agent.Files;
using FreeDesk.Agent.Gui;

namespace FreeDesk.Agent.Viewer;

/// <summary>
/// Передача файлов в духе Total Commander: две панели — «здесь» (машина зрителя)
/// и «там» (машина хоста), посередине кнопки «Отправить» и «Забрать», внизу
/// полоса прогресса с процентами. Работает только по прямому соединению:
/// в резервном режиме data channel нет, и гнать файлы через сигнальный сервер
/// нельзя (это убьёт хостинг).
/// </summary>
public sealed class FileTransferWindow : ModernWindow
{
    private sealed record Item(string Name, string Path, bool IsDirectory, long Size);

    private sealed record Job(bool Upload, string Path, string Name, long Size, string TargetDir);

    private readonly ViewerSession _session;
    private readonly CancellationTokenSource _cts = new();

    private readonly ListView _local = NewList();
    private readonly ListView _remote = NewList();
    private readonly ComboBox _localDrives = NewDrives();
    private readonly ComboBox _remoteDrives = NewDrives();
    private readonly Label _localPath = NewPath();
    private readonly Label _remotePath = NewPath();
    private readonly UiButton _sendBtn = new("Отправить →") { Radius = 8 };
    private readonly UiButton _fetchBtn = new("← Забрать") { Radius = 8, Ghost = true };
    private readonly UiButton _cancelBtn = new("Отмена") { Radius = 8, Ghost = true };
    private readonly Label _status = new();
    private readonly PaintedBar _bar = new();

    private string _localDir = FileWire.Downloads;
    private string _remoteDir = "";          // "" — корни дисков хоста
    private readonly Queue<Job> _queue = new();
    private int _queueLeft;
    private bool _running;
    private CancellationTokenSource? _jobCts;
    private int _lastIncomingId = -1;
    private TaskCompletionSource<string?>? _downloadDone;
    private string? _downloadName;
    private long _downloadSize;

    public FileTransferWindow(ViewerSession session)
    {
        _session = session;

        Text = "Передача файлов";
        Width = 940;
        Height = 600;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Bg;
        Padding = new Padding(0, CaptionHeight, 0, 0);

        SetCaption("Передача файлов", " · " + session.TargetId);
        AddCaptionButtons(closeOnly: true);

        _session.DownloadProgress += OnDownloadProgress;
        _session.UploadProgress += OnUploadProgress;
        _session.Ended += Close;

        BuildLayout();

        // По умолчанию — рабочие столы обеих машин (как в командере — привычные папки).
        _localDir = SafeDesktop();
        _ = LoadLocalAsync(_localDir);
        _ = LoadRemoteAsync("");
    }

    /// <summary>Рабочий стол этого ПК с запасным вариантом.</summary>
    private static string SafeDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        return Directory.Exists(desktop) ? desktop : FileWire.Downloads;
    }

    /// <summary>CenterParent работает только для модальных окон — центруемся сами.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        if (Owner is { } owner)
        {
            Location = new Point(
                Math.Max(0, owner.Location.X + (owner.Width - Width) / 2),
                Math.Max(0, owner.Location.Y + (owner.Height - Height) / 2));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            _session.DownloadProgress -= OnDownloadProgress;
            _session.UploadProgress -= OnUploadProgress;
        }

        base.Dispose(disposing);
    }

    // ───────────────────────────── раскладка ─────────────────────────────

    private void BuildLayout()
    {
        var middle = new Panel { Width = 120, Dock = DockStyle.Left, BackColor = Ui.CardDeep };

        _sendBtn.Size = new Size(104, 40);
        _sendBtn.Location = new Point(8, 160);
        _sendBtn.Click += (_, _) => EnqueueFrom(_local, upload: true);

        _fetchBtn.Size = new Size(104, 40);
        _fetchBtn.Location = new Point(8, 210);
        _fetchBtn.Click += (_, _) => EnqueueFrom(_remote, upload: false);

        middle.Controls.AddRange(new Control[] { _sendBtn, _fetchBtn });

        var bottom = new Panel
        {
            Height = 74,
            Dock = DockStyle.Bottom,
            BackColor = Ui.Card,
            Padding = new Padding(16, 14, 16, 10),
        };

        // Dock вместо Anchor: анкоры считались до dock-раскладки панели, и полоса
        // уезжала за край. Порядок добавления важен: последняя добавленная
        // раскладывается первой (кнопка забирает правый край, полоса — остаток).
        _cancelBtn.Size = new Size(70, 46);
        _cancelBtn.Dock = DockStyle.Right;
        _cancelBtn.Enabled = false;
        _cancelBtn.Click += (_, _) => _jobCts?.Cancel();

        _status.Dock = DockStyle.Top;
        _status.Height = 22;
        _status.ForeColor = Ui.Muted;
        _status.Text = RelayNotice() is { } notice ? notice : "Выберите файлы и нажмите «Отправить» или «Забрать»";

        _bar.Dock = DockStyle.Top;
        _bar.Height = 14;

        bottom.Controls.Add(_bar);
        bottom.Controls.Add(_status);
        bottom.Controls.Add(_cancelBtn);

        // Порядок важен: Fill-панель добавляем первой, иначе она заберёт всё место.
        Controls.Add(MakePane(_remote, _remoteDrives, _remotePath, isRemote: true));
        Controls.Add(middle);
        Controls.Add(bottom);
        Controls.Add(MakePane(_local, _localDrives, _localPath, isRemote: false));
    }

    private Panel MakePane(ListView list, ComboBox drives, Label path, bool isRemote)
    {
        var pane = new Panel
        {
            Dock = isRemote ? DockStyle.Fill : DockStyle.Left,
            Width = (ClientSize.Width - 120) / 2,
            BackColor = Ui.Bg,
            Padding = new Padding(8, 8, 4, 8),
        };

        drives.SetBounds(8, 8, 120, 28);
        drives.SelectedIndexChanged += (_, _) =>
        {
            if (drives.SelectedItem is not DriveItem { FullName: { } target })
            {
                return;
            }

            // Навигация только если выбранный диск не тот, где уже стоим.
            var current = isRemote ? _remoteDir : _localDir;

            if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                _ = (isRemote ? LoadRemoteAsync(target) : LoadLocalAsync(target));
            }
        };

        var up = new UiButton("↑") { Radius = 8, Ghost = true, Size = new Size(36, 28) };
        up.Location = new Point(136, 8);
        up.Click += (_, _) =>
        {
            if (isRemote)
            {
                // "C:\Users" → "C:\", "C:\" → корни дисков ("").
                var trimmed = _remoteDir.TrimEnd('\\');
                var cut = trimmed.LastIndexOf('\\');
                var parent = cut switch
                {
                    < 0 => "",
                    <= 2 when trimmed.Length > 1 && trimmed[1] == ':' => trimmed[..3],
                    <= 2 => "",
                    _ => trimmed[..(cut + 1)],
                };

                _ = LoadRemoteAsync(parent);
            }
            else
            {
                var parent = Path.GetDirectoryName(_localDir.TrimEnd('\\'));

                if (parent is { Length: > 0 })
                {
                    _ = LoadLocalAsync(parent);
                }
            }
        };

        path.SetBounds(180, 12, pane.Width - 188, 22);
        path.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        list.SetBounds(8, 44, pane.Width - 12, pane.Height - 52);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count != 1)
            {
                return;
            }

            var item = (Item) list.SelectedItems[0].Tag!;

            if (item.IsDirectory)
            {
                _ = (isRemote ? LoadRemoteAsync(item.Path) : LoadLocalAsync(item.Path));
            }
            else
            {
                EnqueueFrom(list, upload: !isRemote);
            }
        };

        pane.Controls.AddRange(new Control[] { list, drives, up, path });
        return pane;
    }

    private string? RelayNotice() =>
        _session.RelayActive
            ? "Прямое соединение не установлено — передача файлов работает только без резервного канала"
            : null;

    // ───────────────────────────── загрузка каталогов ─────────────────────────────

    private static ListView NewList()
    {
        var list = new ListView
        {
            View = View.Details,
            BorderStyle = BorderStyle.None,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            BackColor = Ui.Field,
            ForeColor = Ui.Ink,
            Font = new Font("Segoe UI", 9.75f),
            HeaderStyle = ColumnHeaderStyle.Clickable,
        };

        list.Columns.Add("Имя", 250);
        list.Columns.Add("Размер", 90, HorizontalAlignment.Right);
        Ui.EnableDoubleBuffer(list);
        Ui.ApplyDarkScrollbar(list);
        return list;
    }

    private static ComboBox NewDrives()
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Ui.Field,
            ForeColor = Ui.Ink,
        };

        return combo;
    }

    private static Label NewPath()
    {
        var label = new Label
        {
            AutoEllipsis = true,
            ForeColor = Ui.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        return label;
    }

    private sealed record DriveItem(string Label, string FullName)
    {
        public override string ToString() => Label;
    }

    private static void FillDrives(ComboBox combo)
    {
        combo.Items.Clear();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            combo.Items.Add(new DriveItem(drive.Name, drive.Name));
        }
    }

    private async Task LoadLocalAsync(string path)
    {
        var listing = await Task.Run(() =>
        {
            try
            {
                var items = new List<Item>();

                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    var info = new DirectoryInfo(dir);

                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    items.Add(new Item(info.Name, info.FullName, true, 0));
                }

                foreach (var file in Directory.EnumerateFiles(path))
                {
                    var info = new FileInfo(file);

                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    items.Add(new Item(info.Name, info.FullName, false, info.Length));
                }

                return (Ok: true, Items: Sort(items), Error: (string?) null);
            }
            catch (Exception ex)
            {
                return (Ok: false, Items: new List<Item>(), Error: ex.Message);
            }
        });

        if (!listing.Ok)
        {
            return;
        }

        _localDir = path;
        _localPath.Text = path;
        FillDrives(_localDrives);
        ShowItems(_local, listing.Items);
    }

    private async Task LoadRemoteAsync(string path)
    {
        if (_session.RelayActive)
        {
            return;
        }

        var listing = await _session.BrowseAsync(path.Length == 0 ? null : path);

        if (listing is null || listing.Error is not null)
        {
            _remotePath.Text = listing?.Error ?? "хост не ответил";
            return;
        }

        // Корни дисков пришли — сразу уводим панель на рабочий стол хоста.
        if (listing.Path.Length == 0 && !string.IsNullOrEmpty(listing.Desktop))
        {
            _ = LoadRemoteAsync(listing.Desktop);
            return;
        }

        _remoteDir = listing.Path;
        _remotePath.Text = listing.Path.Length == 0 ? "Этот компьютер" : listing.Path;

        var items = listing.Entries
            .Select(e => new Item(e.Name, e.FullName ?? CombineRemote(listing.Path, e.Name), e.IsDirectory, e.Size))
            .ToList();

        // Ответ на «диски» — заполним выпадающий список корнями.
        if (listing.Path.Length == 0)
        {
            _remoteDrives.Items.Clear();

            foreach (var entry in listing.Entries)
            {
                if (entry.FullName is not null)
                {
                    _remoteDrives.Items.Add(new DriveItem(entry.Name, entry.FullName));
                }
            }
        }

        ShowItems(_remote, Sort(items));
    }

    private static string CombineRemote(string dir, string name)
    {
        if (dir.Length == 0)
        {
            return name;
        }

        return dir.EndsWith('\\') ? dir + name : dir + "\\" + name;
    }

    private static List<Item> Sort(List<Item> items) =>
        items
            .OrderBy(item => item.IsDirectory ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void ShowItems(ListView list, List<Item> items)
    {
        list.BeginUpdate();
        list.Items.Clear();

        foreach (var item in items)
        {
            var row = new ListViewItem(item.Name) { Tag = item };

            if (!item.IsDirectory)
            {
                row.SubItems.Add(FormatSize(item.Size));
            }
            else
            {
                row.SubItems.Add("");
            }

            list.Items.Add(row);
        }

        list.EndUpdate();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} МБ",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} ГБ",
    };

    // ───────────────────────────── очередь передач ─────────────────────────────

    private void EnqueueFrom(ListView list, bool upload)
    {
        if (_running || list.SelectedItems.Count == 0)
        {
            return;
        }

        if (!_session.IsConnected)
        {
            UpdateStatus("нет прямого соединения с хостом", Ui.Amber);
            return;
        }

        var jobs = list.SelectedItems
            .Cast<ListViewItem>()
            .Select(row => (Item) row.Tag!)
            .Where(item => !item.IsDirectory)
            .Select(item => new Job(upload, item.Path, item.Name, item.Size,
                upload ? _remoteDir : _localDir))
            .ToList();

        if (jobs.Count == 0)
        {
            return;
        }

        foreach (var job in jobs)
        {
            _queue.Enqueue(job);
        }

        _queueLeft = _queue.Count;
        _ = RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _sendBtn.Enabled = false;
        _fetchBtn.Enabled = false;
        _cancelBtn.Enabled = true;

        try
        {
            while (_queue.TryDequeue(out var job))
            {
                _jobCts = new CancellationTokenSource();
                UpdateStatus($"[{_queueLeft - _queue.Count}/{_queueLeft}] {job.Name}", Ui.Muted);
                _bar.Percent = 0;

                var ok = job.Upload
                    ? await RunUploadAsync(job, _jobCts.Token)
                    : await RunDownloadAsync(job, _jobCts.Token);

                if (!ok && _jobCts.IsCancellationRequested)
                {
                    UpdateStatus("передача прервана", Ui.Amber);
                    break;
                }

                // Файл появился на другой стороне — обновим ту панель.
                if (ok)
                {
                    _ = (job.Upload ? LoadRemoteAsync(_remoteDir) : LoadLocalAsync(_localDir));
                }
            }
        }
        finally
        {
            _running = false;
            _sendBtn.Enabled = true;
            _fetchBtn.Enabled = true;
            _cancelBtn.Enabled = false;
            _jobCts?.Dispose();
            _jobCts = null;

            if (_bar.Percent is > 0 and < 100)
            {
                _bar.Percent = 0;
            }
        }
    }

    private async Task<bool> RunUploadAsync(Job job, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? current = null;

        void OnProgress(string name, long sent, long total)
        {
            if (!string.Equals(name, job.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = name;
            _bar.Percent = total > 0 ? (int) (sent * 100 / total) : 0;
            UpdateStatus($"[{_queueLeft - _queue.Count}/{_queueLeft}] {job.Name}: {_bar.Percent}%", Ui.Muted);

            if (sent >= total)
            {
                done.TrySetResult(true);
            }
        }

        _session.UploadProgress += OnProgress;

        try
        {
            var send = _session.SendFileAsync(job.Path, job.TargetDir, ct);
            var finished = await Task.WhenAny(send, done.Task, Task.Delay(Timeout.Infinite, ct));

            if (finished == done.Task)
            {
                await send; // проглотить возможное исключение уже после завершения
                UpdateStatus($"[{_queueLeft - _queue.Count}/{_queueLeft}] {job.Name} — отправлено", Ui.Green);
                return true;
            }

            await finished; // проброс отмены
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            UpdateStatus($"ошибка: {ex.Message}", Ui.Red);
            return false;
        }
        finally
        {
            _session.UploadProgress -= OnProgress;
        }
    }

    private async Task<bool> RunDownloadAsync(Job job, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _downloadDone = done;
        _downloadName = job.Name;
        _downloadSize = job.Size;

        try
        {
            _session.RequestDownload(job.Path, job.TargetDir);

            var path = await done.Task.WaitAsync(TimeSpan.FromMinutes(30), ct);

            UpdateStatus($"[{_queueLeft - _queue.Count}/{_queueLeft}] {job.Name} — получено", Ui.Green);
            return path is not null;
        }
        catch (OperationCanceledException)
        {
            if (_lastIncomingId >= 0)
            {
                _session.AbortIncoming(_lastIncomingId);
            }

            return false;
        }
        catch (TimeoutException)
        {
            UpdateStatus($"{job.Name}: хост не ответил", Ui.Amber);
            return false;
        }
        finally
        {
            _downloadDone = null;
        }
    }

    private void OnUploadProgress(string name, long sent, long total)
    {
        if (!_running || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            _bar.Percent = total > 0 ? (int) Math.Min(100, sent * 100 / total) : 0;
        });
    }

    private void OnDownloadProgress(int id, string name, long written, long total, string? path)
    {
        _lastIncomingId = id;

        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            if (total > 0)
            {
                _bar.Percent = (int) Math.Min(100, written * 100 / total);
                UpdateStatus($"{name}: {_bar.Percent}%", Ui.Muted);
            }

            if (path is not null && _downloadDone is not null &&
                string.Equals(name, _downloadName, StringComparison.OrdinalIgnoreCase))
            {
                _downloadDone.TrySetResult(path);
            }
        });
    }

    private void UpdateStatus(string text, Color color)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            _status.Text = text;
            _status.ForeColor = color;
        });
    }

    /// <summary>Полоса прогресса: панель с закрашенным процентом — системный ProgressBar в тёмную тему не красится.</summary>
    private sealed class PaintedBar : Control
    {
        private int _percent;

        public int Percent
        {
            get => _percent;
            set
            {
                _percent = Math.Clamp(value, 0, 100);
                Invalidate();
            }
        }

        public PaintedBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Height = 14;
            BackColor = Ui.Field;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var graphics = e.Graphics;
            var width = (int) ((Width - 2) * (_percent / 100.0));

            using var background = new SolidBrush(Ui.Field);
            using var fill = new SolidBrush(Ui.Accent);

            graphics.FillRectangle(background, ClientRectangle);
            graphics.FillRectangle(fill, 1, 1, width, Height - 2);
        }
    }
}
