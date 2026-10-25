using System.Runtime.InteropServices;

namespace FreeDesk.Agent.Capture;

/// <summary>
/// Захват через GDI BitBlt. Работает везде и без зависимостей.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint DibRgbColors = 0;

    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previousObject;
    private readonly byte[] _buffer;
    private readonly int _stride;
    private BitmapInfo _bitmapInfo;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }

    public GdiScreenCapture()
    {
        Width = GetSystemMetrics(SmCxScreen);
        Height = GetSystemMetrics(SmCyScreen);

        if (Width <= 0 || Height <= 0)
        {
            throw new InvalidOperationException("не удалось определить размер экрана");
        }

        _stride = Width * 4;
        _buffer = new byte[_stride * Height];

        _screenDc = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(_screenDc);
        _bitmap = CreateCompatibleBitmap(_screenDc, Width, Height);
        _previousObject = SelectObject(_memoryDc, _bitmap);

        _bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = Width,
                Height = -Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            },
        };
    }

    public ReadOnlySpan<byte> CaptureFrame(out int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var desktopHwnd = GetDesktopWindow();

        // Пробуем PrintWindow с PW_RENDERFULLCONTENT — захватывает скомпозированный
        // DWM-вывод без разрывов (в отличие от BitBlt).
        if (!PrintWindow(desktopHwnd, _memoryDc, 0x00000002)) // PW_RENDERFULLCONTENT
        {
            if (!BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, 0, 0, SrcCopy | CaptureBlt))
            {
                throw new InvalidOperationException("BitBlt завершился ошибкой");
            }
        }

        DrawCursor();

        if (GetDIBits(_memoryDc, _bitmap, 0, (uint)Height, _buffer, ref _bitmapInfo, DibRgbColors) == 0)
        {
            throw new InvalidOperationException("GetDIBits завершился ошибкой");
        }

        stride = _stride;
        return _buffer;
    }

    private const uint DiNormal = 0x0003;

    /// <summary>
    /// BitBlt/PrintWindow не захватывают указатель мыши — рисуем его сами,
    /// иначе зритель не видит, куда тыкается на удалённой машине.
    /// </summary>
    private void DrawCursor()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref info) == 0 || (info.Flags & CursorShowing) == 0)
        {
            return;
        }

        // координаты экранные; наш memory DC снят с (0,0) — совпадают напрямую
        DrawIconEx(_memoryDc, info.PositionX, info.PositionY, info.CursorHandle, 0, 0, 0, IntPtr.Zero, DiNormal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr CursorHandle;
        public int PositionX;
        public int PositionY;
    }

    private const uint CursorShowing = 0x0001;

    [DllImport("user32.dll")]
    private static extern int GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SelectObject(_memoryDc, _previousObject);
        DeleteObject(_bitmap);
        DeleteDC(_memoryDc);
        ReleaseDC(IntPtr.Zero, _screenDc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hDc, IntPtr bitmap, uint start, uint lines,
        byte[] bits, ref BitmapInfo info, uint usage);
}
