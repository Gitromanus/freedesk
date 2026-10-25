<?php
declare(strict_types=1);

namespace FreeDesk;

/** Ошибка, которую можно безопасно отдать клиенту как JSON. */
final class ApiError extends \RuntimeException
{
    public function __construct(
        public readonly string $errorCode,
        string $message,
        public readonly int $status = 400,
    ) {
        parent::__construct($message);
    }
}
