using System.IO;

namespace FreeDesk.Agent;

public static class Log
{
    private static readonly object Sync = new();
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FreeDesk", "freedesk.log");

    public static void Info(string message) => Write("info", message);
    public static void Warn(string message) => Write("warn", message);
    public static void Error(string message) => Write("err ", message);

    /// <summary>
    /// Падение фонового потока: полный стек в отдельный файл, чтобы разбор
    /// не зависел от того, увидел ли кто-то консоль перед закрытием.
    /// </summary>
    public static void Crash(Exception? ex, string kind)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{kind}]\n{ex}\n";

        lock (Sync)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeDesk");
                Directory.CreateDirectory(dir);

                // Гасим разрастание: держим только десять свежих отчётов.
                var files = Directory.GetFiles(dir, "crash-*.log");
                Array.Sort(files);

                for (var i = 0; i < files.Length - 9; i++)
                {
                    File.Delete(files[i]);
                }

                File.WriteAllText(Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log"), text);
            }
            catch
            {
                // некуда писать — остаётся только консоль
            }

            Write("crash", $"{kind}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    private static string? _lastMessage;
    private static string _lastLevel = "info";
    private static DateTime _lastAt;
    private static int _repeats;

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            var now = DateTime.Now;

            // Шторм одинаковых предупреждений (SendInput/BitBlt на зазваной сессии)
            // раздувал лог на тысячи строк — подряд идущие повторы схлопываем.
            if (message == _lastMessage && (now - _lastAt).TotalSeconds < 3)
            {
                _repeats++;
                _lastAt = now;
                return;
            }

            if (_repeats > 0)
            {
                var repeatLine = $"{_lastAt:HH:mm:ss} [{_lastLevel}] … и ещё {_repeats} раз(а)";
                Console.WriteLine(repeatLine);
                AppendFile(repeatLine);
                _repeats = 0;
            }

            _lastMessage = message;
            _lastLevel = level;
            _lastAt = now;

            var line = $"{now:HH:mm:ss} [{level}] {message}";
            Console.WriteLine(line);
            AppendFile(line);
        }
    }

    private static void AppendFile(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "freedesk-log-err.txt"), ex.ToString() + Environment.NewLine); } catch { }
        }
    }
}
