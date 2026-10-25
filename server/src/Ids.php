<?php
declare(strict_types=1);

namespace FreeDesk;

/**
 * Публичные 9-значные ID устройств (как адрес в AnyDesk).
 * Уникальность обеспечивает UNIQUE-индекс devices.public_id,
 * здесь — просто генерация кандидата.
 */
final class Ids
{
    public const PATTERN = '/^\d{9}$/';

    public static function generate(): string
    {
        return (string) random_int(100_000_000, 999_999_999);
    }

    public static function isPublicId(string $value): bool
    {
        return (bool) preg_match(self::PATTERN, $value);
    }
}
