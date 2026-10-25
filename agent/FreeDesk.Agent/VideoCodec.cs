namespace FreeDesk.Agent;

/// <summary>Кодек видео в сессии: JPEG по RTP (MVP) или H.264 (OpenH264).</summary>
public enum VideoCodec
{
    Mjpeg,
    H264,
}

public static class VideoCodecNames
{
    public static VideoCodec Parse(string? value) =>
        string.Equals(value, "h264", StringComparison.OrdinalIgnoreCase) ? VideoCodec.H264 : VideoCodec.Mjpeg;

    public static string ToWire(this VideoCodec codec) => codec == VideoCodec.H264 ? "h264" : "mjpeg";
}
