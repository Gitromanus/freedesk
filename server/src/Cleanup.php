<?php
declare(strict_types=1);

namespace FreeDesk;

/** Ленивая уборка: просроченные заявки, старые сигналы, счётчики. */
final class Cleanup
{
    public static function maybeRun(int $intervalSeconds = 60): void
    {
        try {
            // Отметка времени — в файле, а не в таблице state: этот код вызывается
            // на каждом запросе, и лишний SELECT в горячем пути ни к чему.
            $stamp = self::stampFile();
            $last = 0;

            if ($stamp !== null && is_file($stamp)) {
                $last = (int) @file_get_contents($stamp);
            }

            if (time() - $last < $intervalSeconds) {
                return;
            }

            if ($stamp !== null) {
                @file_put_contents($stamp, (string) time(), LOCK_EX);
            }

            self::run();
        } catch (\Throwable $e) {
            // Уборка не должна ломать API.
            error_log('[freedesk] cleanup failed: ' . $e->getMessage());
        }
    }

    private static function stampFile(): ?string
    {
        $dir = sys_get_temp_dir();

        if ($dir === '' || !is_dir($dir) || !is_writable($dir)) {
            return null;
        }

        return $dir . '/fd-cleanup.stamp';
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

        // Каталоги резервного канала живут в tmp и убираются по своему TTL.
        $relayDirs = Relay::cleanup();

        return [
            'expired_sessions' => $expiredSessions,
            'deleted_signals' => $staleSignals,
            'deleted_counters' => $staleCounters,
            'relay_dirs_removed' => $relayDirs,
        ];
    }
}
