<?php
// stats.php — счетчик скачиваний (публичный)

$countFile = '/home/n/netesnl9/fd.cherubino74.ru/downloads.count';
$count = 0;
if (file_exists($countFile)) {
    $count = (int) trim(file_get_contents($countFile));
}

header('Content-Type: application/json; charset=utf-8');
echo json_encode(['count' => $count]);
