<?php
declare(strict_types=1);

namespace FreeDesk;

/**
 * Резервный канал на случай, когда NAT не пропускает прямое WebRTC-соединение.
 *
 * Кадры JPEG и события ввода ходят через этот же сервер, в файлах на диске:
 *   <tmp>/fdrelay/<session>/frame.jpg  — последний кадр
 *   <tmp>/fdrelay/<session>/meta.json  — {seq, ts, w, h}
 *   <tmp>/fdrelay/<session>/ctl.jsonl  — события ввода, по строке на событие
 *
 * Формат намеренно простой: ни БД, ни блокировок на чтение — только атомарная
 * замена файла и дозапись с LOCK_EX. Это в разы дешевле, чем гонять кадры
 * через MySQL, и не требует WebSocket на shared-хостинге.
 */
final class Relay
{
    /** Больше кадра в 200 КБ быть не должно: это уже не наш режим. */
    public const MaxFrameBytes = 200 * 1024;

    /** Сессия без обновлений столько секунд — каталог удаляется уборкой. */
    public const TtlSeconds = 3600;

    public static function dir(string $sessionId): ?string
    {
        $base = sys_get_temp_dir();

        if ($base === '' || !is_dir($base) || !is_writable($base)) {
            return null;
        }

        $safe = preg_replace('/[^a-f0-9]/i', '', $sessionId) ?? '';
        if (strlen($safe) < 8) {
            return null;
        }

        $dir = $base . '/fdrelay/' . $safe;

        if (!is_dir($dir) && !@mkdir($dir, 0700, true) && !is_dir($dir)) {
            return null;
        }

        return $dir;
    }

    /** @return array{seq:int,ts:int} */
    public static function putFrame(string $sessionId, string $jpeg): array
    {
        $dir = self::dir($sessionId);

        if ($dir === null) {
            throw new ApiError('relay_unavailable', 'relay storage is not writable', 500);
        }

        $seq = self::nextSeq($dir);

        // Пишем во временный файл и переименовываем: зритель никогда не увидит половину кадра.
        $tmp = $dir . '/frame.tmp';
        if (@file_put_contents($tmp, $jpeg, LOCK_EX) === false || !@rename($tmp, $dir . '/frame.jpg')) {
            throw new ApiError('relay_write', 'cannot store frame', 500);
        }

        $meta = ['seq' => $seq, 'ts' => time()];
        @file_put_contents($dir . '/meta.json', json_encode($meta), LOCK_EX);

        return $meta;
    }

    /** @return array{seq:int,ts:int} */
    public static function meta(string $sessionId): array
    {
        $dir = self::dir($sessionId);
        $empty = ['seq' => 0, 'ts' => 0];

        if ($dir === null || !is_file($dir . '/meta.json')) {
            return $empty;
        }

        $raw = @file_get_contents($dir . '/meta.json');
        $meta = $raw === false ? null : json_decode($raw, true);

        return is_array($meta) ? ['seq' => (int) ($meta['seq'] ?? 0), 'ts' => (int) ($meta['ts'] ?? 0)] : $empty;
    }

    /** @return array{0:string,1:string}|null [jpeg, etag] */
    public static function frame(string $sessionId): ?array
    {
        $dir = self::dir($sessionId);

        if ($dir === null) {
            return null;
        }

        $path = $dir . '/frame.jpg';

        if (!is_file($path)) {
            return null;
        }

        $data = @file_get_contents($path);

        if ($data === false || $data === '') {
            return null;
        }

        return [$data, (string) (self::meta($sessionId)['seq'] ?? 0)];
    }

    /** Добавить событие ввода в очередь хоста. */
    public static function pushControl(string $sessionId, string $json): int
    {
        $dir = self::dir($sessionId);

        if ($dir === null) {
            throw new ApiError('relay_unavailable', 'relay storage is not writable', 500);
        }

        $line = json_encode(['j' => $json], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES) . "\n";

        if (@file_put_contents($dir . '/ctl.jsonl', $line, FILE_APPEND | LOCK_EX) === false) {
            throw new ApiError('relay_write', 'cannot store control event', 500);
        }

        return (int) @filesize($dir . '/ctl.jsonl');
    }

    /**
     * Забрать события после байтового смещения.
     *
     * @return array{offset:int,events:list<string>}
     */
    public static function control(string $sessionId, int $offset): array
    {
        $dir = self::dir($sessionId);
        $path = $dir === null ? null : $dir . '/ctl.jsonl';

        if ($path === null || !is_file($path)) {
            return ['offset' => 0, 'events' => []];
        }

        $size = (int) @filesize($path);

        // Файл пересоздан или смещение из прошлой жизни — начинаем сначала.
        if ($offset < 0 || $offset > $size) {
            $offset = 0;
        }

        if ($size <= $offset) {
            return ['offset' => $size, 'events' => []];
        }

        $handle = @fopen($path, 'rb');

        if ($handle === false) {
            return ['offset' => $offset, 'events' => []];
        }

        fseek($handle, $offset);
        $chunk = (string) fread($handle, max(1, $size - $offset));
        fclose($handle);

        $events = [];
        $consumed = 0;

        foreach (explode("\n", $chunk) as $line) {
            if ($line === '') {
                continue;
            }

            // Последняя строка может быть недописанной — её не трогаем.
            $decoded = json_decode($line, true);

            if (!is_array($decoded) || !isset($decoded['j'])) {
                break;
            }

            $events[] = (string) $decoded['j'];
            $consumed += strlen($line) + 1;
        }

        return ['offset' => $offset + $consumed, 'events' => $events];
    }

    /** Убрать каталоги, к которым давно не обращались. */
    public static function cleanup(int $ttl = self::TtlSeconds): int
    {
        $base = sys_get_temp_dir() . '/fdrelay';

        if (!is_dir($base)) {
            return 0;
        }

        $removed = 0;

        foreach (glob($base . '/*', GLOB_ONLYDIR) ?: [] as $dir) {
            $meta = $dir . '/meta.json';
            $ctl = $dir . '/ctl.jsonl';
            $touched = max(
                is_file($meta) ? (int) @filemtime($meta) : 0,
                is_file($ctl) ? (int) @filemtime($ctl) : 0
            );

            if ($touched === 0 || time() - $touched > $ttl) {
                foreach (glob($dir . '/*') ?: [] as $file) {
                    @unlink($file);
                }

                @rmdir($dir);
                $removed++;
            }
        }

        return $removed;
    }

    private static function nextSeq(string $dir): int
    {
        $metaFile = $dir . '/meta.json';
        $seq = 0;

        if (is_file($metaFile)) {
            $raw = @file_get_contents($metaFile);
            $meta = $raw === false ? null : json_decode($raw, true);

            if (is_array($meta)) {
                $seq = (int) ($meta['seq'] ?? 0);
            }
        }

        return $seq + 1;
    }
}
