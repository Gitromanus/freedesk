<?php
declare(strict_types=1);

/**
 * Диагностика раскладки файлов. Удалите этот файл после настройки.
 * Ничего секретного не печатает: ни паролей, ни токенов.
 */

header('Content-Type: text/plain; charset=utf-8');

$here = __DIR__;
$parent = dirname($here);

$line = static function (string $label, $value): void {
    printf("%-38s %s\n", $label, is_bool($value) ? ($value ? 'ДА' : 'нет') : (string) $value);
};

echo "FreeDesk diagnostics\n";
echo str_repeat('-', 62), "\n";

$line('PHP version', PHP_VERSION);
$line('SAPI', PHP_SAPI);
$line('document root', $_SERVER['DOCUMENT_ROOT'] ?? '?');
$line('каталог public (__DIR__)', $here);
echo "\n";

$line('../bootstrap.php', is_file($parent . '/bootstrap.php'));
$line('./bootstrap.php', is_file($here . '/bootstrap.php'));
$line('../src/Db.php', is_file($parent . '/src/Db.php'));
$line('./src/Db.php', is_file($here . '/src/Db.php'));
$line('../config.php', is_file($parent . '/config.php'));
$line('./config.php', is_file($here . '/config.php'));
echo "\n";

$line('.htaccess здесь', is_file($here . '/.htaccess'));
$line('mod_rewrite (если 404 ниже — не работает)', 'проверьте GET /nope: должен быть JSON 404');

echo "\nСодержимое родительского каталога (", $parent, "):\n";
$items = @scandir($parent) ?: [];
foreach ($items as $item) {
    if ($item === '.' || $item === '..') {
        continue;
    }
    printf("  %-30s %s\n", $item, is_dir($parent . '/' . $item) ? '<dir>' : 'file');
}

$configFile = null;
foreach ([$parent . '/config.php', $here . '/config.php'] as $candidate) {
    if (is_file($candidate)) {
        $configFile = $candidate;
        break;
    }
}

echo "\nБаза данных:\n";

if ($configFile === null) {
    echo "  config.php не найден — скопируйте config.example.php в config.php\n";
    exit;
}

$line('config.php найден', $configFile);

try {
    $config = require $configFile;
} catch (\Throwable $e) {
    echo '  ошибка чтения config.php: ', $e->getMessage(), "\n";
    exit;
}

try {
    $pdo = new PDO($config['db']['dsn'], $config['db']['user'], $config['db']['pass'], [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_TIMEOUT => 5,
    ]);
    $line('подключение', 'OK');

    $tables = $pdo->query('SHOW TABLES')->fetchAll(PDO::FETCH_COLUMN);
    $line('таблиц найдено', count($tables));

    $required = ['devices', 'sessions', 'signals', 'state', 'rate_limits'];
    $missing = array_values(array_diff($required, $tables));
    $line('обязательные таблицы', $missing === [] ? 'все на месте' : 'НЕТ: ' . implode(', ', $missing));

    if ($missing !== []) {
        echo "  → импортируйте migrations/001_init.sql в эту БД\n";
    }
} catch (\Throwable $e) {
    $line('подключение', 'ОШИБКА');
    echo '  ', $e->getMessage(), "\n";
    echo "  → проверьте dsn/user/pass в config.php (в панели Beget: MySQL)\n";
}

// ── резервный канал: виден ли поток кадров ──
echo "\nРезервный канал (relay):\n";
$line('sys_get_temp_dir()', sys_get_temp_dir());
$relayBase = sys_get_temp_dir() . '/fdrelay';

if (!is_dir($relayBase)) {
    $line('каталоги сессий', 'нет — хост не отправил ни одного кадра');
} else {
    $sessions = glob($relayBase . '/*', GLOB_ONLYDIR) ?: [];

    if ($sessions === []) {
        $line('каталоги сессий', 'пусто');
    }

    foreach ($sessions as $dir) {
        $meta = $dir . '/meta.json';
        $seq = 0;
        $ts = 0;

        if (is_file($meta)) {
            $raw = (string) @file_get_contents($meta);
            $decoded = json_decode($raw, true);

            if (is_array($decoded)) {
                $seq = (int) ($decoded['seq'] ?? 0);
                $ts = (int) ($decoded['ts'] ?? 0);
            }
        }

        $age = $ts > 0 ? time() - $ts : -1;
        printf(
            "  %s  кадров=%d  последний %s назад\n",
            basename($dir),
            $seq,
            $age < 0 ? '—' : ($age . ' с')
        );
    }
}
