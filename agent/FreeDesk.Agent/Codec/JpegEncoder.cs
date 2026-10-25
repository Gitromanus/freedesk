using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FreeDesk.Agent.Codec;

/// <summary>JPEG-кодирование кадра BGRA. Быстрый путь для MVP; H.264 — следующий шаг.</summary>
public static class JpegEncoder
{
    /// <param name="maxWidth">Если больше нуля и кадр шире — уменьшить пропорционально.</param>
    public static unsafe byte[] Encode(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        long quality,
        int maxWidth = 0)
    {
        using var buffer = new MemoryStream(width * height / 4);

        fixed (byte* pointer = bgra)
        {
            using var source = new Bitmap(width, height, stride, PixelFormat.Format32bppRgb, (IntPtr)pointer);

            if (maxWidth > 0 && width > maxWidth)
            {
                var targetWidth = maxWidth;
                var targetHeight = (int)Math.Round(height * (double)maxWidth / width);

                using var scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppRgb);

                using (var graphics = Graphics.FromImage(scaled))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.Bilinear;
                    graphics.PixelOffsetMode = PixelOffsetMode.Half;
                    graphics.SmoothingMode = SmoothingMode.None;
                    graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));
                }

                Save(scaled, buffer, quality);
            }
            else
            {
                Save(source, buffer, quality);
            }
        }

        return buffer.ToArray();
    }

    private static void Save(Bitmap bitmap, MemoryStream buffer, long quality)
    {
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(buffer, GetJpegCodec(), parameters);
    }

    private static ImageCodecInfo GetJpegCodec()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                return codec;
            }
        }

        throw new InvalidOperationException("JPEG-кодек не найден");
    }
}
