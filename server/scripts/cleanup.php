<?php
declare(strict_types=1);

/**
 * CLI-уборка для cron (если есть доступ к php-cli на хостинге).
 * Если cron недоступен — уборка всё равно идёт лениво из API (Cleanup::maybeRun).
 *
 * Пример строки для cron Beget:
 *   /usr/local/bin/php /home/u/USER/fd.cherubino74.ru/scripts/cleanup.php
 */

require dirname(__DIR__) . '/bootstrap.php';

$result = \FreeDesk\Cleanup::run();

echo json_encode(
    ['ok' => true, 'cleaned' => $result] + ['at' => date('c')],
    JSON_UNESCAPED_UNICODE
), PHP_EOL;
