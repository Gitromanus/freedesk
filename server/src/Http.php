<?php
declare(strict_types=1);

namespace FreeDesk;

final class Http
{
    public static function json(array $data, int $status = 200): never
    {
        http_response_code($status);
        header('Content-Type: application/json; charset=utf-8');
        header('Cache-Control: no-store');
        echo json_encode($data, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        exit;
    }

    public static function fail(string $error, string $message, int $status = 400): never
    {
        self::json(['ok' => false, 'error' => $error, 'message' => $message], $status);
    }

    /** Тело запроса как массив. Поддерживает JSON и form-urlencoded (для отладки через curl). */
    public static function body(): array
    {
        $raw = file_get_contents('php://input');

        if ($raw === false || trim($raw) === '') {
            return $_POST;
        }

        $data = json_decode($raw, true);

        if (!is_array($data)) {
            throw new ApiError('bad_json', 'request body must be a JSON object');
        }

        return $data;
    }

    public static function query(string $key, mixed $default = null): mixed
    {
        return $_GET[$key] ?? $default;
    }

    /** Токен устройства: Authorization: Bearer … либо X-Device-Token (на случай, если Apache режет заголовок). */
    public static function deviceToken(): ?string
    {
        $header = $_SERVER['HTTP_AUTHORIZATION']
            ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION']
            ?? '';

        if ($header !== '' && preg_match('/^Bearer\s+(.+)$/i', $header, $m)) {
            return trim($m[1]);
        }

        $fallback = $_SERVER['HTTP_X_DEVICE_TOKEN'] ?? '';

        return $fallback !== '' ? trim($fallback) : null;
    }

    public static function clientIp(): string
    {
        return (string) ($_SERVER['REMOTE_ADDR'] ?? '0.0.0.0');
    }
}
