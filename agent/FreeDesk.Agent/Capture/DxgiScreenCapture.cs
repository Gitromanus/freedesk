using System;
using System.Runtime.InteropServices;

namespace FreeDesk.Agent.Capture;

/// <summary>
/// Захват через DXGI Desktop Duplication: кадры уже скомпозированы DWM,
/// поэтому нет разрывов (tearing) и мерцания, как при BitBlt.
/// </summary>
public sealed class DxgiScreenCapture : IScreenCapture, IDisposable
{
    private const int S_OK = 0;
    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _duplication;
    private IntPtr _staging;
    private int _width;
    private int _height;
    private int _stride;
    private byte[] _buffer = Array.Empty<byte>();
    private bool _hasFrame;
    private int _blankFrames;
    private bool _disposed;

    // курсор: DXGI не отдаёт указатель с кадром — рисуем сами, храня патч фона
    private byte[] _cursorPatch = Array.Empty<byte>();
    private int _cursorX = -1;
    private int _cursorY = -1;

    public int Width => _width;
    public int Height => _height;

    public DxgiScreenCapture()
    {
        Init();
    }

    private void Init()
    {
        // Create DXGI factory
        var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        int hr = CreateDXGIFactory1(ref factoryGuid, out var factory);
        if (hr != S_OK) throw new InvalidOperationException($"CreateDXGIFactory1 failed: 0x{hr:X8}");

        // Enumerate adapters to find one with outputs
        IntPtr adapter = IntPtr.Zero;
        IntPtr output = IntPtr.Zero;

        for (uint ai = 0; ; ai++)
        {
            hr = EnumAdapters1(factory, ai, out var ad);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            if (hr != S_OK) continue;

            // Try to get output 0
            var ehr = EnumOutputs(ad, 0, out output);
            if (ehr == S_OK && output != IntPtr.Zero)
            {
                adapter = ad;
                break;
            }

            Marshal.Release(ad);
        }

        if (adapter == IntPtr.Zero || output == IntPtr.Zero)
        {
            Marshal.Release(factory);
            throw new InvalidOperationException("No adapter with outputs found");
        }

        // Create D3D11 device on this adapter
        var featureLevels = new uint[] { 0xB000 };
        uint deviceFlags = 0; // D3D11_CREATE_DEVICE_BGRA_SUPPORT

        hr = D3D11CreateDevice(
            adapter, 0, IntPtr.Zero, deviceFlags,
            featureLevels, (uint)featureLevels.Length,
            7, out _device, out _, out _context);

        if (hr != S_OK || _device == IntPtr.Zero)
        {
            Marshal.Release(output);
            Marshal.Release(adapter);
            Marshal.Release(factory);
            throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr:X8}");
        }

        // QueryInterface output to IDXGIOutput1
        var output1Guid = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        var output1 = QueryInterface(output, output1Guid);

        // DuplicateOutput
        hr = DuplicateOutput(output1, _device, out _duplication);
        if (hr != S_OK)
        {
            Marshal.Release(output1);
            Marshal.Release(output);
            Marshal.Release(adapter);
            Marshal.Release(factory);
            throw new InvalidOperationException($"DuplicateOutput failed: 0x{hr:X8}");
        }

        // Get desc
        var desc = GetDuplDesc(_duplication);
        _width = (int)desc.Width;
        _height = (int)desc.Height;
        _stride = _width * 4;
        _buffer = new byte[_stride * _height];

        Marshal.Release(output1);
        Marshal.Release(output);
        Marshal.Release(adapter);
        Marshal.Release(factory);

        // Первый кадр сеем через GDI: на неподвижном рабочем столе DXGI не отдаёт
        // кадра до первого present, а зритель должен сразу увидеть рабочий стол.
        SeedFirstFrame();
    }

    private void SeedFirstFrame()
    {
        try
        {
            using var gdi = new GdiScreenCapture();
            var frame = gdi.CaptureFrame(out var stride);
            if (stride == _stride && frame.Length == _buffer.Length)
            {
                frame.CopyTo(_buffer);
                _hasFrame = true;
            }
        }
        catch
        {
            // Не критично: первый кадр придёт с первым present.
        }
    }

    public ReadOnlySpan<byte> CaptureFrame(out int stride)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DxgiScreenCapture));

        stride = _stride;
        var hr = -1; // не S_OK: если цикл не выполнился, считаем кадра нет
        IntPtr resource = IntPtr.Zero;

        var timeout = _hasFrame ? 16u : 100u;
        var maxAttempts = _hasFrame ? 1 : 10;
        var reinits = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            hr = AcquireNextFrame(_duplication, timeout, out _, out resource);
            if (hr == S_OK) break;
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue;

            // DXGI_ERROR_ACCESS_LOST и подобные: смена режима, полноэкранное
            // приложение, блокировка сессии. Дубликацию надо пересоздать.
            if (resource != IntPtr.Zero)
            {
                Marshal.Release(resource);
                resource = IntPtr.Zero;
            }

            if (++reinits > 3)
            {
                throw new InvalidOperationException($"AcquireNextFrame: 0x{hr:X8}");
            }

            Reinit();
            attempt = -1; // после пересоздания снова ждём первый кадр
            timeout = 100u;
            maxAttempts = 10;
        }

        if (hr != S_OK)
        {
            // Нет нового кадра (статичный экран) — возвращаем предыдущий
            // (с нарисованным курсором, если он двигался).
            DrawCursor(redraw: false);
            return _buffer;
        }

        try
        {
            if (_staging == IntPtr.Zero)
            {
                var texDesc = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)_width,
                    Height = (uint)_height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
                    SampleDesc_Count = 1,
                    Usage = 3, // D3D11_USAGE_STAGING
                    CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
                };
                hr = CreateTexture2D(_device, ref texDesc, IntPtr.Zero, out _staging);
                if (hr != S_OK) throw new InvalidOperationException($"CreateTexture2D failed: 0x{hr:X8}");
            }

            CopyResource(_context, _staging, resource);

            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            hr = Map(_context, _staging, 0, 1, 0, ref mapped);
            if (hr != S_OK) throw new InvalidOperationException($"Map failed: 0x{hr:X8}");

            for (var y = 0; y < _height; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.pData, y * (int)mapped.RowPitch), _buffer, y * _stride, _stride);
            }

            Unmap(_context, _staging, 0);
            _hasFrame = true;

            // Дубликация иногда «работает», но отдаёт пустые чёрные текстуры —
            // так ведёт себя RDP-сессия. Два пустых кадра подряд считаем поломкой
            // и бросаем исключение: стример откатится на GDI.
            if (IsBlankFrame())
            {
                if (++_blankFrames >= 2)
                {
                    throw new InvalidOperationException("dxgi отдаёт пустые (чёрные) кадры");
                }
            }
            else
            {
                _blankFrames = 0;
            }
        }
        finally
        {
            ReleaseFrame(_duplication);
            if (resource != IntPtr.Zero) Marshal.Release(resource);
        }

        // новый кадр перезаписал буфер — патч под курсором устарел, рисуем заново
        _cursorX = -1;
        _cursorY = -1;
        DrawCursor(redraw: true);

        stride = _stride;
        return _buffer;
    }

    // ─────────── курсор ───────────

    // классическая стрелка: '#' — чёрная обводка, 'o' — белая заливка
    private static readonly string[] CursorSprite =
    {
        "#...........",
        "##..........",
        "#o#.........",
        "#oo#........",
        "#ooo#.......",
        "#oooo#......",
        "#ooooo#.....",
        "#oooooo#....",
        "#ooooooo#...",
        "#oooooooo#..",
        "#ooooooooo#.",
        "#oooooooooo#",
        "#oooo#######",
        "#oo#o#......",
        "#o#.#o#.....",
        "##..#o#.....",
        "#...#o#.....",
        "....##......",
    };

    /// <summary>
    /// Рисует стрелку в буфер по текущей позиции указателя. Фон под прежней
    /// позицией восстанавливается из патча, поэтому курсор можно двигать и по
    /// статичному экрану — отпечаток кадра изменится и зритель его увидит.
    /// </summary>
    private void DrawCursor(bool redraw)
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var spriteHeight = CursorSprite.Length;
        var spriteWidth = CursorSprite[0].Length;

        if (!redraw && point.X == _cursorX && point.Y == _cursorY)
        {
            return; // ничего не изменилось, курсор уже нарисован
        }

        // вернуть фон под старой стрелкой
        if (_cursorX >= 0 && _cursorPatch.Length == spriteWidth * spriteHeight * 4)
        {
            Blit(_cursorPatch, _cursorX, _cursorY, spriteWidth, spriteHeight, copyBack: true);
        }

        _cursorX = point.X;
        _cursorY = point.Y;

        // сохранить фон под новой и нарисовать стрелку
        _cursorPatch = new byte[spriteWidth * spriteHeight * 4];
        Blit(_cursorPatch, point.X, point.Y, spriteWidth, spriteHeight, copyBack: false);

        for (var y = 0; y < spriteHeight; y++)
        {
            for (var x = 0; x < spriteWidth; x++)
            {
                var cell = CursorSprite[y][x];
                if (cell == '.')
                {
                    continue;
                }

                var px = point.X + x;
                var py = point.Y + y;
                if (px < 0 || px >= _width || py < 0 || py >= _height)
                {
                    continue;
                }

                var offset = (py * _stride) + (px * 4);
                var value = cell == '#' ? (byte)0 : (byte)255;
                _buffer[offset] = value;     // B
                _buffer[offset + 1] = value; // G
                _buffer[offset + 2] = value; // R
                _buffer[offset + 3] = 255;   // A
            }
        }
    }

    /// <summary>Копирует прямоугольник между _buffer и патчем (с обрезкой по краям).</summary>
    private void Blit(byte[] patch, int x, int y, int width, int height, bool copyBack)
    {
        for (var row = 0; row < height; row++)
        {
            var py = y + row;
            if (py < 0 || py >= _height)
            {
                continue;
            }

            var span = Math.Min(width, _width - Math.Max(0, x));
            if (span <= 0)
            {
                continue;
            }

            var patchOffset = row * width * 4 + Math.Max(0, -x) * 4;
            var bufferOffset = (py * _stride) + Math.Max(0, x) * 4;

            if (copyBack)
            {
                patch.AsSpan(patchOffset, span * 4).CopyTo(_buffer.AsSpan(bufferOffset, span * 4));
            }
            else
            {
                _buffer.AsSpan(bufferOffset, span * 4).CopyTo(patch.AsSpan(patchOffset, span * 4));
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Выборочная проверка: пустая ли текстура (все сэмплы — нули).</summary>
    private bool IsBlankFrame()
    {
        for (var i = 0; i < _buffer.Length; i += 256)
        {
            if (_buffer[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void Reinit()
    {
        // Не через Dispose(): он выставляет _disposed, после чего захват навсегда
        // считался бы закрытым, а новые COM-объекты из Init утекли бы.
        ReleaseResources();
        Init();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
        if (_duplication != IntPtr.Zero) { Marshal.Release(_duplication); _duplication = IntPtr.Zero; }
        if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
        _hasFrame = false;
    }

    // ---- COM helpers ----

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        var hr = Marshal.QueryInterface(obj, in iid, out var ptr);
        if (hr != S_OK) throw new InvalidOperationException("QueryInterface failed");
        return ptr;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    private static int EnumAdapters1(IntPtr factory, uint adapter, out IntPtr ppAdapter)
    {
        var vtable = Marshal.ReadIntPtr(factory);
        var method = Marshal.ReadIntPtr(vtable, 12 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_EnumAdapters1>(method);
        return del(factory, adapter, out ppAdapter);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_EnumAdapters1(IntPtr self, uint adapter, out IntPtr ppAdapter);

    private static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        var vtable = Marshal.ReadIntPtr(adapter);
        var method = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_EnumOutputs>(method);
        return del(adapter, index, out output);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_EnumOutputs(IntPtr self, uint index, out IntPtr output);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
        uint[] pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, out uint pFeatureLevel, out IntPtr ppImmediateContext);

    private static int DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication)
    {
        var vtable = Marshal.ReadIntPtr(output1);
        var method = Marshal.ReadIntPtr(vtable, 22 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_DuplicateOutput>(method);
        return del(output1, device, out duplication);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_DuplicateOutput(IntPtr self, IntPtr device, out IntPtr duplication);

    private static DXGI_OUTDUPL_DESC GetDuplDesc(IntPtr dupl)
    {
        var desc = new DXGI_OUTDUPL_DESC();
        var vtable = Marshal.ReadIntPtr(dupl);
        var method = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_GetDesc>(method);
        del(dupl, out desc);
        return desc;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Delegate_GetDesc(IntPtr self, out DXGI_OUTDUPL_DESC desc);

    private static int AcquireNextFrame(IntPtr dupl, uint timeout, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource)
    {
        info = default;
        var vtable = Marshal.ReadIntPtr(dupl);
        var method = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_AcquireNextFrame>(method);
        return del(dupl, timeout, out info, out resource);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_AcquireNextFrame(IntPtr self, uint timeout, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IntPtr desktopResource);

    private static void ReleaseFrame(IntPtr dupl)
    {
        var vtable = Marshal.ReadIntPtr(dupl);
        var method = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_ReleaseFrame>(method);
        del(dupl);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_ReleaseFrame(IntPtr self);

    private static int CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, IntPtr initData, out IntPtr texture)
    {
        var vtable = Marshal.ReadIntPtr(device);
        var method = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_CreateTexture2D>(method);
        return del(device, ref desc, initData, out texture);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_CreateTexture2D(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);

    private static void CopyResource(IntPtr context, IntPtr dst, IntPtr src)
    {
        var vtable = Marshal.ReadIntPtr(context);
        var method = Marshal.ReadIntPtr(vtable, 47 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_CopyResource>(method);
        del(context, dst, src);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Delegate_CopyResource(IntPtr self, IntPtr dst, IntPtr src);

    private static int Map(IntPtr context, IntPtr resource, uint subresource, int mapType, uint mapFlags, ref D3D11_MAPPED_SUBRESOURCE mapped)
    {
        var vtable = Marshal.ReadIntPtr(context);
        var method = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_Map>(method);
        return del(context, resource, subresource, mapType, mapFlags, ref mapped);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_Map(IntPtr self, IntPtr resource, uint subresource, int mapType, uint mapFlags, ref D3D11_MAPPED_SUBRESOURCE mapped);

    private static void Unmap(IntPtr context, IntPtr resource, uint subresource)
    {
        var vtable = Marshal.ReadIntPtr(context);
        var method = Marshal.ReadIntPtr(vtable, 15 * IntPtr.Size);
        var del = Marshal.GetDelegateForFunctionPointer<Delegate_Unmap>(method);
        del(context, resource, subresource);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Delegate_Unmap(IntPtr self, IntPtr resource, uint subresource);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_DESC
    {
        public uint Width;
        public uint Height;
        public DXGI_MODE_DESC ModeDesc;
        public uint DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_MODE_DESC
    {
        public uint Width;
        public uint Height;
        public DXGI_RATIONAL RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerPositionX;
        public int PointerPositionY;
        public int PointerPositionVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleDesc_Count;
        public uint SampleDesc_Quality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
