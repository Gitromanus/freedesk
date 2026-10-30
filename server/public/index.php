<?php
declare(strict_types=1);

/**
 * FreeDesk signaling API — единственная точка входа.
 * Всё остальное (src/, config.php) лежит выше document root.
 */

// bootstrap.php ищется уровнем выше public_html; второй вариант — на случай,
// если всё залито в одну папку (тогда src/ и config.php закрывает .htaccess).
$bootstrap = null;
foreach ([__DIR__ . '/../bootstrap.php', __DIR__ . '/bootstrap.php'] as $candidate) {
    if (is_file($candidate)) {
        $bootstrap = $candidate;
        break;
    }
}

if ($bootstrap === null) {
    http_response_code(500);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode([
        'ok' => false,
        'error' => 'bootstrap_missing',
        'message' => 'bootstrap.php не найден. Положите его в каталог выше public_html.',
        'expected' => __DIR__ . '/../bootstrap.php',
        'hint' => 'Откройте /diag.php — он покажет фактическую раскладку файлов.',
    ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

require $bootstrap;

use FreeDesk\Auth;
use FreeDesk\Cleanup;
use FreeDesk\Db;
use FreeDesk\Devices;
use FreeDesk\Http;
use FreeDesk\Relay;
use FreeDesk\RateLimit;
use FreeDesk\Sessions;

$method = strtoupper((string) ($_SERVER['REQUEST_METHOD'] ?? 'GET'));
$path = (string) (parse_url((string) ($_SERVER['REQUEST_URI'] ?? '/'), PHP_URL_PATH) ?? '/');
$path = (string) preg_replace('#^/index\.php#', '', $path);
$path = '/' . trim($path, '/');

Cleanup::maybeRun();

if ($method === 'OPTIONS') {
    header('Allow: GET, POST, OPTIONS');
    http_response_code(204);
    exit;
}

try {
    switch (true) {
        case $method === 'GET' && $path === '/':
            Http::json([
                'ok' => true,
                'service' => 'freedesk-signaling',
                'version' => '1.0',
                'health' => '/api/v1/health',
                'test_page' => '/test.html',
                'endpoints' => [
                    'POST /api/v1/device/register',
                    'POST /api/v1/presence',
                    'GET  /api/v1/resolve/{id}',
                    'POST /api/v1/session/request',
                    'GET  /api/v1/session/incoming',
                    'POST /api/v1/session/answer',
                    'POST /api/v1/session/signal',
                    'GET  /api/v1/session/poll',
                ],
            ]);

        case $method === 'GET' && $path === '/api/v1/health':
            $dbOk = true;
            try {
                Db::one('SELECT 1 AS ok');
            } catch (\Throwable) {
                $dbOk = false;
            }
            Http::json([
                'ok' => true,
                'service' => 'freedesk-signaling',
                'db' => $dbOk,
                'server_time' => time(),
            ]);

            // no break — Http::json завершает выполнение

        case $method === 'POST' && $path === '/api/v1/device/register':
            RateLimit::hit('ip:' . Http::clientIp(), 120, 60);
            Http::json(['ok' => true] + Devices::register(Http::body(), Http::clientIp()));

        case $method === 'POST' && $path === '/api/v1/presence':
            $device = Auth::device();
            $body = Http::body();
            $name = isset($body['name']) ? (string) $body['name'] : null;
            Http::json([
                'ok' => true,
                'server_time' => Devices::touch((int) $device['id'], $name),
            ]);

        case $method === 'GET' && preg_match('#^/api/v1/resolve/(\d{9})$#', $path, $m) === 1:
            $device = Auth::device();
            Http::json(['ok' => true] + Devices::resolve($m[1], (int) $device['id']));

        case $method === 'POST' && $path === '/api/v1/session/request':
            $device = Auth::device();
            Http::json(['ok' => true] + Sessions::request((int) $device['id'], Http::body()));

        case $method === 'GET' && $path === '/api/v1/session/incoming':
            $device = Auth::device();
            Http::json(['ok' => true] + Sessions::incoming((int) $device['id'], (int) ($_GET['wait'] ?? 0)));

        case $method === 'POST' && $path === '/api/v1/session/answer':
            $device = Auth::device();
            Http::json(['ok' => true] + Sessions::answer((int) $device['id'], Http::body()));

        case $method === 'POST' && $path === '/api/v1/session/signal':
            $device = Auth::device();
            Http::json(['ok' => true] + Sessions::signal((int) $device['id'], Http::body()));

        case $method === 'GET' && $path === '/api/v1/session/poll':
            $device = Auth::device();
            Http::json(['ok' => true] + Sessions::poll((int) $device['id'], $_GET));

        // ── резервный канал: кадры и ввод через сервер, когда NAT не пропустил P2P ──

        case $method === 'POST' && $path === '/api/v1/relay/frame':
            $device = Auth::device();
            $sid = Sessions::requireParticipant((int) $device['id'], (string) ($_GET['session_id'] ?? ''));

            $jpeg = file_get_contents('php://input', false, null, 0, Relay::MaxFrameBytes + 1);

            if ($jpeg === false || strlen($jpeg) < 64) {
                Http::fail('bad_frame', 'empty or too small frame', 400);
            }

            if (strlen($jpeg) > Relay::MaxFrameBytes) {
                Http::fail('frame_too_large', 'frame exceeds relay limit', 413);
            }

            Http::json(['ok' => true] + Relay::putFrame($sid, $jpeg));

        case $method === 'GET' && $path === '/api/v1/relay/frame':
            $device = Auth::device();
            $sid = Sessions::requireParticipant((int) $device['id'], (string) ($_GET['session_id'] ?? ''));
            Http::json(['ok' => true] + Relay::meta($sid));

        case $method === 'GET' && $path === '/api/v1/relay/image':
            $device = Auth::device();
            $sid = Sessions::requireParticipant((int) $device['id'], (string) ($_GET['session_id'] ?? ''));

            // Зритель присылает номер последнего кадра. Если нового нет — ждём
            // (long-poll, как в /session/poll): в покое это единицы запросов в минуту
            // вместо двухсот, при активном потоке запрос уходит ровно на кадр.
            $since = isset($_GET['since']) ? (int) $_GET['since'] : -1;
            $wait = min(5, max(0, (int) ($_GET['wait'] ?? 0)));
            $deadline = microtime(true) + $wait;

            while ($since >= 0 && $since === Relay::meta($sid)['seq']) {
                if (microtime(true) >= $deadline) {
                    Http::json(['ok' => true, 'changed' => false, 'seq' => $since]);
                }

                usleep(150_000);
            }

            $meta = Relay::meta($sid);
            $frame = Relay::frame($sid);

            if ($frame === null) {
                Http::json(['ok' => true, 'changed' => false, 'seq' => 0]);
            }

            header('Content-Type: image/jpeg');
            header('Cache-Control: no-store');
            header('X-Frame-Seq: ' . $meta['seq']);
            echo $frame[0];
            exit;

        case $method === 'POST' && $path === '/api/v1/relay/control':
            $device = Auth::device();
            $sid = Sessions::requireParticipant((int) $device['id'], (string) ($_GET['session_id'] ?? ''));
            $body = Http::body();
            $event = isset($body['event']) ? json_encode($body['event'], JSON_UNESCAPED_UNICODE) : null;

            if ($event === null) {
                Http::fail('bad_event', 'event is required', 400);
            }

            Http::json(['ok' => true, 'offset' => Relay::pushControl($sid, $event)]);

        case $method === 'GET' && $path === '/api/v1/relay/control':
            $device = Auth::device();
            $sid = Sessions::requireParticipant((int) $device['id'], (string) ($_GET['session_id'] ?? ''));

            $offset = (int) ($_GET['offset'] ?? 0);
            $wait = min(5, max(0, (int) ($_GET['wait'] ?? 0)));
            $deadline = microtime(true) + $wait;

            // long-poll: в покое хост не дёргает сервер четыре раза в секунду
            while (true) {
                $batch = Relay::control($sid, $offset);

                if ($batch['events'] !== [] || microtime(true) >= $deadline) {
                    Http::json(['ok' => true] + $batch);
                }

                usleep(200_000);
            }

        default:
            Http::fail('not_found', 'no such endpoint: ' . $method . ' ' . $path, 404);
    }
} catch (\PDOException $e) {
    error_log('[freedesk] db error: ' . $e->getMessage());
    Http::fail('db_error', 'database error', 500);
}
