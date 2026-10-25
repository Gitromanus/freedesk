<?php
declare(strict_types=1);

namespace FreeDesk;

/** Ленивая уборка: просроченные заявки, старые сигналы, счётчики. */
final class Cleanup
{
    public static function maybeRun(int $intervalSeconds = 60): void
    {
        try {
            $last = (int) (Db::one('SELECT v FROM state WHERE k = ?', ['last_cleanup'])['v'] ?? 0);

            if (time() - $last < $intervalSeconds) {
                return;
            }

            Db::exec(
                'INSERT INTO state (k, v) VALUES (?, ?) ON DUPLICATE KEY UPDATE v = VALUES(v)',
                ['last_cleanup', (string) time()]
            );

            self::run();
        } catch (\Throwable $e) {
            // Уборка не должна ломать API.
            error_log('[freedesk] cleanup failed: ' . $e->getMessage());
        }
    }

    public static function run(): array
    {
        $sessionTtl = max(60, (int) Config::get('session_ttl', 900));
        $signalTtl = max(60, (int) Config::get('signal_ttl', 600));

        $expiredSessions = Db::exec(
            "UPDATE sessions SET state = 'ended', ended_at = NOW()
             WHERE state = 'pending' AND created_at < DATE_SUB(NOW(), INTERVAL {$sessionTtl} SECOND)"
        );

        $staleSignals = Db::exec(
            "DELETE FROM signals WHERE created_at < DATE_SUB(NOW(), INTERVAL {$signalTtl} SECOND)"
        );

        $staleCounters = Db::exec('DELETE FROM rate_limits WHERE window_start < ?', [time() - 7200]);

        return [
            'expired_sessions' => $expiredSessions,
            'deleted_signals' => $staleSignals,
            'deleted_counters' => $staleCounters,
        ];
    }
}
