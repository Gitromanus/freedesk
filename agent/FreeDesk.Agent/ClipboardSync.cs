using System.Text.Json;

namespace FreeDesk.Agent;

/// <summary>
/// Синхронизация текстового буфера обмена между сторонами сессии.
/// Локальный буфер опрашивается раз в секунду; изменение уезжает peer'у
/// сообщением {"t":"cb","v":...} по data channel. Эхо подавляется запоминанием
/// последнего отправленного и последнего примененного текста.
/// </summary>
public sealed class ClipboardSync
{
    private const int MaxTextChars = 256 * 1024;
    private static readonly TimeSpan StaTimeout = TimeSpan.FromMilliseconds(800);

    private string? _lastSent;
    private string? _lastApplied;

    /// <summary>Текст, который нужно отправить peer'у, или null.</summary>
    public string? PollOutgoing()
    {
        var text = GetText();
        if (text is null || text.Length > MaxTextChars)
        {
            return null;
        }

        if (text == _lastSent || text == _lastApplied)
        {
            return null;
        }

        _lastSent = text;
        return text;
    }

    /// <summary>Формирует JSON для отправки, если есть что послать.</summary>
    public string? PollOutgoingJson()
    {
        var text = PollOutgoing();
        return text is null ? null : JsonSerializer.Serialize(new { t = "cb", v = text });
    }

    /// <summary>Входящее сообщение от peer'а: применяем, если это не эхо.</summary>
    public void Incoming(JsonElement json)
    {
        if (json.TryGetProperty("v", out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextChars)
            {
                return;
            }

            if (text == _lastSent)
            {
                // peer вернул наш же текст — просто запоминаем, не трогая буфер
                _lastApplied = text;
                return;
            }

            SetText(text);
            _lastApplied = text;
            Log.Info($"буфер обмена ← peer ({text.Length} символов)");
        }
    }

    public void Reset()
    {
        _lastSent = null;
        _lastApplied = null;
    }

    /// <summary>Чтение/запись буфера требуют STA — фоновой нити агента это чуждо.</summary>
    private static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        var thread = new Thread(() => result = func())
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(StaTimeout);

        return result;
    }

    private static string? GetText() => RunSta<string?>(() =>
    {
        try
        {
            return System.Windows.Forms.Clipboard.ContainsText()
                ? System.Windows.Forms.Clipboard.GetText()
                : null;
        }
        catch
        {
            return null;
        }
    });

    private static void SetText(string text) => RunSta<object?>(() =>
    {
        try
        {
            System.Windows.Forms.Clipboard.SetText(text);
        }
        catch
        {
        }
        return null;
    });
}
