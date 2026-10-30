<?php
declare(strict_types=1);

/**
 * Диагностика: последние сессии с состоянием и временем.
 * Запуск на сервере: /usr/local/bin/php8.3 scripts/sessions.php
 */

require __DIR__ . '/../bootstrap.php';

use FreeDesk\Db;

$rows = Db::all(
    "SELECT s.id, s.state, s.created_at, s.ended_at,
            f.public_id AS from_id, f.name AS from_name,
            t.public_id AS to_id, t.name AS to_name
     FROM sessions s
     JOIN devices f ON f.id = s.from_device
     JOIN devices t ON t.id = s.to_device
     ORDER BY s.created_at DESC
     LIMIT 10"
);

foreach ($rows as $r) {
    printf(
        "%s  %s (%s) -> %s (%s)  %-8s  %s / %s\n",
        substr((string) $r['id'], 0, 8),
        $r['from_id'],
        $r['from_name'],
        $r['to_id'],
        $r['to_name'],
        $r['state'],
        $r['created_at'],
        $r['ended_at'] ?? '-'
    );
}

$signals = Db::all('SELECT session_id, COUNT(*) AS n FROM signals GROUP BY session_id ORDER BY MAX(id) DESC LIMIT 5');

echo "\nсигналы по последним сессиям:\n";
foreach ($signals as $s) {
    printf("  %s  %d\n", substr((string) $s['session_id'], 0, 8), $s['n']);
}

// Если передан префикс id — разворачиваем сигналы этой сессии по порядку.
$prefix = $argv[1] ?? null;

if ($prefix !== null) {
    $detail = Db::all(
        'SELECT id, type, from_device, created_at, LEFT(COALESCE(payload, \'\'), 120) AS payload
         FROM signals WHERE session_id LIKE ? ORDER BY id ASC',
        [$prefix . '%']
    );

    echo "\nсигналы сессии {$prefix}:\n";
    foreach ($detail as $d) {
        printf(
            "  #%s %-12s from=%s %s  %s\n",
            $d['id'],
            $d['type'],
            $d['from_device'],
            $d['created_at'],
            str_replace("\n", ' ', (string) $d['payload'])
        );
    }
}
