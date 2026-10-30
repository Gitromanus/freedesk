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

    /// <summary>Прогресс приёма: id, имя, принято байт, всего байт, путь (заполнен, когда файл готов).</summary>
    public event Action<int, string, long, long, string?>? Progress;

    public sealed record Received(string Path, string Name, long Size);

    private sealed class Incoming
    {
        public required FileStream Stream { get; init; }
        public required string Path { get; init; }
        public required string Name { get; init; }
        public required long Size { get; init; }
        public long Written { get; set; }
    }

    /// <summary>Готов ли принимать файл; ошибка — повод ответить отказом.
    /// targetDir — папка, открытая на принимающей стороне; невалидная — папка приёма.</summary>
    public bool TryStart(int id, string name, long size, out string? error, string? targetDir = null)
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

        var directory = ResolveDirectory(targetDir);

        try
        {
            var path = FileWire.UniquePath(directory, name);
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

    /// <summary>Папка приёма: предлагаемая, если существует, иначе Downloads\FreeDesk.</summary>
    private static string ResolveDirectory(string? targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return FileWire.Downloads;
        }

        try
        {
            var full = Path.GetFullPath(targetDir);

            if (Directory.Exists(full))
            {
                return full;
            }
        }
        catch
        {
            // кривой путь от другой стороны — молча падаем в папку приёма
        }

        return FileWire.Downloads;
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
            Progress?.Invoke(id, incoming.Name, incoming.Written, incoming.Size, null);
            return null;
        }

        _active.Remove(id);
        incoming.Stream.Flush();
        incoming.Stream.Dispose();

        Progress?.Invoke(id, incoming.Name, incoming.Written, incoming.Size, incoming.Path);
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

/// <summary>
/// Отправка файлов по data channel. Один экземпляр на сессию: помнит,
/// какие передачи ждут подтверждения от другой стороны.
/// </summary>
public sealed class FileSender
{
    private readonly IPeerTransport _peer;
    private readonly Action<string>? _status;
    private readonly Dictionary<int, TaskCompletionSource<bool>> _offers = new();
    private readonly Dictionary<int, CancellationTokenSource> _streams = new();
    private int _counter;

    /// <summary>Прогресс отправки: имя, отправлено байт, всего байт.</summary>
    public event Action<string, long, long>? SendProgress;

    public FileSender(IPeerTransport peer, Action<string>? status = null)
    {
        _peer = peer;
        _status = status;
    }

    /// <summary>Ответ на наше предложение (accept/reject/cancel). true — сообщение наше.</summary>
    public bool HandleControl(System.Text.Json.JsonElement root)
    {
        var op = root.TryGetProperty("op", out var opValue) ? opValue.GetString() : null;
        var id = root.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var parsed) ? parsed : 0;

        // Другая сторона прервала приём — останавливаем свою отправку.
        if (op == "cancel")
        {
            if (_streams.TryGetValue(id, out var stream))
            {
                stream.Cancel();
            }

            return _offers.Remove(id);
        }

        if (op is not ("accept" or "reject") || !_offers.TryGetValue(id, out var pending))
        {
            return false;
        }

        pending.TrySetResult(op == "accept");
        return true;
    }

    /// <summary>Предложить и отправить файл. targetDir — куда просим положить на той
    /// стороне (та папка, что открыта у неё в панели). Ошибки — через статус.</summary>
    public async Task SendAsync(string path, string? targetDir = null, CancellationToken ct = default)
    {
        if (!_peer.IsConnected)
        {
            _status?.Invoke("нет соединения");
            return;
        }

        var info = new FileInfo(path);

        if (!info.Exists)
        {
            _status?.Invoke("файл не найден");
            return;
        }

        if (info.Length <= 0 || info.Length > FileWire.MaxFileBytes)
        {
            _status?.Invoke("файл слишком большой (макс. 2 ГБ)");
            return;
        }

        var id = Interlocked.Increment(ref _counter);
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _offers[id] = accepted;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _streams[id] = linked;

        try
        {
            object offer = string.IsNullOrWhiteSpace(targetDir)
                ? (object) new { t = "file", op = "offer", id, name = info.Name, size = info.Length }
                : new { t = "file", op = "offer", id, name = info.Name, size = info.Length, dir = targetDir };

            _peer.SendControl(System.Text.Json.JsonSerializer.Serialize(offer));

            _status?.Invoke($"ждём подтверждения: {info.Name}");

            bool ok;

            try
            {
                ok = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(60), linked.Token);
            }
            catch (TimeoutException)
            {
                _status?.Invoke($"{info.Name}: удалённая сторона не ответила");
                return;
            }

            if (!ok)
            {
                _status?.Invoke($"{info.Name}: передача отклонена");
                return;
            }

            await using var stream = File.OpenRead(path);
            var buffer = new byte[FileWire.ChunkSize];
            long sent = 0;

            while (sent < info.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, FileWire.ChunkSize), linked.Token);

                if (read <= 0)
                {
                    break;
                }

                _peer.SendBinary(FileWire.Frame(id, buffer.AsSpan(0, read)));
                sent += read;

                SendProgress?.Invoke(info.Name, sent, info.Length);

                // Очередь SCTP упорядоченная: если забить её файлом, события ввода
                // (мышь, клавиши) встанут в хвост и экран «залипнет». Держим
                // в полёте немного — ввод в 64 байта проскакивает между чанками.
                while (_peer.BufferedBytes > 256_000)
                {
                    await Task.Delay(10, linked.Token);
                }
            }

            _peer.SendControl(System.Text.Json.JsonSerializer.Serialize(new { t = "file", op = "done", id }));

            Log.Info($"файл отправлен: {info.Name} ({sent / 1024} КБ)");
            _status?.Invoke($"файл отправлен: {info.Name}");
        }
        catch (OperationCanceledException)
        {
            _peer.SendControl(System.Text.Json.JsonSerializer.Serialize(new { t = "file", op = "cancel", id }));
            _status?.Invoke("передача отменена");
        }
        catch (Exception ex)
        {
            _peer.SendControl(System.Text.Json.JsonSerializer.Serialize(new { t = "file", op = "cancel", id }));
            Log.Error($"файл не отправлен: {ex.Message}");
            _status?.Invoke("ошибка передачи: " + ex.Message);
        }
        finally
        {
            _offers.Remove(id);
            _streams.Remove(id);
        }
    }
}
