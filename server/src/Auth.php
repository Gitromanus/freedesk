<?php
declare(strict_types=1);

namespace FreeDesk;

final class Auth
{
    /** @return array<string, mixed> строка устройства, соответствующая токену */
    public static function device(): array
    {
        $token = Http::deviceToken();

        if ($token === null || $token === '') {
            throw new ApiError('unauthorized', 'device token required', 401);
        }

        return Devices::byToken($token);
    }
}
