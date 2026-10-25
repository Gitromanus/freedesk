using System.Security.Cryptography;
using System.Text;

namespace FreeDesk.Agent;

/// <summary>
/// Пароль устройства для доступа без подтверждения.
/// Хранится не пароль, а PBKDF2-хеш (соль + итерации) в конфиге профиля.
/// Проверка — challenge-response: хост присылает nonce, зритель отвечает
/// PBKDF2(хеш, nonce); сам хеш по сети не ходит и из ответа не восстанавливается.
/// </summary>
public static class UnattendedAuth
{
    public const int DefaultIterations = 120_000;
    public const int MinIterations = 50_000;
    public const int MaxIterations = 1_000_000;

    private const int HashBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 16;

    /// <summary>Соль и хеш пароля — то, что лежит в конфиге.</summary>
    public sealed record Secret(byte[] Salt, int Iterations, byte[] Hash);

    public static Secret Create(string password, int iterations = DefaultIterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("пустой пароль", nameof(password));
        }

        iterations = Math.Clamp(iterations, MinIterations, MaxIterations);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new Secret(salt, iterations, Derive(password, salt, iterations));
    }

    /// <summary>PBKDF2-SHA256 от пароля: внутренний хеш, он же «секрет» для challenge.</summary>
    public static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Math.Clamp(iterations, MinIterations, MaxIterations),
            HashAlgorithmName.SHA256, HashBytes);

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes));

    /// <summary>Ответ зрителя: PBKDF2 от сохранённого хеша и nonce хоста.</summary>
    public static string ComputeProof(byte[] secretHash, string nonce, int iterations) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
            secretHash, Convert.FromBase64String(nonce),
            Math.Clamp(iterations, MinIterations, MaxIterations), HashAlgorithmName.SHA256, HashBytes));

    /// <summary>Проверка ответа; сравнение — за постоянное время.</summary>
    public static bool Verify(Secret secret, string nonce, string proof)
    {
        try
        {
            var expected = Rfc2898DeriveBytes.Pbkdf2(
                secret.Hash, Convert.FromBase64String(nonce), secret.Iterations,
                HashAlgorithmName.SHA256, HashBytes);

            var actual = Convert.FromBase64String(proof);
            return actual.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
