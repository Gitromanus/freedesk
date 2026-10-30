<?php
declare(strict_types=1);

namespace FreeDesk;

/**
 * Сессии и обмен сигналами (SDP/ICE) между двумя устройствами.
 * Сервер видит только эти короткие сообщения — медиа идёт P2P.
 */
final class Sessions
{
    private const SID_PATTERN = '/^[a-f0-9]{32}$/';

    public static function request(int $fromId, array $in): array
    {
        $targetPublicId = trim((string) ($in['target_id'] ?? ''));

        if (!Ids::isPublicId($targetPublicId)) {
            throw new ApiError('bad_target', 'target_id must be 9 digits');
        }

        $target = Db::one(
            'SELECT id, public_id, name, TIMESTAMPDIFF(SECOND, last_seen, NOW()) AS idle
             FROM devices WHERE public_id = ? LIMIT 1',
            [$targetPublicId]
        );

        if ($target === null) {
            throw new ApiError('not_found', 'unknown device id', 404);
        }

        if ((int) $target['id'] === $fromId) {
            throw new ApiError('bad_target', 'cannot connect to yourself');
        }

        $idle = $target['idle'] === null ? PHP_INT_MAX : (int) $target['idle'];

        if ($idle > (int) Config::get('online_window', 60)) {
            throw new ApiError('target_offline', 'device is offline', 409);
        }

        $sid = bin2hex(random_bytes(16));

        Db::exec(
            "INSERT INTO sessions (id, from_device, to_device, state, created_at)
             VALUES (?, ?, ?, 'pending', NOW())",
            [$sid, $fromId, (int) $target['id']]
        );

        return [
            'session_id' => $sid,
            'target' => ['public_id' => $target['public_id'], 'name' => $target['name']],
            'expires_in' => (int) Config::get('session_ttl', 900),
        ];
    }

    /** Входящие заявки на подключение — их опрашивает принимающая сторона. */
    public static function incoming(int $deviceId, int $wait = 0): array
    {
        $wait = min(15, max(0, $wait));
        $deadline = microtime(true) + $wait;

        $rows = [];

        // long-poll: хост держит запрос и получает заявку сразу, как она появилась,
        // вместо опроса раз в секунду.
        while (true) {
            $rows = Db::all(
                "SELECT s.id, s.created_at, d.public_id, d.name, d.os
                 FROM sessions s
                 JOIN devices d ON d.id = s.from_device
                 WHERE s.to_device = ? AND s.state = 'pending'
                 ORDER BY s.created_at ASC
                 LIMIT 20",
                [$deviceId]
            );

            if ($rows !== [] || microtime(true) >= $deadline) {
                break;
            }

            usleep(600_000);
        }

        $items = [];

        foreach ($rows as $row) {
            $items[] = [
                'session_id' => $row['id'],
                'from' => [
                    'public_id' => $row['public_id'],
                    'name' => $row['name'],
                    'os' => $row['os'],
                ],
                'created_at' => $row['created_at'],
            ];
        }

        return ['incoming' => $items];
    }

    public static function answer(int $deviceId, array $in): array
    {
        $sid = self::requireSid((string) ($in['session_id'] ?? ''));
        $action = (string) ($in['action'] ?? '');
        $session = self::load($sid);

        $isFrom = (int) $session['from_device'] === $deviceId;
        $isTo = (int) $session['to_device'] === $deviceId;

        if (!$isFrom && !$isTo) {
            throw new ApiError('forbidden', 'not a participant of this session', 403);
        }

        if ($session['state'] === 'ended') {
            throw new ApiError('session_ended', 'session already ended', 409);
        }

        switch ($action) {
            case 'accept':
                if (!$isTo) {
                    throw new ApiError('forbidden', 'only the receiving side can accept', 403);
                }
                if ($session['state'] !== 'pending') {
                    throw new ApiError('bad_state', 'session is not pending', 409);
                }
                Db::exec("UPDATE sessions SET state = 'active', accepted_at = NOW() WHERE id = ?", [$sid]);
                break;

            case 'reject':
                if (!$isTo) {
                    throw new ApiError('forbidden', 'only the receiving side can reject', 403);
                }
                if ($session['state'] !== 'pending') {
                    throw new ApiError('bad_state', 'session is not pending', 409);
                }
                Db::exec("UPDATE sessions SET state = 'ended', ended_at = NOW() WHERE id = ?", [$sid]);
                break;

            case 'end':
                Db::exec("UPDATE sessions SET state = 'ended', ended_at = NOW() WHERE id = ?", [$sid]);
                break;

            default:
                throw new ApiError('bad_action', 'action must be accept, reject or end');
        }

        return ['session_id' => $sid, 'state' => self::load($sid)['state']];
    }

    public static function signal(int $deviceId, array $in): array
    {
        $sid = self::requireSid((string) ($in['session_id'] ?? ''));
        $type = mb_substr(trim((string) ($in['type'] ?? '')), 0, 32);

        if ($type === '') {
            throw new ApiError('bad_type', 'type is required (e.g. offer, answer, ice)');
        }

        $session = self::load($sid);

        if ((int) $session['from_device'] !== $deviceId && (int) $session['to_device'] !== $deviceId) {
            throw new ApiError('forbidden', 'not a participant of this session', 403);
        }

        if ($session['state'] === 'ended') {
            throw new ApiError('session_ended', 'session already ended', 409);
        }

        $payload = $in['payload'] ?? null;
        $encoded = $payload === null
            ? null
            : json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);

        if ($encoded !== null && strlen($encoded) > 256 * 1024) {
            throw new ApiError('payload_too_large', 'payload exceeds 256 KiB', 413);
        }

        Db::exec(
            'INSERT INTO signals (session_id, from_device, type, payload) VALUES (?, ?, ?, ?)',
            [$sid, $deviceId, $type, $encoded]
        );

        return ['signal_id' => (int) Db::pdo()->lastInsertId()];
    }

    public static function poll(int $deviceId, array $query): array
    {
        $sid = self::requireSid((string) ($query['session_id'] ?? ''));
        $since = max(0, (int) ($query['since'] ?? 0));
        $wait = min(20, max(0, (int) ($query['wait'] ?? 0)));
        $session = self::load($sid);

        if ((int) $session['from_device'] !== $deviceId && (int) $session['to_device'] !== $deviceId) {
            throw new ApiError('forbidden', 'not a participant of this session', 403);
        }

        $deadline = microtime(true) + $wait;
        $rows = [];
        $round = 0;
        $prevState = (string) $session['state'];

        // В режиме ожидания не дёргаем MySQL каждые 400 мс: сигналы — всегда,
        // состояние сессии — раз в два круга. Смена состояния (pending → active)
        // должна разбудить запрос сразу, иначе оффер уходит с задержкой в wait секунд.
        while (true) {
            $rows = Db::all(
                'SELECT id, type, payload FROM signals
                 WHERE session_id = ? AND id > ? AND from_device <> ?
                 ORDER BY id ASC LIMIT 100',
                [$sid, $since, $deviceId]
            );

            if ($rows !== []) {
                break;
            }

            if (++$round % 2 === 0) {
                $state = self::load($sid)['state'];

                if ($state !== $prevState) {
                    break;
                }
            }

            if (microtime(true) >= $deadline) {
                break;
            }

            usleep(600_000);
        }

        $cursor = $since;
        $messages = [];

        foreach ($rows as $row) {
            $cursor = max($cursor, (int) $row['id']);
            $payload = $row['payload'];

            if ($payload !== null && $payload !== '') {
                $decoded = json_decode($payload, true);
                $payload = $decoded ?? $row['payload'];
            }

            $messages[] = [
                'id' => (int) $row['id'],
                'type' => $row['type'],
                'payload' => $payload,
            ];
        }

        $session = self::load($sid);

        return [
            'session' => [
                'id' => $sid,
                'state' => $session['state'],
                'role' => (int) $session['from_device'] === $deviceId ? 'host' : 'guest',
            ],
            'messages' => $messages,
            'cursor' => $cursor,
            'server_time' => time(),
        ];
    }

    /** @return array<string, mixed> */
    private static function load(string $sid): array
    {
        $session = Db::one('SELECT * FROM sessions WHERE id = ? LIMIT 1', [$sid]);

        if ($session === null) {
            throw new ApiError('not_found', 'unknown session', 404);
        }

        return $session;
    }

    private static function requireSid(string $sid): string
    {
        if (!preg_match(self::SID_PATTERN, $sid)) {
            throw new ApiError('bad_session', 'session_id must be 32 hex chars');
        }

        return $sid;
    }

    /**
     * Проверка для резервного канала: сессия существует и устройство — её участник.
     * Возвращает нормализованный id сессии.
     */
    public static function requireParticipant(int $deviceId, string $sid): string
    {
        $sid = self::requireSid($sid);
        $session = self::load($sid);

        if ((int) $session['from_device'] !== $deviceId && (int) $session['to_device'] !== $deviceId) {
            throw new ApiError('forbidden', 'not a participant of this session', 403);
        }

        return $sid;
    }
}
