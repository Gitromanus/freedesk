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

        default:
            Http::fail('not_found', 'no such endpoint: ' . $method . ' ' . $path, 404);
    }
} catch (\PDOException $e) {
    error_log('[freedesk] db error: ' . $e->getMessage());
    Http::fail('db_error', 'database error', 500);
}
