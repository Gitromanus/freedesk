<?php
declare(strict_types=1);

/**
 * Точка входа для index.php и CLI-скриптов.
 * Ищет config.php рядом с собой, регистрирует автозагрузку src/*.
 */

mb_internal_encoding('UTF-8');

spl_autoload_register(static function (string $class): void {
    $prefix = 'FreeDesk\\';
    if (!str_starts_with($class, $prefix)) {
        return;
    }
    $file = __DIR__ . '/src/' . str_replace('\\', '/', substr($class, strlen($prefix))) . '.php';
    if (is_file($file)) {
        require $file;
    }
});

$configFile = __DIR__ . '/config.php';

if (!is_file($configFile)) {
    if (PHP_SAPI === 'cli') {
        fwrite(STDERR, "config.php not found — copy config.example.php and fill credentials\n");
        exit(1);
    }
    http_response_code(500);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode([
        'ok' => false,
        'error' => 'config_missing',
        'message' => 'Create config.php from config.example.php',
    ]);
    exit;
}

/** @var array $CONFIG */
$CONFIG = require $configFile;

\FreeDesk\Config::load($CONFIG);
\FreeDesk\Db::init($CONFIG['db']);

set_exception_handler(static function (\Throwable $e) use ($CONFIG): void {
    if ($e instanceof \FreeDesk\ApiError) {
        \FreeDesk\Http::fail($e->errorCode, $e->getMessage(), $e->status);
        return;
    }
    error_log('[freedesk] ' . $e);
    $message = !empty($CONFIG['debug']) ? $e->getMessage() : 'internal server error';
    \FreeDesk\Http::fail('internal', $message, 500);
});
