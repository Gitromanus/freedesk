using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using FreeDesk.Agent;
using FreeDesk.Agent.Codec;
using FreeDesk.Agent.Gui;
using FreeDesk.Agent.Viewer;

// GUI-сборка (WinExe) не получает консоль при запуске из терминала — цепляемся
// к консоли родителя, чтобы флаги вроде --capture-test печатали что-нибудь.
if (args.Length > 0)
{
    TryAttachParentConsole();
}

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // нет консоли — не страшно
}

// Кодек встроен в exe: распаковываем до любого режима (включая --capture-test).
NativeCodec.Ensure();

// Профиль читаем до загрузки конфига: он определяет, какой файл открывать.
var profile = "agent";

// GUI-режим: явный --gui, либо запуск без режимных аргументов (портабл, двойной клик).
var guiRequested = args.Contains("--gui") || args.Length == 0;

for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--profile")
    {
        profile = args[i + 1];
    }
}

// В GUI без явного --profile подбираем свободный профиль, чтобы два окна
// не делили один device token (и один ID).
if (guiRequested && !args.Contains("--profile"))
{
    profile = PickFreeProfile();
}

AgentConfig.Profile = profile;
var config = AgentConfig.Load();
string? connectTo = null;
string? viewTarget = null;
string? inputTestTarget = null;
string? viewPassword = null;
string? newPassword = null;
bool clearPassword = false;
bool guiMode = guiRequested;
var openSettings = false;
var startHidden = false;
var installAutostart = false;
var removeAutostart = false;
int? displayArg = null;
string? sendFile = null;
string? downloadPath = null;
var captureTestFrames = 0;
long captureQuality = 70;
var captureCodec = "mjpeg";
var connectTargets = new List<string>();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" when i + 1 < args.Length:
            config.Server = args[++i];
            break;
        case "--name" when i + 1 < args.Length:
            config.Name = args[++i];
            break;
        case "--profile" when i + 1 < args.Length:
            i++; // уже учтён до загрузки конфига
            break;
        case "--connect" when i + 1 < args.Length:
            connectTo = args[++i];
            connectTargets.Add(connectTo);
            break;
        case "--view" when i + 1 < args.Length:
            viewTarget = args[++i];
            break;
        case "--console":
            guiMode = false;
            break;
        case "--gui":
            guiMode = true;
            break;
        case "--settings":
            guiMode = true;
            openSettings = true;
            break;
        case "--tray":
            guiMode = true;
            startHidden = true;
            break;
        case "--install-autostart":
            installAutostart = true;
            break;
        case "--remove-autostart":
            removeAutostart = true;
            break;
        case "--input-test" when i + 1 < args.Length:
            inputTestTarget = args[++i];
            break;
        case "--password" when i + 1 < args.Length:
            viewPassword = args[++i];
            break;
        case "--send-file" when i + 1 < args.Length:
            sendFile = args[++i];
            break;
        case "--download" when i + 1 < args.Length:
            downloadPath = args[++i];
            break;
        case "--set-password" when i + 1 < args.Length:
            newPassword = args[++i];
            break;
        case "--clear-password":
            clearPassword = true;
            break;
        case "--codec" when i + 1 < args.Length:
            var codec = args[++i].ToLowerInvariant();
            captureCodec = codec is "mjpeg" or "h264" ? codec : "mjpeg";
            config.Codec = captureCodec;
            break;
        case "--capture-test":
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var frameCount))
            {
                captureTestFrames = frameCount;
                i++;
            }
            else
            {
                captureTestFrames = 20;
            }
            break;
        case "--quality" when i + 1 < args.Length:
            captureQuality = long.TryParse(args[++i], out var q) ? Math.Clamp(q, 10, 100) : 70;
            config.JpegQuality = captureQuality;
            break;
        case "--fps" when i + 1 < args.Length:
            config.Fps = int.TryParse(args[++i], out var fps) ? Math.Clamp(fps, 1, 60) : config.Fps;
            break;
        case "--max-width" when i + 1 < args.Length:
            config.MaxWidth = int.TryParse(args[++i], out var maxWidth) ? Math.Clamp(maxWidth, 0, 7680) : config.MaxWidth;
            break;
        case "--capture" when i + 1 < args.Length:
            var capture = args[++i].ToLowerInvariant();
            config.Capture = capture is "auto" or "dxgi" or "gdi" ? capture : "auto";
            break;
        case "--display" when i + 1 < args.Length:
            displayArg = int.TryParse(args[++i], out var display) ? Math.Max(0, display) : 0;
            config.Display = displayArg.Value;
            break;
        case "--auto-accept":
            Console.Error.WriteLine("--auto-accept убран: без пароля подключение требует подтверждения," +
                                    " а для автоматического задайте пароль (--set-password)");
            break;
        case "--reset":
            config.DeviceToken = null;
            config.PublicId = null;
            break;
        case "--help":
        case "-h":
            Console.WriteLine("""
                freedesk-agent — агент удалённого доступа

                  --server <url>       адрес signaling-сервера
                  --name <имя>         имя устройства (по умолчанию имя машины)
                  --connect <id>       подключиться к устройству по 9-значному ID
                  --view <id>          открыть окно просмотра удалённого экрана
                  --input-test <id>    проверить передачу ввода без окна
                  --password <пароль>  пароль устройства для подключения без подтверждения
                  --send-file <путь>   после подключения отправить файл на удалённую машину
                  --download <путь>    после подключения скачать файл по пути на удалённой машине
                  --set-password <п..> сохранить пароль unattended-доступа в профиль и выйти
                  --clear-password     удалить сохранённый пароль профиля и выйти
                  --profile <имя>      отдельный профиль (несколько агентов на машине)
                  --auto-accept        (убран) раньше принимал входящие без подтверждения
                  --reset              забыть токен и зарегистрироваться заново
                  --fps <1..60>        частота кадров (по умолчанию 15)
                  --max-width <px>     ширина кадра после масштабирования (0 — без сжатия)
                  --capture <режим>    захват экрана: auto | dxgi | gdi (по умолчанию auto)
                  --display <номер>    монитор для трансляции (0 — основной)
                  --capture-test [N]   снять N кадров, закодировать и сохранить в %TEMP%
                  --codec <кодек>      видео: h264 (openh264) | mjpeg
                  --quality <10..100>  качество JPEG (по умолчанию 70)
                  --tray               запустить скрыто, только иконка в трее
                  --install-autostart  прописать автозапуск при входе в Windows
                  --remove-autostart   убрать автозапуск
                """);
            return 0;
        default:
            Console.Error.WriteLine($"неизвестный аргумент: {args[i]}");
            return 2;
    }
}

if (newPassword is not null || clearPassword)
{
    if (clearPassword)
    {
        config.ClearUnattendedPassword();
    }
    else if (newPassword!.Length < 6)
    {
        Console.Error.WriteLine("пароль должен быть не короче 6 символов");
        return 2;
    }
    else
    {
        config.SetUnattendedPassword(newPassword);
    }

    config.Save();
    Console.WriteLine(clearPassword
        ? $"пароль устройства удалён (профиль {AgentConfig.Profile})"
        : $"пароль устройства сохранён (профиль {AgentConfig.Profile})");
    return 0;
}

if (installAutostart || removeAutostart)
{
    if (removeAutostart)
    {
        Autostart.Disable();
        Console.WriteLine("автозапуск выключен");
        return 0;
    }

    var ok = Autostart.Enable(AgentConfig.Profile);
    Console.WriteLine(ok
        ? $"автозапуск включён (профиль {AgentConfig.Profile})"
        : "не удалось прописать автозапуск");
    return ok ? 0 : 1;
}

if (captureTestFrames > 0)
{
    await CaptureTest.RunAsync(captureTestFrames, captureQuality, config.MaxWidth, config.Capture, captureCodec, config.Display);
    return 0;
}

// Без openh264.dll H.264 невозможен — лучше явно откатиться на MJPEG, чем
// упасть на первой сессии. Проверяем свою машину: и зритель, и хост идут одним бинарником.
if (VideoCodecNames.Parse(config.Codec) == VideoCodec.H264 && !H264Decoder.IsAvailable())
{
    Log.Warn("openh264.dll не найдена рядом с exe — переключаюсь на mjpeg");
    config.Codec = "mjpeg";
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var client = new SignalingClient(config);

try
{
    if (string.IsNullOrEmpty(config.DeviceToken))
    {
        var registered = await client.RegisterAsync(config.Name, $"windows {Environment.OSVersion.Version}", cts.Token);
        config.DeviceToken = registered.DeviceToken;
        config.PublicId = registered.PublicId;
        config.Save();
        Log.Info($"устройство зарегистрировано: {FormatId(registered.PublicId)}");
    }
    else
    {
        await client.PresenceAsync(cts.Token);
        Log.Info($"устройство уже зарегистрировано: {FormatId(config.PublicId ?? "?")}");
    }
}
catch (Exception ex) when (ex is ApiException or HttpRequestException)
{
    Log.Error($"не удалось выйти на сервер {config.Server}: {ex.Message}");
    return 1;
}

Console.WriteLine();
Console.WriteLine("  FreeDesk agent v" + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"));
Console.WriteLine($"  ID:     {FormatId(config.PublicId ?? "?")}");
Console.WriteLine($"  Имя:    {config.Name}");
Console.WriteLine($"  Сервер: {config.Server}");
Console.WriteLine($"  Приём:  {(config.HasUnattendedPassword ? "по паролю устройства" : "по подтверждению (Enter)")}");
Console.WriteLine();

if (viewTarget is not null)
{
    return RunViewer(client, viewTarget, VideoCodecNames.Parse(config.Codec), viewPassword, displayArg, config, sendFile, downloadPath);
}

if (inputTestTarget is not null)
{
    return RunInputTest(client, inputTestTarget, viewPassword);
}

if (guiMode)
{
    return RunGui(config, client, cts, openSettings, startHidden, sendFile, connectTargets);
}

var runtime = new AgentRuntime(config, client)
{
    AutoSendFile = sendFile,
};
await runtime.RunAsync(connectTo, cts.Token);

Log.Info("остановлено");
return 0;

static string FormatId(string id) =>
    id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

static void TryAttachParentConsole()
{
    // Уже есть консоль (консольная сборка или запуск с редиректом) — не трогаем.
    if (Win32.GetConsoleWindow() != IntPtr.Zero)
    {
        return;
    }

    if (!Win32.AttachConsole(Win32.AttachParentProcess))
    {
        return;
    }

    ReopenStd(-11); // STD_OUTPUT_HANDLE
    ReopenStd(-12); // STD_ERROR_HANDLE

    static void ReopenStd(int handleId)
    {
        // WinExe без консоли: handle может числиться установленным, но быть
        // нерабочим — любой сбой здесь просто оставляет вывод без консоли.
        try
        {
            var handle = Win32.GetStdHandle(handleId);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return;
            }

            var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(handle, ownsHandle: false);
            var stream = new FileStream(safeHandle, FileAccess.Write);
            var writer = new StreamWriter(stream, Console.OutputEncoding) { AutoFlush = true };

            if (handleId == -11)
            {
                Console.SetOut(writer);
            }
            else
            {
                Console.SetError(writer);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"консоль родителя недоступна: {ex.Message}");
        }
    }
}

static string PickFreeProfile() => ProfilePicker.Pick();

static int RunViewer(
    SignalingClient client, string targetId, VideoCodec codec, string? password, int? display,
    AgentConfig config, string? sendFile = null, string? downloadPath = null)
{
    var session = new ViewerSession(client, targetId, codec, password ?? string.Empty, config);
    session.PreferredDisplay = display;

    if (sendFile is not null)
    {
        session.Connected += () => _ = session.SendFileAsync(sendFile);
    }

    if (downloadPath is not null)
    {
        session.Connected += () => session.RequestDownload(downloadPath);
    }
    var exitCode = 0;

    // WinForms требует STA — создаём отдельный поток, чтобы не зависеть от
    // apartment state главного потока консольного приложения.
    var thread = new Thread(() =>
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new ViewerForm(session);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Log.Error($"окно просмотра упало: {ex.Message}");
            exitCode = 1;
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    return exitCode;
}

/// <summary>
/// Проверка ввода без окна: подключается, двигает мышь, кликает и жмёт клавишу.
/// </summary>
static int RunInputTest(SignalingClient client, string targetId, string? password = null)
{
    var session = new ViewerSession(client, targetId, VideoCodec.Mjpeg, password ?? string.Empty);
    var connected = new ManualResetEventSlim();

    session.Connected += () => connected.Set();
    session.StatusChanged += text => Log.Info(text);
    session.Start();

    if (!connected.Wait(TimeSpan.FromSeconds(30)))
    {
        Log.Error("не дождались соединения");
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 1;
    }

    Log.Info("канал есть, отправляю ввод");

    session.SendInput("""{"t":"m","x":0.25,"y":0.25}""");
    Thread.Sleep(600);
    session.SendInput("""{"t":"mb","b":"left","d":true}""");
    Thread.Sleep(120);
    session.SendInput("""{"t":"mb","b":"left","d":false}""");
    Thread.Sleep(400);
    session.SendInput("""{"t":"k","vk":65,"d":true}""");
    Thread.Sleep(120);
    session.SendInput("""{"t":"k","vk":65,"d":false}""");
    Thread.Sleep(1200);

    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    Log.Info("ввод отправлен");
    return 0;
}

static int RunGui(
    AgentConfig config, SignalingClient client, CancellationTokenSource cts,
    bool openSettings = false, bool startHidden = false, string? autoSendFile = null,
    List<string>? autoConnect = null)
{
    var runtime = new AgentRuntime(config, client)
    {
        AutoSendFile = autoSendFile,
    };
    var exitCode = 0;

    var thread = new Thread(() =>
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new MainForm(config, client, runtime, cts, openSettings, startHidden, autoConnect);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Log.Error($"GUI упало: {ex.Message}");
            exitCode = 1;
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    cts.Cancel();
    return exitCode;
}

internal static class Win32
{
    public const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetStdHandle(int standardHandle);
}
