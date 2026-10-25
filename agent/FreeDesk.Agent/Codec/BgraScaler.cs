namespace FreeDesk.Agent.Codec;

/// <summary>Масштабирование кадра BGRA билинейной интерполяцией.</summary>
public static class BgraScaler
{
    /// <summary>Уменьшает/увеличивает кадр в заранее выделенный буфер назначения.</summary>
    public static unsafe ReadOnlySpan<byte> Resize(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        byte[] destination,
        int width,
        int height)
    {
        if (destination.Length < width * height * 4)
        {
            throw new ArgumentException("буфер назначения меньше кадра", nameof(destination));
        }

        // 16.16 с фиксированной точкой: смещение = (x + 0.5) * scale - 0.5
        var scaleX = ((sourceWidth - 1) << 16) / Math.Max(1, width - 1);
        var scaleY = ((sourceHeight - 1) << 16) / Math.Max(1, height - 1);

        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            for (var y = 0; y < height; y++)
            {
                var sy = y * scaleY;
                var y0 = sy >> 16;
                var y1 = Math.Min(y0 + 1, sourceHeight - 1);
                var fy = sy & 0xFFFF;

                var row0 = src + y0 * sourceStride;
                var row1 = src + y1 * sourceStride;
                var outRow = dst + y * width * 4;

                for (var x = 0; x < width; x++)
                {
                    var sx = x * scaleX;
                    var x0 = sx >> 16;
                    var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                    var fx = sx & 0xFFFF;

                    var p00 = row0 + x0 * 4;
                    var p01 = row0 + x1 * 4;
                    var p10 = row1 + x0 * 4;
                    var p11 = row1 + x1 * 4;

                    var outPixel = outRow + x * 4;

                    for (var c = 0; c < 3; c++)
                    {
                        var top = (long)p00[c] * (65536 - fx) + (long)p01[c] * fx;
                        var bottom = (long)p10[c] * (65536 - fx) + (long)p11[c] * fx;
                        var value = (int)((top * (65536 - fy) + bottom * fy) >> 32);
                        outPixel[c] = (byte)Math.Min(255, value);
                    }

                    outPixel[3] = 255;
                }
            }
        }

        return destination.AsSpan(0, width * height * 4);
    }
}
