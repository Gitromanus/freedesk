using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FreeDesk.Agent.Codec;

/// <summary>
/// H.264-декодер на OpenH264. Принимает Annex B access unit (как отдаёт энкодер
/// и RTP-депакетизатор), возвращает готовый Bitmap в BGRA.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private const string Dll = "openh264";

    // ISVCDecoder vtable (codec_api.h):
    // 0 Initialize, 1 Uninitialize, 2 DecodeFrame, 3 DecodeFrameNoDelay, 4 DecodeFrame2,
    // 5 FlushFrame, 6 DecodeParser, 7 DecodeFrameEx, 8 SetOption, 9 GetOption
    private const int SlotInitialize = 0;
    private const int SlotUninitialize = 1;
    private const int SlotDecodeNoDelay = 3;

    private const int VideoBitstreamAvc = 0;
    private const int EcEnable = 1;               // ERROR_CON_IDC: включить маскирование потерь
    private const int BufferInfoSize = 72;

    private readonly IntPtr _decoder;
    private readonly IntPtr _bufferInfo;
    private bool _disposed;

    /// <summary>
    /// Проверяет, что кодек доступен: встроенный ресурс или openh264.dll рядом с exe.
    /// Без него H.264 невозможен, и лучше заранее откатиться на MJPEG.
    /// </summary>
    public static bool IsAvailable()
    {
        if (NativeCodec.Ensure() is not null)
        {
            return true;
        }

        try
        {
            NativeLibrary.Load(Dll);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public H264Decoder()
    {
        var hr = WelsCreateDecoder(out _decoder);
        if (hr != 0 || _decoder == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"openh264: WelsCreateDecoder failed ({hr}). Проверь, что openh264.dll лежит рядом с exe.");
        }

        _bufferInfo = Marshal.AllocHGlobal(BufferInfoSize);

        var param = new SDecodingParam
        {
            UiCpuLoad = 0,
            TargetDqLayer = 0xFF,
            EcActiveIdc = EcEnable,
            ParseOnly = 0,
            VideoPropertySize = 12,
            VideoBsType = VideoBitstreamAvc,
        };

        var initialize = GetDelegate<Delegate_Initialize>(SlotInitialize);
        var result = initialize(_decoder, ref param);
        if (result != 0)
        {
            WelsDestroyDecoder(_decoder);
            Marshal.FreeHGlobal(_bufferInfo);
            throw new InvalidOperationException($"openh264: decoder Initialize failed ({result})");
        }
    }

    /// <summary>
    /// Декодирует access unit. Возвращает новый кадр либо null, если кадр ещё не
    /// собрался целиком (многослайсовые кадры) или битстрим битый.
    /// </summary>
    public Bitmap? Decode(ReadOnlySpan<byte> annexB)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (annexB.Length == 0)
        {
            return null;
        }

        for (var offset = 0; offset < BufferInfoSize; offset += 8)
        {
            Marshal.WriteInt64(_bufferInfo, offset, 0);
        }

        int state;
        byte** planes = stackalloc byte*[3];

        fixed (byte* data = annexB)
        {
            var decode = GetDelegate<Delegate_Decode>(SlotDecodeNoDelay);
            state = decode(_decoder, data, annexB.Length, planes, _bufferInfo);
        }

        var status = Marshal.ReadInt32(_bufferInfo);
        if (status != 1)
        {
            return null;
        }

        var width = Marshal.ReadInt32(_bufferInfo, 24);
        var height = Marshal.ReadInt32(_bufferInfo, 28);
        var strideY = Marshal.ReadInt32(_bufferInfo, 36);
        var strideUv = Marshal.ReadInt32(_bufferInfo, 40);

        if (width <= 0 || height <= 0 || state != 0)
        {
            return null;
        }

        var y = Marshal.ReadIntPtr(_bufferInfo, 48);
        var u = Marshal.ReadIntPtr(_bufferInfo, 56);
        var v = Marshal.ReadIntPtr(_bufferInfo, 64);

        if (y == IntPtr.Zero || u == IntPtr.Zero || v == IntPtr.Zero)
        {
            return null;
        }

        var frame = ToBitmap((byte*)y, (byte*)u, (byte*)v, width, height, strideY, strideUv);

        // Кадр принадлежит вызывающему: окно само освобождает предыдущий.
        return frame;
    }

    /// <summary>I420 → Bitmap BGRA (BT.601, как в энкодере).</summary>
    private static Bitmap ToBitmap(
        byte* y, byte* u, byte* v, int width, int height, int strideY, int strideUv)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);

        try
        {
            var dst = (byte*)data.Scan0;

            for (var row = 0; row < height; row++)
            {
                var srcY = y + row * strideY;
                var srcU = u + (row / 2) * strideUv;
                var srcV = v + (row / 2) * strideUv;
                var dstRow = dst + row * data.Stride;

                for (var x = 0; x < width; x++)
                {
                    var c = srcY[x] - 16;
                    var d = srcU[x / 2] - 128;
                    var e = srcV[x / 2] - 128;

                    var luma = 298 * c;
                    var pixel = dstRow + x * 4;

                    pixel[2] = Clamp((luma + 409 * e + 128) >> 8);
                    pixel[1] = Clamp((luma - 100 * d - 208 * e + 128) >> 8);
                    pixel[0] = Clamp((luma + 516 * d + 128) >> 8);
                    pixel[3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    // ─────────── vtable ───────────

    private T GetDelegate<T>(int slot) where T : class
    {
        var vtable = Marshal.ReadIntPtr(_decoder);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_Initialize(IntPtr self, ref SDecodingParam param);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_Uninitialize(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_Decode(IntPtr self, byte* source, int length, byte** destination, IntPtr info);

    /// <summary>SDecodingParam из codec_app_def.h (x64: 32 байта).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct SDecodingParam
    {
        [FieldOffset(0)] public IntPtr RestoredFileName;
        [FieldOffset(8)] public uint UiCpuLoad;
        [FieldOffset(12)] public byte TargetDqLayer;
        [FieldOffset(16)] public int EcActiveIdc;
        [FieldOffset(20)] public byte ParseOnly;
        [FieldOffset(24)] public uint VideoPropertySize;
        [FieldOffset(28)] public int VideoBsType;
    }

    [DllImport(Dll, EntryPoint = "WelsCreateDecoder")]
    private static extern int WelsCreateDecoder(out IntPtr decoder);

    [DllImport(Dll, EntryPoint = "WelsDestroyDecoder")]
    private static extern void WelsDestroyDecoder(IntPtr decoder);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            GetDelegate<Delegate_Uninitialize>(SlotUninitialize)(_decoder);
        }
        catch
        {
            // декодер мог не инициализироваться
        }

        WelsDestroyDecoder(_decoder);
        Marshal.FreeHGlobal(_bufferInfo);
    }
}
