namespace FreeDesk.Agent;

/// <summary>Кодек видео в сессии: JPEG по RTP, H.264 (OpenH264) или JPEG по data channel для браузера.</summary>
public enum VideoCodec
{
    Mjpeg,
    H264,

    /// <summary>Кадры JPEG идут бинарём по data channel — так работает браузерный клиент.</summary>
    JpgData,
}

public static class VideoCodecNames
{
    public static VideoCodec Parse(string? value)
    {
        if (string.Equals(value, "h264", StringComparison.OrdinalIgnoreCase)) return VideoCodec.H264;
        if (string.Equals(value, "jpgdata", StringComparison.OrdinalIgnoreCase)) return VideoCodec.JpgData;
        return VideoCodec.Mjpeg;
    }

    public static string ToWire(this VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264",
        VideoCodec.JpgData => "jpgdata",
        _ => "mjpeg",
    };
}
