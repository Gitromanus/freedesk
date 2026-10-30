using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeDesk.Agent;

/// <summary>Состояние агента на диске: сервер, токен устройства, настройки.</summary>
public sealed class AgentConfig
{
    public string Server { get; set; } = "https://fd.cherubino74.ru";
    public string Name { get; set; } = Environment.MachineName;
    public string? DeviceToken { get; set; }
    public string? PublicId { get; set; }

    /// <summary>Частота кадров при стриминге.</summary>
    public int Fps { get; set; } = 12;

    /// <summary>Настройки опроса — оставлены для совместимости со старыми профилями.</summary>
    public int PresenceSeconds { get; set; } = 15;
    public int PollSeconds { get; set; } = 1;

    /// <summary>Способ захвата экрана: auto | dxgi | gdi.</summary>
    public string Capture { get; set; } = "auto";

    /// <summary>Качество JPEG (10..100).</summary>
    public long JpegQuality { get; set; } = 80;

    /// <summary>Кодек видео: h264 (openh264) либо mjpeg.</summary>
    public string Codec { get; set; } = "h264";

    /// <summary>Целевой битрейт H.264, кбит/с.</summary>
    public int BitrateKbps { get; set; } = 3000;

    /// <summary>Монитор, с которого начинается трансляция (0 — основной).</summary>
    public int Display { get; set; }

    /// <summary>Закрытие окна сворачивает агент в трей, а не завершает его.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>При подключении подгонять окно зрителя под родное разрешение
    /// удалённого экрана (сжать, если оно больше рабочего стола).</summary>
    public bool FitWindowOnConnect { get; set; } = true;

    /// <summary>
    /// Сохранённые пароли устройств: по ID устройства лежит соль/итерации/хеш PBKDF2.
    /// Сам пароль не хранится — только то, из чего нельзя восстановить пароль.
    /// </summary>
    public Dictionary<string, SavedCredential> SavedCredentials { get; set; } = new();

    /// <summary>Соль PBKDF2-хеша пароля unattended-доступа.</summary>
    public string? UnattendedSalt { get; set; }

    /// <summary>Число итераций PBKDF2 для сохранённого пароля.</summary>
    public int UnattendedIterations { get; set; } = UnattendedAuth.DefaultIterations;

    /// <summary>Хеш пароля unattended-доступа (сам пароль не хранится).</summary>
    public string? UnattendedHash { get; set; }

    [JsonIgnore]
    public bool HasUnattendedPassword =>
        !string.IsNullOrEmpty(UnattendedHash) && !string.IsNullOrEmpty(UnattendedSalt);

    /// <summary>Сохраняет пароль в виде соль+итерации+хеш.</summary>
    public void SetUnattendedPassword(string password)
    {
        var secret = UnattendedAuth.Create(password);
        UnattendedSalt = Convert.ToBase64String(secret.Salt);
        UnattendedIterations = secret.Iterations;
        UnattendedHash = Convert.ToBase64String(secret.Hash);
    }

    public void ClearUnattendedPassword()
    {
        UnattendedSalt = null;
        UnattendedHash = null;
    }

    /// <summary>Секрет для проверки challenge; null — пароль не задан.</summary>
    public UnattendedAuth.Secret? GetUnattendedSecret()
    {
        if (!HasUnattendedPassword)
        {
            return null;
        }

        try
        {
            return new UnattendedAuth.Secret(
                Convert.FromBase64String(UnattendedSalt!),
                Math.Clamp(UnattendedIterations, UnattendedAuth.MinIterations, UnattendedAuth.MaxIterations),
                Convert.FromBase64String(UnattendedHash!));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Сохранённый хеш пароля для устройства — если соль та же, что в challenge.
    /// Сменившуюся соль (пароль на хосте поменяли) считаем недействительной.
    /// </summary>
    public byte[]? TryGetSavedSecret(string targetId, string saltBase64)
    {
        if (!SavedCredentials.TryGetValue(targetId, out var saved) ||
            !string.Equals(saved.Salt, saltBase64, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(saved.Hash);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void RememberSavedSecret(string targetId, string saltBase64, int iterations, byte[] hash) =>
        SavedCredentials[targetId] = new SavedCredential
        {
            Salt = saltBase64,
            Iterations = iterations,
            Hash = Convert.ToBase64String(hash),
        };

    public void ForgetSavedSecrets() => SavedCredentials.Clear();

    /// <summary>Ширина кадра после масштабирования (0 — без масштабирования).</summary>
    public int MaxWidth { get; set; } = 1920;

    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeDesk");

    /// <summary>Профиль позволяет держать несколько агентов на одной машине.</summary>
    public static string Profile { get; set; } = "agent";

    private static string FilePath => Path.Combine(Directory, $"{Profile}.json");

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"не удалось прочитать {FilePath}: {ex.Message}");
        }

        return new AgentConfig();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"не удалось сохранить {FilePath}: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Сохранённый хеш пароля устройства (без самого пароля).</summary>
public sealed class SavedCredential
{
    public string Salt { get; set; } = "";
    public int Iterations { get; set; }
    public string Hash { get; set; } = "";
}
