<?php
declare(strict_types=1);

namespace FreeDesk;

/** Устройства: регистрация, поиск по токену/ID, heartbeat, статус. */
final class Devices
{
    public static function register(array $in, string $ip): array
    {
        RateLimit::hit('register:' . $ip, (int) Config::get('register_limit', 20), 3600);

        $name = trim((string) ($in['name'] ?? ''));
        if ($name === '' || mb_strlen($name) > 64) {
            throw new ApiError('bad_name', 'name is required (1..64 chars)');
        }

        $os = mb_substr(trim((string) ($in['os'] ?? 'unknown')), 0, 32);
        if ($os === '') {
            $os = 'unknown';
        }

        $pubkey = isset($in['pubkey']) ? mb_substr((string) $in['pubkey'], 0, 512) : null;
        $token = bin2hex(random_bytes(32));
        $tokenHash = hash('sha256', $token);

        for ($attempt = 0; $attempt < 5; $attempt++) {
            $publicId = Ids::generate();

            try {
                Db::exec(
                    'INSERT INTO devices (public_id, token_hash, name, os, pubkey, last_seen)
                     VALUES (?, ?, ?, ?, ?, NOW())',
                    [$publicId, $tokenHash, $name, $os, $pubkey]
                );
            } catch (\PDOException $e) {
                // 23000 = нарушение уникальности: заняли public_id, пробуем другой
                if ((string) $e->getCode() === '23000' && $attempt < 4) {
                    continue;
                }
                throw $e;
            }

            return [
                'device_id' => (int) Db::pdo()->lastInsertId(),
                'public_id' => $publicId,
                'device_token' => $token,
                'heartbeat_seconds' => (int) Config::get('presence_interval', 15),
            ];
        }

        throw new ApiError('id_conflict', 'could not allocate device id, retry', 503);
    }

    /** @return array<string, mixed> */
    public static function byToken(string $token): array
    {
        $row = Db::one('SELECT * FROM devices WHERE token_hash = ? LIMIT 1', [hash('sha256', $token)]);

        if ($row === null) {
            throw new ApiError('unauthorized', 'unknown device token', 401);
        }

        return $row;
    }

    public static function touch(int $deviceId, ?string $name = null): int
    {
        // Имя можно поменять из настроек клиента — обновляем вместе с heartbeat.
        $name = $name === null ? '' : trim($name);
        if ($name !== '' && mb_strlen($name) <= 64) {
            Db::exec('UPDATE devices SET last_seen = NOW(), name = ? WHERE id = ?', [$name, $deviceId]);
        } else {
            Db::exec('UPDATE devices SET last_seen = NOW() WHERE id = ?', [$deviceId]);
        }

        return time();
    }

    public static function resolve(string $publicId, int $meId): array
    {
        if (!Ids::isPublicId($publicId)) {
            throw new ApiError('bad_target', 'device id must be 9 digits');
        }

        $row = Db::one(
            'SELECT id, name, os, TIMESTAMPDIFF(SECOND, last_seen, NOW()) AS idle
             FROM devices WHERE public_id = ? LIMIT 1',
            [$publicId]
        );

        if ($row === null) {
            throw new ApiError('not_found', 'unknown device id', 404);
        }

        $idle = $row['idle'] === null ? null : (int) $row['idle'];

        return [
            'public_id' => $publicId,
            'name' => $row['name'],
            'os' => $row['os'],
            'online' => $idle !== null && $idle <= (int) Config::get('online_window', 60),
            'idle_seconds' => $idle,
            'self' => (int) $row['id'] === $meId,
        ];
    }
}
