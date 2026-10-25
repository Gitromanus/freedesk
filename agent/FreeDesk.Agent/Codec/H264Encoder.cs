using System.Runtime.InteropServices;

namespace FreeDesk.Agent.Codec;

/// <summary>
/// H.264-энкодер на OpenH264 (Cisco, тот же кодек, что в WebRTC).
/// Требует openh264.dll рядом с exe. Вход — BGRA (как из захвата), выход — Annex B
/// access unit: SPS/PPS перед ключевым кадром, дальше слайсы.
/// Режим SCREEN_CONTENT_REAL_TIME — спец-оптимизация OpenH264 под удалённый рабочий стол.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private const string Dll = "openh264";

    // ISVCEncoder vtable (codec_api.h):
    // 0 Initialize, 1 InitializeExt, 2 GetDefaultParams, 3 Uninitialize,
    // 4 EncodeFrame, 5 EncodeParameterSets, 6 ForceIntraFrame, 7 SetOption, 8 GetOption
    private const int SlotInitialize = 0;
    private const int SlotUninitialize = 3;
    private const int SlotEncodeFrame = 4;
    private const int SlotForceIntra = 6;
    private const int SlotSetOption = 7;

    private const int UsageScreenContentRealTime = 1; // EUsageType
    private const int RcBitrateMode = 1;              // RC_MODES
    private const int VideoFormatI420 = 23;           // EVideoFormatType
    private const int FrameTypeIdr = 1;               // EVideoFrameType
    private const int FrameTypeSkip = 4;              // EVideoFrameType
    private const int EncoderOptionBitrate = 5;       // ENCODER_OPTION
    private const int SpatialLayerAll = 4;            // SPATIAL_LAYER_ALL

    /// <summary>MAX_LAYER_NUM_OF_FRAME из codec_app_def.h.</summary>
    private const int MaxLayers = 128;

    private readonly IntPtr _encoder;
    private readonly IntPtr _bsInfo;
    private readonly IntPtr _picture;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _i420;
    private readonly byte[] _scaled;
    private const int MaxFrameBytes = 32 * 1024 * 1024;
    private long _frames;
    private long _skipped;
    private long _lastBytes;
    private bool _disposed;

    private static readonly int LayerOffset = IntPtr.Size;               // int iLayerNum + выравнивание
    private static readonly int LayerSize = Marshal.SizeOf<SLayerBSInfo>();
    private static readonly int FrameTypeOffset = LayerOffset + MaxLayers * LayerSize;
    private static readonly int FrameSizeOffset = FrameTypeOffset + sizeof(int);
    private static readonly int BsInfoSize = Math.Max(FrameSizeOffset + sizeof(int) + sizeof(long), 64 * 1024);

    public int Width => _width;
    public int Height => _height;

    /// <summary>Сколько кадров закодировано и сколько энкодер пропустил как статичные.</summary>
    public long Frames => _frames;
    public long Skipped => _skipped;

    /// <summary>Размер последнего кадра в байтах.</summary>
    public long LastFrameBytes => _lastBytes;

    /// <param name="width">Ширина входного кадра (обрезается до чётной).</param>
    /// <param name="height">Высота входного кадра (обрезается до чётной).</param>
    /// <param name="fps">Максимальная частота кадров.</param>
    /// <param name="bitrateKbps">Целевой битрейт, кбит/с.</param>
    public H264Encoder(int width, int height, int fps, int bitrateKbps)
    {
        _width = width & ~1;
        _height = height & ~1;

        if (_width < 16 || _height < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"openh264: слишком маленький кадр {width}x{height}");
        }

        _i420 = new byte[_width * _height * 3 / 2];
        _scaled = new byte[_width * _height * 4];

        var hr = WelsCreateSVCEncoder(out _encoder);
        if (hr != 0 || _encoder == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"openh264: WelsCreateSVCEncoder failed ({hr}). Проверь, что openh264.dll лежит рядом с exe.");
        }

        _bsInfo = Marshal.AllocHGlobal(BsInfoSize);
        _picture = Marshal.AllocHGlobal(Marshal.SizeOf<SSourcePicture>());
        ZeroBsInfo();

        var param = new SEncParamBase
        {
            UsageType = UsageScreenContentRealTime,
            Width = _width,
            Height = _height,
            TargetBitrate = Math.Max(100, bitrateKbps) * 1000,
            RcMode = RcBitrateMode,
            MaxFrameRate = fps,
        };

        Check(GetDelegate<Delegate_ParamBase>(SlotInitialize)(_encoder, ref param), "Initialize");
        Log.Info($"h264: энкодер {_width}x{_height}, {fps} fps, {bitrateKbps} кбит/с");
    }

    /// <summary>
    /// Кодирует кадр BGRA. Возвращает Annex B access unit либо null, если энкодер
    /// счёл кадр статичным (skip) — такой кадр отправлять не нужно.
    /// </summary>
    public byte[]? Encode(ReadOnlySpan<byte> bgra, int stride, long timestampMs, out bool keyframe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        keyframe = false;

        ConvertBgraToI420(bgra, stride);

        var picture = new SSourcePicture
        {
            ColorFormat = VideoFormatI420,
            Stride0 = _width,
            Stride1 = _width / 2,
            Stride2 = _width / 2,
            Width = _width,
            Height = _height,
            TimeStamp = timestampMs,
        };

        fixed (byte* i420 = _i420)
        {
            picture.Data0 = (nint)i420;
            picture.Data1 = (nint)(i420 + _width * _height);
            picture.Data2 = (nint)(i420 + _width * _height * 5 / 4);

            Marshal.StructureToPtr(picture, _picture, false);

            var hr = GetDelegate<Delegate_Encode>(SlotEncodeFrame)(_encoder, _picture, _bsInfo);
            if (hr != 0)
            {
                throw new InvalidOperationException($"openh264: EncodeFrame failed ({hr})");
            }
        }

        var layerCount = Marshal.ReadInt32(_bsInfo);
        var frameType = Marshal.ReadInt32(_bsInfo, FrameTypeOffset);
        var frameSize = Marshal.ReadInt32(_bsInfo, FrameSizeOffset);

        if (frameType == FrameTypeSkip || frameSize <= 0 || frameSize > MaxFrameBytes)
        {
            _skipped++;
            return null;
        }

        // OpenH264 держит битстрим кадра одним непрерывным буфером, а в sLayerInfo
        // раскладывает его по слоям (в 2.5.0 наборы параметров — отдельный слой).
        // Берём самый младший адрес и читаем кадр целиком.
        var start = IntPtr.Zero;

        for (var i = 0; i < layerCount && i < MaxLayers; i++)
        {
            var layer = Marshal.PtrToStructure<SLayerBSInfo>(_bsInfo + LayerOffset + i * LayerSize);

            if (layer.BsBuf == IntPtr.Zero)
            {
                continue;
            }

            if (start == IntPtr.Zero || layer.BsBuf.ToInt64() < start.ToInt64())
            {
                start = layer.BsBuf;
            }
        }

        if (start == IntPtr.Zero)
        {
            return null;
        }

        var output = new byte[frameSize];
        Marshal.Copy(start, output, 0, frameSize);

        _frames++;
        _lastBytes = output.Length;
        keyframe = frameType == FrameTypeIdr || ContainsIdr(output);

        return output;
    }

    private void ZeroBsInfo()
    {
        var zeros = new byte[4096];

        for (var offset = 0; offset < BsInfoSize; offset += zeros.Length)
        {
            Marshal.Copy(zeros, 0, _bsInfo + offset, Math.Min(zeros.Length, BsInfoSize - offset));
        }
    }

    /// <summary>Форсирует IDR-кадр (нужен зрителю после потери пакетов).</summary>
    public void ForceKeyframe() =>
        GetDelegate<Delegate_ForceIntra>(SlotForceIntra)(_encoder, 1, -1);

    /// <summary>
    /// Меняет целевой битрейт на лету (адаптация под канал).
    /// OpenH264 принимает SBitrateInfo с iLayer = SPATIAL_LAYER_ALL.
    /// </summary>
    public void SetBitrate(int kbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var info = new SBitrateInfo { Layer = SpatialLayerAll, Bitrate = Math.Max(100, kbps) * 1000 };
        var hr = GetDelegate<Delegate_SetOption>(SlotSetOption)(_encoder, EncoderOptionBitrate, ref info);

        if (hr != 0)
        {
            throw new InvalidOperationException($"openh264: SetOption(BITRATE) failed ({hr})");
        }
    }

    /// <summary>Есть ли в access unit IDR-слайс (nal_unit_type 5).</summary>
    private static bool ContainsIdr(ReadOnlySpan<byte> annexB)
    {
        for (var i = 0; i + 4 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0 || annexB[i + 2] != 1)
            {
                continue;
            }

            if ((annexB[i + 3] & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }

    // ─────────── BGRA → I420 (BT.601, полный диапазон) ───────────

    private void ConvertBgraToI420(ReadOnlySpan<byte> bgra, int stride)
    {
        fixed (byte* srcPtr = bgra)
        fixed (byte* i420 = _i420)
        {
            var yPlane = i420;
            var uPlane = i420 + _width * _height;
            var vPlane = uPlane + _width * _height / 4;

            for (var y = 0; y < _height; y++)
            {
                var src = srcPtr + y * stride;
                var dst = yPlane + y * _width;

                for (var x = 0; x < _width; x++)
                {
                    dst[x] = (byte)((src[x * 4 + 2] * 77 + src[x * 4 + 1] * 150 + src[x * 4] * 29) >> 8);
                }
            }

            for (var y = 0; y < _height; y += 2)
            {
                var uRow = uPlane + (y / 2) * (_width / 2);
                var vRow = vPlane + (y / 2) * (_width / 2);

                for (var x = 0; x < _width; x += 2)
                {
                    int b = 0, g = 0, r = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        var s = srcPtr + (y + dy) * stride + x * 4;
                        b += s[0] + s[4];
                        g += s[1] + s[5];
                        r += s[2] + s[6];
                    }

                    b >>= 2;
                    g >>= 2;
                    r >>= 2;

                    uRow[x / 2] = (byte)(((-43 * r - 85 * g + 128 * b) >> 8) + 128);
                    vRow[x / 2] = (byte)(((128 * r - 107 * g - 21 * b) >> 8) + 128);
                }
            }
        }
    }

    /// <summary>Масштабирует кадр BGRA под размер энкодера (если он отличается).</summary>
    public ReadOnlySpan<byte> Fit(ReadOnlySpan<byte> bgra, int sourceWidth, int sourceHeight, int stride)
    {
        if (sourceWidth == _width && sourceHeight == _height)
        {
            return bgra;
        }

        return BgraScaler.Resize(bgra, sourceWidth, sourceHeight, stride, _scaled, _width, _height);
    }

    // ─────────── vtable ───────────

    private T GetDelegate<T>(int slot) where T : class =>
        Marshal.GetDelegateForFunctionPointer<T>(VtableSlot(_encoder, slot));

    private static IntPtr VtableSlot(IntPtr obj, int slot)
    {
        var vtable = Marshal.ReadIntPtr(obj);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_ParamBase(IntPtr self, ref SEncParamBase param);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_VoidRet(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_Encode(IntPtr self, IntPtr source, IntPtr bsInfo);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_ForceIntra(IntPtr self, int idr, int layerId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Delegate_SetOption(IntPtr self, int option, ref SBitrateInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct SBitrateInfo
    {
        public int Layer;
        public int Bitrate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SEncParamBase
    {
        public int UsageType;
        public int Width;
        public int Height;
        public int TargetBitrate;
        public int RcMode;
        public float MaxFrameRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SSourcePicture
    {
        public int ColorFormat;
        public int Stride0;
        public int Stride1;
        public int Stride2;
        public int Stride3;
        public nint Data0;
        public nint Data1;
        public nint Data2;
        public nint Data3;
        public int Width;
        public int Height;
        public long TimeStamp;
        public byte PsnrY;
        public byte PsnrU;
        public byte PsnrV;
    }

    /// <summary>SLayerBSInfo из codec_app_def.h (x64, версия 2.5.0: 40 байт, без PSNR).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct SLayerBSInfo
    {
        [FieldOffset(0)] public byte TemporalId;
        [FieldOffset(1)] public byte SpatialId;
        [FieldOffset(2)] public byte QualityId;
        [FieldOffset(4)] public int FrameType;
        [FieldOffset(8)] public byte LayerType;
        [FieldOffset(12)] public int SubSeqId;
        [FieldOffset(16)] public int NalCount;
        [FieldOffset(24)] public IntPtr NalLengths;
        [FieldOffset(32)] public IntPtr BsBuf;
    }

    // ─────────── P/Invoke ───────────

    [DllImport(Dll, EntryPoint = "WelsCreateSVCEncoder")]
    private static extern int WelsCreateSVCEncoder(out IntPtr encoder);

    [DllImport(Dll, EntryPoint = "WelsDestroySVCEncoder")]
    private static extern void WelsDestroySVCEncoder(IntPtr encoder);

    private static void Check(int hr, string what)
    {
        if (hr != 0)
        {
            throw new InvalidOperationException($"openh264: {what} failed ({hr})");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            GetDelegate<Delegate_VoidRet>(SlotUninitialize)(_encoder);
        }
        catch
        {
            // энкодер мог уже не инициализироваться
        }

        WelsDestroySVCEncoder(_encoder);
        Marshal.FreeHGlobal(_bsInfo);
        Marshal.FreeHGlobal(_picture);
    }
}
