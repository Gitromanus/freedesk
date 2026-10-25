using System.Buffers.Binary;
using System.IO;

namespace FreeDesk.Agent.Files;

/// <summary>
/// Обмен файлами по data channel: управление — JSON (`{"t":"file",...}`),
/// данные — бинарные блоки `[4 байта id][чанк]`. Канал надёжный и упорядоченный,
/// поэтому подтверждения на каждый чанк не нужны: проверяем только итоговый размер.
/// </summary>
public static class FileWire
{
    public const int ChunkSize = 16 * 1024;
    public const int HeaderSize = 4;

    /// <summary>Больше 2 ГБ по data channel гонять бессмысленно — и опасно для памяти.</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    public static byte[] Frame(int id, ReadOnlySpan<byte> chunk)
    {
        var buffer = new byte[HeaderSize + chunk.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, id);
        chunk.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryUnframe(byte[] data, out int id, out ReadOnlySpan<byte> chunk)
    {
        if (data.Length <= HeaderSize)
        {
            id = 0;
            chunk = default;
            return false;
        }

        id = BinaryPrimitives.ReadInt32LittleEndian(data);
        chunk = data.AsSpan(HeaderSize);
        return true;
    }

    /// <summary>Имя без каталогов и запрещённых символов — чтобы нельзя было выйти за папку приёма.</summary>
    public static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(name.Replace('\\', '/'));
        var cleaned = new string(leaf
            .Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '/' && c != '\\')
            .ToArray())
            .Trim();

        if (cleaned.Length == 0)
        {
            cleaned = "file";
        }

        return cleaned.Length > 120 ? cleaned[^120..] : cleaned;
    }

    /// <summary>Свободное имя в каталоге: file.txt, file (1).txt, file (2).txt …</summary>
    public static string UniquePath(string directory, string fileName)
    {
        var safe = SanitizeFileName(fileName);
        var path = Path.Combine(directory, safe);

        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);

        for (var i = 1; i < 10_000; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){extension}");

            if (!File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
    }

    public static string Downloads
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FreeDesk");

            Directory.CreateDirectory(folder);
            return folder;
        }
    }
}

/// <summary>Приём файлов на стороне хоста.</summary>
public sealed class FileReceiver : IDisposable
{
    private readonly Dictionary<int, Incoming> _active = new();

    public sealed record Received(string Path, string Name, long Size);

    private sealed class Incoming
    {
        public required FileStream Stream { get; init; }
        public required string Path { get; init; }
        public required string Name { get; init; }
        public required long Size { get; init; }
        public long Written { get; set; }
    }

    /// <summary>Готов ли принимать файл; ошибка — повод ответить отказом.</summary>
    public bool TryStart(int id, string name, long size, out string? error)
    {
        error = null;
        name = FileWire.SanitizeFileName(name);

        if (size <= 0 || size > FileWire.MaxFileBytes)
        {
            error = "недопустимый размер файла";
            return false;
        }

        if (_active.ContainsKey(id))
        {
            error = "передача с таким id уже идёт";
            return false;
        }

        try
        {
            var path = FileWire.UniquePath(FileWire.Downloads, name);
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);

            _active[id] = new Incoming { Stream = stream, Path = path, Name = name, Size = size };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Пишет чанк; возвращает готовый файл, когда получено всё.</summary>
    public Received? Write(int id, ReadOnlySpan<byte> chunk)
    {
        if (!_active.TryGetValue(id, out var incoming))
        {
            return null;
        }

        incoming.Stream.Write(chunk);
        incoming.Written += chunk.Length;

        if (incoming.Written < incoming.Size)
        {
            return null;
        }

        _active.Remove(id);
        incoming.Stream.Flush();
        incoming.Stream.Dispose();

        return new Received(incoming.Path, incoming.Name, incoming.Written);
    }

    /// <summary>Оборвать передачу (сессия закрылась или пришёл отказ).</summary>
    public void Abort(int id)
    {
        if (!_active.Remove(id, out var incoming))
        {
            return;
        }

        incoming.Stream.Dispose();

        try
        {
            File.Delete(incoming.Path);
        }
        catch
        {
            // недописанный файл мог остаться — это допустимо
        }
    }

    public void Dispose()
    {
        foreach (var id in _active.Keys.ToList())
        {
            Abort(id);
        }
    }
}
