using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FreeDesk.Agent.Codec;

/// <summary>
/// OpenH264 встраивается в exe как ресурс и распаковывается в
/// %LOCALAPPDATA%\FreeDesk\native при первом запуске. Так портабл остаётся
/// одним файлом: рядом с exe никаких dll носить не надо.
/// </summary>
public static class NativeCodec
{
    private const string ResourceName = "openh264.dll";
    private const string DllName = "openh264";

    private static readonly object Sync = new();
    private static string? _path;
    private static bool _resolverInstalled;

    /// <summary>Путь к распакованной библиотеке; null — встроенного ресурса нет.</summary>
    public static string? Ensure()
    {
        lock (Sync)
        {
            if (_path is not null)
            {
                return _path;
            }

            try
            {
                var assembly = typeof(NativeCodec).Assembly;

                using var resource = assembly.GetManifestResourceStream(ResourceName);
                if (resource is null)
                {
                    // Сборка без встроенного кодека — работаем как раньше, с dll рядом.
                    return null;
                }

                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FreeDesk", "native");

                Directory.CreateDirectory(directory);

                var target = Path.Combine(directory, "openh264.dll");

                if (!File.Exists(target) || new FileInfo(target).Length != resource.Length)
                {
                    // Пишем через временный файл: параллельный запуск не оставит огрызок.
                    var temp = target + ".tmp";
                    using (var file = File.Create(temp))
                    {
                        resource.CopyTo(file);
                    }

                    File.Move(temp, target, overwrite: true);
                }

                _path = target;

                if (!_resolverInstalled)
                {
                    NativeLibrary.SetDllImportResolver(assembly, Resolve);
                    _resolverInstalled = true;
                }

                return _path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException)
            {
                Log.Warn($"openh264: не удалось распаковать встроенный кодек ({ex.Message})");
                return null;
            }
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, DllName, StringComparison.OrdinalIgnoreCase) || _path is null)
        {
            // 0 — пусть ищет стандартным способом (рядом с exe)
            return IntPtr.Zero;
        }

        return NativeLibrary.Load(_path);
    }
}
