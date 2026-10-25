using System.Drawing;
using System.Windows.Forms;

namespace FreeDesk.Agent.Gui;

/// <summary>
/// Модальное окно в стиле приложения: подтверждение входящего подключения,
/// ввод текста (переименование) или пароль устройства. Только кнопка «закрыть»:
/// разворачивать и сворачивать диалогу нечего.
/// </summary>
public sealed class PromptDialog : ModernWindow
{
    private readonly UiInput _input;
    private readonly UiToggle? _remember;
    private DialogResult _result = DialogResult.Cancel;

    internal PromptDialog(string title, string message, string value, bool confirmMode, bool secretMode = false)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Width = 420;
        Height = confirmMode ? 210 : secretMode ? 306 : 240;
        BackColor = Ui.Card;
        Padding = new Padding(0, CaptionHeight, 0, 0);
        ControlBox = false;

        SetCaption(title);
        AddCaptionButtons(closeOnly: true);

        var messageHeight = confirmMode ? 48 : secretMode ? 46 : 32;

        var messageLabel = new Label
        {
            Text = message,
            Dock = DockStyle.Top,
            Height = messageHeight,
            Padding = new Padding(24, 8, 24, 0),
            ForeColor = Ui.Ink,
            Font = new Font("Segoe UI", 9.75f),
        };

        _input = new UiInput
        {
            Location = new Point(24, CaptionHeight + messageHeight + 8),
            Size = new Size(Width - 48, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Visible = !confirmMode,
        };

        if (!confirmMode)
        {
            _input.InputText = value;
        }

        if (secretMode)
        {
            _input.Box.UseSystemPasswordChar = true;

            _remember = new UiToggle("Запомнить пароль для этого устройства")
            {
                Location = new Point(24, _input.Bottom + 12),
                Size = new Size(Width - 48, 24),
                Visible = true,
            };
        }

        var accept = new UiButton(confirmMode ? "Разрешить" : "Сохранить")
        {
            Location = new Point(Width - 24 - 120 - 8, Height - 24 - 40),
            Size = new Size(120, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        accept.Click += (_, _) =>
        {
            _result = DialogResult.OK;
            Close();
        };

        var cancel = new UiButton("Отмена")
        {
            Ghost = true,
            Radius = 10,
            Location = new Point(Width - 24 - 120 - 8 - 120 - 8, Height - 24 - 40),
            Size = new Size(120, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        cancel.Click += (_, _) =>
        {
            _result = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(new Control[] { messageLabel, _input, accept, cancel });

        if (_remember is not null)
        {
            Controls.Add(_remember);
        }

        // Без явного фокуса ввод «в никуда»: набираешь пароль, а поле пустое.
        Shown += (_, _) =>
        {
            Activate();

            if (confirmMode)
            {
                accept.Focus();
            }
            else
            {
                _input.Box.Focus();
            }
        };

        // Enter подтверждает только ввод текста: принятие входящего подключения —
        // осознанный клик, случайный Enter не должен открывать доступ.
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (!confirmMode && e.KeyCode == Keys.Enter)
            {
                _result = DialogResult.OK;
                Close();
                e.SuppressKeyPress = true;
            }

            if (e.KeyCode == Keys.Escape)
            {
                _result = DialogResult.Cancel;
                Close();
            }
        };

        if (!confirmMode)
        {
            _input.Box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    _result = DialogResult.OK;
                    Close();
                    e.SuppressKeyPress = true;
                }
            };
        }
    }

    public string InputValue => _input.InputText;

    /// <summary>Галочка «запомнить» (есть только в режиме пароля).</summary>
    public bool Remember => _remember?.Checked ?? false;

    public new DialogResult ShowDialog(IWin32Window? owner)
    {
        base.ShowDialog(owner);
        return _result;
    }

    /// <summary>Спросить разрешение на входящее подключение (потокобезопасно).</summary>
    public static Task<bool> AskAsync(Form owner, string peerName, string peerId)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        owner.BeginInvoke(() =>
        {
            try
            {
                using var dialog = new PromptDialog(
                    "Входящее подключение",
                    $"{peerName} ({FormatId(peerId)}) запрашивает доступ к этому компьютеру.",
                    "", confirmMode: true);

                source.SetResult(dialog.ShowDialog(owner) == DialogResult.OK);
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        });

        return source.Task;
    }

    /// <summary>Спросить строку (например, путь к файлу на удалённой машине).</summary>
    public static Task<string?> AskTextAsync(Form owner, string title, string message, string value)
    {
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        owner.BeginInvoke(() =>
        {
            try
            {
                using var dialog = new PromptDialog(title, message, value, confirmMode: false);
                source.SetResult(dialog.ShowDialog(owner) == DialogResult.OK ? dialog.InputValue : null);
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        });

        return source.Task;
    }

    /// <summary>Спросить пароль устройства. null — пользователь отказался.</summary>
    public static Task<PasswordReply?> AskPasswordAsync(Form owner, string peerName)
    {
        var source = new TaskCompletionSource<PasswordReply?>(TaskCreationOptions.RunContinuationsAsynchronously);

        owner.BeginInvoke(() =>
        {
            try
            {
                using var dialog = new PromptDialog(
                    "Пароль устройства",
                    $"{peerName} требует пароль для подключения без подтверждения.",
                    "", confirmMode: false, secretMode: true);

                var result = dialog.ShowDialog(owner) == DialogResult.OK
                    ? new PasswordReply(dialog.InputValue, dialog.Remember)
                    : null;

                source.SetResult(result);
            }
            catch (Exception ex)
            {
                source.SetException(ex);
            }
        });

        return source.Task;
    }

    private static string FormatId(string id) =>
        id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;
}
