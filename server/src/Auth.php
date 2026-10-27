<?php
declare(strict_types=1);

namespace FreeDesk;

/**
 * Авторизация по токену устройства.
 *
 * Раньше каждый запрос делал SELECT из devices — при поллинге это самая частая
 * операция в базе. Теперь строка устройства кешируется в файле на 3 минуты
 * (APCu на shared-хостинге нет), а в базе остаются только промахи кеша.
 */
final class Auth
{
    private const CacheTtl = 180;

    /** Поля, которых достаточно обработчикам API. */
    private const CacheFields = ['id', 'public_id', 'name', 'os'];

    /** @return array<string, mixed> строка устройства, соответствующая токену */
    public static function device(): array
    {
        $token = Http::deviceToken();

        if ($token === null || $token === '') {
            throw new ApiError('unauthorized', 'device token required', 401);
        }

        $file = self::cacheFile($token);

        if ($file !== null && is_file($file)) {
            $raw = @file_get_contents($file);

            if ($raw !== false) {
                $cached = json_decode($raw, true);

                if (is_array($cached) && ($cached['exp'] ?? 0) > time() && is_array($cached['device'] ?? null)) {
                    return $cached['device'];
                }
            }
        }

        $device = Devices::byToken($token);

        $slice = [];
        foreach (self::CacheFields as $field) {
            if (array_key_exists($field, $device)) {
                $slice[$field] = $device[$field];
            }
        }

        if ($file !== null) {
            @file_put_contents($file, json_encode(
                ['exp' => time() + self::CacheTtl, 'device' => $slice],
                JSON_UNESCAPED_UNICODE
            ), LOCK_EX);
        }

        return $slice;
    }

    /** Сбросить кеш для токена (например, при смене имени устройства). */
    public static function forget(string $token): void
    {
        $file = self::cacheFile($token);

        if ($file !== null && is_file($file)) {
            @unlink($file);
        }
    }

    private static function cacheFile(string $token): ?string
    {
        $dir = sys_get_temp_dir();

        if ($dir === '' || !is_dir($dir) || !is_writable($dir)) {
            return null;
        }

        return $dir . '/fd-auth-' . hash('sha256', $token) . '.json';
    }
}
