<?php
// download.php — счетчик скачиваний + отдача файла
// Счетчик хранится в файле вне public_html (или в корне сайта, но не в webroot)
// Путь к счетчику — в домашнем каталоге, недоступен извне.

$countFile = '/home/n/netesnl9/fd.cherubino74.ru/downloads.count';
$file = '/home/n/netesnl9/fd.cherubino74.ru/public_html/FreeDesk.exe';

// Инкрементируем счетчик (атомарно через flock)
$fp = fopen($countFile, 'c+');
if ($fp) {
    if (flock($fp, LOCK_EX)) {
        $count = (int) trim(stream_get_contents($fp));
        $count++;
        ftruncate($fp, 0);
        rewind($fp);
        fwrite($fp, (string) $count);
        fflush($fp);
        flock($fp, LOCK_UN);
    }
    fclose($fp);
}

// Отдаем файл
if (!file_exists($file)) {
    http_response_code(404);
    echo 'File not found';
    exit;
}

header('Content-Type: application/octet-stream');
header('Content-Disposition: attachment; filename="FreeDesk.exe"');
header('Content-Length: ' . filesize($file));
header('Cache-Control: no-cache, no-store, must-revalidate');

readfile($file);
exit;
