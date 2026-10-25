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

    /// <summary>Принимать входящие подключения без подтверждения.</summary>
    public bool AutoAccept { get; set; }

    public int PresenceSeconds { get; set; } = 15;
    public int PollSeconds { get; set; } = 1;

    /// <summary>Частота кадров при стриминге.</summary>
    public int Fps { get; set; } = 12;

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
