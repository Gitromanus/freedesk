<?php
declare(strict_types=1);

namespace FreeDesk;

/** Простой счётчик запросов по ключу в скользящем окне (фиксированные окна). */
final class RateLimit
{
    public static function hit(string $key, int $limit, int $windowSeconds): void
    {
        $now = time();
        $windowStart = intdiv($now, $windowSeconds) * $windowSeconds;

        Db::exec(
            'INSERT INTO rate_limits (k, window_start, hits) VALUES (?, ?, 1)
             ON DUPLICATE KEY UPDATE
               hits = IF(window_start = VALUES(window_start), hits + 1, 1),
               window_start = VALUES(window_start)',
            [$key, $windowStart]
        );

        $hits = (int) Db::one('SELECT hits FROM rate_limits WHERE k = ?', [$key])['hits'];

        if ($hits > $limit) {
            throw new ApiError('rate_limited', 'too many requests, try again later', 429);
        }
    }
}
