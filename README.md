# FreeDesk

Аналог AnyDesk: свой signaling-сервер + P2P-сессии через WebRTC.
Сервер не передаёт ни пиксели, ни звук — только знакомит устройства и пропускает SDP/ICE.

```
Клиент A ──HTTPS poll──► server (PHP 8 + MySQL)
Клиент B ──HTTPS poll──►        │
      A ◄═══ WebRTC P2P (UDP) ═══► B   ← весь трафик здесь
```

## Текущее состояние

Работает end-to-end: агент на Windows отдаёт экран и принимает ввод, зритель
подключается по 9-значному ID и видит удалённый рабочий стол.

- сигналинг — HTTPS-поллинг через `fd.cherubino74.ru`;
- медиа — P2P WebRTC, RTP. Кодек выбирает зритель и передаёт его в offer'е:
  **H.264** (OpenH264, по умолчанию) либо **MJPEG**. Видео не идёт через data channel:
  SCTP в SIPSorcery упирается в ~300 КБ/с из-за `MAX_BURST = 4` пакета на SACK;
- H.264 — `Annex B` access unit'ы, FU-A пакетизация штатная (SIPSorcery),
  ключевой кадр раз в 5 с и по запросу зрителя (`{"t":"kf"}`), если декодер сбился;
- управление (мышь/клавиатура) — data channel, JSON;
- **текстовый буфер обмена** синхронизируется в обе стороны (data channel,
  `{"t":"cb","v":...}`, эхо подавляется);
- **курсор рисуется в кадре**: GDI — DrawIconEx с реальным указателем, DXGI —
  синтетическая стрелка с патчем фона (видна даже на неподвижном экране);
- **автопереподключение** зрителя при обрыве: до 3 попыток с паузой 2 с;
- **мониторы**: зритель видит список мониторов хоста и переключается кнопками
  «Монитор 1/2/...» (или `--display N`); окно смены — без переподключения, с IDR;
- **unattended-доступ**: у устройства может быть пароль — тогда заявка принимается
  без человека, по challenge-response (PBKDF2-SHA256), сам пароль по сети не идёт;
- статичный экран: в MJPEG кадры не отправляются вовсе (побайтовое сравнение),
  раз в 5 с пересылается последний — зритель восстанавливается после потери
  RTP-пакета; в H.264 кадры идут постоянно, но на статике это ~1 КБ P-кадры;
- захват — DXGI Desktop Duplication с откатом на GDI BitBlt (`--capture auto|dxgi|gdi`);
  DXGI дублирует вывод адаптера, поэтому умеет только основной монитор — остальные
  снимаются GDI по координатам монитора;
  в RDP-сессиях DXGI отдаёт пустые кадры — детектор замечает это за два кадра
  и стример сам переходит на GDI (и больше не возвращается);
- кодирование — H.264 (openh264) либо JPEG с масштабированием до `max-width`.

Измерено на этой машине (2560×1440, GDI, кадр 1600×900):

| Кодек | fps | На кадр | Трафик (активный экран) | В покое |
|---|---|---|---|---|
| H.264 | ~12 | ~21 мс (захват+кодирование) | 3–5 КБ/кадр, 0,3–0,5 Мбит/с | 0,1 Мбит/с |
| MJPEG | 15 | — | ~60 КБ/кадр, ~7,5 Мбит/с | ~0 (кадр раз в 5 с) |

DXGI на консольной сессии даёт ~47 fps на захват+кодирование против ~19 у GDI.

## Структура

```
agent/FreeDesk.Agent/            клиент (C#, .NET 9, Windows)
  PeerTransport.cs               WebRTC: RTP (H.264/MJPEG) + data channel
  AgentRuntime.cs                цикл агента: presence, приём заявок, сессии
  ViewerSession.cs               сторона зрителя: приём, декод, ввод
  Gui/Ui.cs                      палитра, кастомные контролы, окно с WM_NCHITTEST
  Gui/MainForm.cs                главное окно: ID, подключение, недавние, просмотр
  Gui/SettingsDialog.cs          настройки: имя, сервер, кодек/fps/битрейт, захват
  Gui/PromptDialog.cs            модальные диалоги (входящий запрос, переименование)
  Viewer/ViewerForm.cs           окно просмотра, мышь/клавиатура
  ScreenStreamer.cs              захват → H.264/JPEG → RTP, keyframe по расписанию
  ClipboardSync.cs               синхронизация текстового буфера обмена
  Codec/H264Encoder.cs           OpenH264: BGRA → I420 → Annex B (screen content)
  Codec/H264Decoder.cs           OpenH264: Annex B → BGRA Bitmap
  Codec/BgraScaler.cs            масштабирование кадра (bilinear) до max-width
  VideoCodec.cs                  h264 | mjpeg, разбор значения из конфига/offer
  UnattendedAuth.cs              PBKDF2-хеш пароля и challenge-response
  Native/openh264.dll            кодек Cisco (BSD), кладётся рядом с exe
  Capture/CaptureFactory.cs      выбор источника: dxgi с откатом на gdi
  Capture/Displays.cs            список мониторов и их виртуальные координаты
  Capture/DxgiScreenCapture.cs   Desktop Duplication (COM-интероп без зависимостей)
  Capture/GdiScreenCapture.cs    BitBlt/PrintWindow + курсор, работает в RDP-сессиях
  Codec/JpegEncoder.cs           JPEG + масштабирование
  Input/InputInjector.cs         SendInput, нормализованные координаты
  tests/p2p-smoke.ps1            два процесса: хост + зритель
  tests/input-smoke.ps1          проверка инжекта ввода
server/
  public/            ← это кладётся в public_html
    index.php        фронт-контроллер, весь роутинг
    .htaccess        rewrite + проброс Authorization
    test.html        отладочная страница (2 окна = 2 устройства)
  src/               API: Devices, Sessions, Auth, RateLimit, Cleanup
  migrations/001_init.sql
  scripts/cleanup.php
  tests/smoke.ps1    e2e-проверка через PowerShell
  config.example.php → config.php (в git не попадает)
  bootstrap.php      автозагрузка + подключение БД
```

## Агент

```powershell
# сборка
dotnet build agent/FreeDesk.Agent/FreeDesk.Agent.csproj

# агент: показать свой ID и принимать подключения
.\agent\FreeDesk.Agent\bin\Debug\net9.0-windows\freedesk-agent.exe --auto-accept

# зритель: открыть окно с чужим экраном
... --view 123456789

# отдельные профили (несколько агентов на одной машине)
... --profile work
```

| Флаг | Значение |
|---|---|
| `--view <id>` | окно просмотра удалённого экрана |
| `--settings` | открыть окно приложения сразу с настройками |
| `--connect <id>` | инициировать сессию без окна |
| `--input-test <id>` | проверка ввода без окна |
| `--auto-accept` | принимать входящие без Enter |
| `--fps <1..60>` | частота кадров (по умолчанию 12) |
| `--codec <кодек>` | видео: `h264` (по умолчанию) \| `mjpeg` |
| `--quality <10..100>` | качество JPEG (по умолчанию 80, только MJPEG) |
| `--max-width <px>` | ширина кадра, 0 — без масштабирования (1920) |
| `--capture <режим>` | захват экрана: `auto` \| `dxgi` \| `gdi` (по умолчанию auto) |
| `--profile <имя>` | файл конфига `%APPDATA%\FreeDesk\<имя>.json` |
| `--reset` | забыть токен и зарегистрироваться заново |
| `--display <номер>` | монитор для трансляции/просмотра (0 — основной) |
| `--set-password <пароль>` | сохранить пароль unattended-доступа в профиль и выйти |
| `--clear-password` | удалить пароль профиля и выйти |
| `--password <пароль>` | пароль устройства при подключении (иначе спросит окно) |

Конфиг и токен устройства лежат в `%APPDATA%\FreeDesk\<профиль>.json`.

## Unattended-доступ (пароль устройства)

Пароль задаётся на стороне хоста и хранится только хешем:

```powershell
.\freedesk-agent.exe --set-password "длинный пароль"   # сохранить в профиль и выйти
.\freedesk-agent.exe --clear-password                 # удалить
```

То же самое — в окне настроек («Пароль для доступа без подтверждения»,
кнопка «Сбросить пароль»). Проверка идёт по сигнальному каналу, до WebRTC:

1. зритель запрашивает сессию;
2. хост отвечает `challenge`: `{nonce, salt, iterations}` — nonce одноразовый, живёт 60 с;
3. зритель отвечает `auth`: `proof = PBKDF2(PBKDF2(пароль, salt), nonce)`;
4. хост сверяет за постоянное время: успех — `accept` без участия человека,
   ошибка — `auth_failed` с новым nonce (до 3 попыток, затем `reject`).

Что важно:

- пароль не уходит ни на сервер, ни зрителю — в сигналах только nonce и proof;
- в конфиге лежат соль, итерации (120 000) и хеш: файл профиля — секрет;
- пароль сильнее автоприёма: если он задан, `--auto-accept` не отменяет проверку;
- зритель без `--password` получает запрос пароля в окне (в headless — отказ).

## Портабл-сборка (Windows)

Две сборки из одного проекта (`tools/publish.ps1`):

```
agent\publish\FreeDesk.exe        GUI-приложение (WinExe): двойной клик открывает
                                  только окно, без терминала; консольные флаги
                                  из терминала тоже работают (AttachConsole)
agent\publish\freedesk-agent.exe  консольная версия для скриптов и тестов
agent\publish\openh264.dll       кодек H.264 — обязательно рядом с exe
```

Окно без системной рамки, но с нативным поведением (перетаскивание, ресайз за
края, Aero Snap, двойной клик по заголовку — через WM_NCHITTEST) и компактными
кнопками свернуть/развернуть/закрыть; тёмная тема в стиле сайта, скруглённые
углы (Win11). Все панели рисуются с двойной буферизацией, без прозрачных
подписей — интерфейс не мерцает и не ломается при развороте окна.
Удалённый экран открывается прямо в окне приложения с кнопкой «Отключиться»;
боковая панель при этом сворачивается в узкую полоску (шевроном можно
сворачивать и вручную), после отключения — разворачивается обратно. Действия
с устройствами из списка «Недавние» — значки «изменить»/«удалить» прямо в
строке (появляются при наведении/выделении). Входящий запрос — модальное окно
вместо системного MessageBox.

При первом запуске зарегистрируется на сервере и покажет твой ID.
Чтобы подключиться к другому устройству - введи его ID в поле и нажми
«Подключиться». Галочка «Принимать входящие автоматически» сохраняется
в конфиг профиля.

## API

Все ответы — `{"ok":true, ...}` либо `{"ok":false,"error":"code","message":"..."}`.
Авторизация: `Authorization: Bearer <device_token>` (fallback: `X-Device-Token`).

| Метод | Путь | Назначение |
|---|---|---|
| GET | `/api/v1/health` | живость сервиса и БД |
| POST | `/api/v1/device/register` | `{name, os, pubkey?}` → `public_id` + `device_token` |
| POST | `/api/v1/presence` | heartbeat (раз в 15 с) |
| GET | `/api/v1/resolve/{id}` | онлайн ли устройство |
| POST | `/api/v1/session/request` | `{target_id}` → `session_id` (state=pending) |
| GET | `/api/v1/session/incoming` | входящие заявки |
| POST | `/api/v1/session/answer` | `{session_id, action: accept\|reject\|end}` |
| POST | `/api/v1/session/signal` | `{session_id, type, payload}` — offer/answer/ice; в offer `payload.codec` = `h264`\|`mjpeg` |
| GET | `/api/v1/session/poll` | `?session_id&since&wait` — сообщения после курсора |

`device_token` показывается клиенту один раз: в базе лежит только `sha256`.
`wait` (long-poll, до 20 с) по умолчанию 0 — на shared-хостинге Apache-воркеры лучше не занимать.

## Установка на Beget

Развёрнуто на `https://fd.cherubino74.ru` (shared-хостинг, PHP 8.3, MySQL 5.7).

На этом хостинге домашний каталог SSH/FTP-пользователя **совпадает** с каталогом сайта:

```
/home/n/netesnl9/fd.cherubino74.ru/     ← ~ (home)
  bootstrap.php
  config.php
  src/  migrations/  scripts/  tests/
  public_html/
    index.php  .htaccess  test.html  diag.php
```

Порядок развёртывания с нуля:

1. **База.** Панель Beget → MySQL → создать БД (здесь: `netesnl9_fd`).
2. **Схема.** `php8.3 tools/migrate.php` (или импорт `migrations/001_init.sql` в phpMyAdmin).
3. **PHP.** Для сайта выставить **8.3**. CLI-бинарник на сервере — `/usr/local/bin/php8.3`
   (просто `php` там 5.6, для нашего кода не годится).
4. **Файлы.** `tools/deploy.ps1` — заливает `server/public/*` в `public_html`,
   остальное — на уровень выше.
5. **Конфиг.** `config.example.php` → `config.php`, вписать `dsn` (dbname!), `user`, `pass`.
6. **Проверка:**
   ```
   curl https://fd.cherubino74.ru/api/v1/health
   curl https://fd.cherubino74.ru/            # карта сервиса
   ```
7. **Smoke-тест** (с локальной машины, PHP не нужен):
   ```powershell
   powershell -ExecutionPolicy Bypass -File server/tests/smoke.ps1 -Base https://fd.cherubino74.ru
   ```
8. **Ручная проверка:** открыть `https://fd.cherubino74.ru/test.html` в двух окнах,
   зарегистрировать оба, подключиться по ID одного из другого.

### Деплой из Windows

```powershell
# залить код и проверить health
powershell -ExecutionPolicy Bypass -File tools/deploy.ps1

# произвольная команда на сервере
powershell -ExecutionPolicy Bypass -File tools/ssh.ps1 -Action run -Command "ls -la"

# один файл
powershell -ExecutionPolicy Bypass -File tools/ssh.ps1 -Action put -Local .\file -Remote /path/file
```

`tools/ssh.ps1` использует штатный OpenSSH + `tools/askpass.exe`, пароль берёт из
`%TEMP%\fd_ssh_pass.txt`. Для cron-уборки на сервере: `php8.3 scripts/cleanup.php`.


## cron (необязательно)

```
*/5 * * * * /usr/local/bin/php /home/u/USER/fd.cherubino74.ru/scripts/cleanup.php
```

Без cron уборка работает лениво: `Cleanup::maybeRun()` раз в 60 секунд на любом запросе.

## Ограничения shared-хостинга

- нет WebSocket/демонов → сигналинг на коротких HTTPS-запросах (это осознанно, см. `wait`);
- нет TURN/UDP → на MVP используем бесплатный relay (Metered Open Relay) либо публичный STUN;
- MySQL 5.7 (EOL) → только сигнальные метаданные, ничего чувствительного сверх хэшей токенов.

Когда пользователей станет много — сигналинг переезжает на VPS (Go + WebSocket),
coturn рядом, API и панель остаются на Beget.

## Что дальше

1. **Аппаратное кодирование** (NVENC/QSV через Media Foundation): OpenH264 тратит
   ~20 мс/кадр на 1600×900 — на 1080p+ это упирается в CPU.
2. Адаптивный битрейт: смотреть RTCP-отчёты и менять `TargetBitrate` на лету,
   вместо фиксированных 3000 кбит/с.
3. Аккаунты и адресная книга (`users`/`user_devices` уже в схеме).
4. Смена/сброс пароля только владельцем машины + журнал подключений (кто, когда, с какого IP).
5. TURN-пул на VPS (сейчас только STUN, симметричный NAT не пройдёт).
6. Трей, автозапуск, установщик, подпись бинарника.

## Безопасность (минимум, уже есть)

- TLS обязателен (Beget Let's Encrypt).
- Токены — 256 бит, в БД только sha256.
- Rate limit на регистрацию (20/час на IP) и на все запросы (120/мин на IP).
- Сессии: только участники могут читать/писать сигналы; заявки протухают через 15 минут.
- Сигналы удаляются через 10 минут; в логах нет содержимого сессий.
- Пароль unattended-доступа: PBKDF2-SHA256, 120 000 итераций, соль 16 байт;
  в сигналах — только одноразовый nonce и proof, 3 попытки, окно 60 с.
