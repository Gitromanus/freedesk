using Microsoft.Win32;

namespace FreeDesk.Agent;

/// <summary>
/// Автозапуск агента при входе в Windows: значение в HKCU\...\Run.
/// HKCU, а не HKLM — не требует прав администратора и действует для текущего пользователя.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FreeDesk Agent";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    /// <summary>Текущая команда автозапуска — для диагностики и настроек.</summary>
    public static string? CurrentCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) as string;
        }
    }

    /// <summary>Прописать автозапуск: агент стартует без окна, сразу в трей.</summary>
    public static bool Enable(string profile)
    {
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe))
        {
            return false;
        }

        var command = $"\"{exe}\" --gui --tray --profile \"{profile}\"";

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (key is null)
        {
            return false;
        }

        key.SetValue(ValueName, command);
        return true;
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(ValueName, false);
    }
}
