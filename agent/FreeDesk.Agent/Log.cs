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

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
            Console.WriteLine(line);

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
}
