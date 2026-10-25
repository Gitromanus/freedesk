<?php
// Временный скрипт: применяет migrations/001_init.sql к базе из config.php.
$config = require __DIR__ . '/config.php';
$file = __DIR__ . '/migrations/001_init.sql';

$sql = file_get_contents($file);
if ($sql === false) {
    fwrite(STDERR, "cannot read $file\n");
    exit(1);
}

try {
    $pdo = new PDO($config['db']['dsn'], $config['db']['user'], $config['db']['pass'], [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_TIMEOUT => 10,
    ]);
} catch (Throwable $e) {
    fwrite(STDERR, 'connect failed: ' . $e->getMessage() . "\n");
    exit(1);
}

// Убираем строчные комментарии и режем по точкам с запятой — в схеме нет процедур.
$lines = [];
foreach (explode("\n", $sql) as $line) {
    $trimmed = ltrim($line);
    if (str_starts_with($trimmed, '--')) {
        continue;
    }
    $lines[] = $line;
}

$statements = array_filter(array_map('trim', explode(';', implode("\n", $lines))));

$applied = 0;
foreach ($statements as $statement) {
    try {
        $pdo->exec($statement);
        $applied++;
    } catch (Throwable $e) {
        fwrite(STDERR, "FAILED: " . substr($statement, 0, 80) . "...\n  " . $e->getMessage() . "\n");
        exit(1);
    }
}

$tables = $pdo->query('SHOW TABLES')->fetchAll(PDO::FETCH_COLUMN);
echo "applied statements: {$applied}\n";
echo "tables: " . implode(', ', $tables) . "\n";
