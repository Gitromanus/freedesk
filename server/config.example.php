<?php
declare(strict_types=1);

/**
 * Скопируйте в config.php и заполните доступы из панели Beget.
 * config.php в git не попадает.
 */
return [
    'db' => [
        // Локальный MySQL Beget: host обычно localhost, имя БД и пользователя — с префиксом аккаунта.
        'dsn' => 'mysql:host=localhost;dbname=CHANGE_ME;charset=utf8mb4',
        'user' => 'CHANGE_ME',
        'pass' => 'CHANGE_ME',
    ],

    // Сколько секунд устройство считается онлайн после heartbeat.
    'online_window' => 60,

    // Как часто клиент шлёт presence, секунды.
    'presence_interval' => 15,

    // Сколько живёт необработанная заявка на подключение, секунды.
    'session_ttl' => 900,

    // Через сколько чистить сигналы (SDP/ICE уже доставлены), секунды.
    'signal_ttl' => 600,

    // Лимит регистраций новых устройств с одного IP в час.
    'register_limit' => 20,

    // Показывать тексты исключений клиенту. На проде — false.
    'debug' => false,
];
