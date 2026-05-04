using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OcrTradingBackend.Services;

public interface IOcrImageHasher
{
    string ComputeFullHash(Bitmap bitmap);
    string ComputeSampleHash(Bitmap bitmap, int step);
}

public sealed class OcrImageHasher : IOcrImageHasher
{
    public string ComputeFullHash(Bitmap bitmap)
    {
        using var normalized = CreateNormalizedBitmap(bitmap);

        var rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
        var data = normalized.LockBits(
            rect,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var byteCount = Math.Abs(data.Stride) * data.Height;
            var bytes = new byte[byteCount];

            Marshal.Copy(data.Scan0, bytes, 0, byteCount);

            using var sha = SHA256.Create();

            var widthBytes = BitConverter.GetBytes(normalized.Width);
            var heightBytes = BitConverter.GetBytes(normalized.Height);
            var strideBytes = BitConverter.GetBytes(data.Stride);

            sha.TransformBlock(widthBytes, 0, widthBytes.Length, null, 0);
            sha.TransformBlock(heightBytes, 0, heightBytes.Length, null, 0);
            sha.TransformBlock(strideBytes, 0, strideBytes.Length, null, 0);
            sha.TransformFinalBlock(bytes, 0, bytes.Length);

            return Convert.ToHexString(sha.Hash!);
        }
        finally
        {
            normalized.UnlockBits(data);
        }
    }

    public string ComputeSampleHash(Bitmap bitmap, int step)
    {
        var safeStep = Math.Clamp(step, 1, 128);

        using var normalized = CreateNormalizedBitmap(bitmap);

        var rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
        var data = normalized.LockBits(
            rect,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(header[..4], normalized.Width);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), normalized.Height);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), safeStep);
            hash.AppendData(header);

            Span<byte> pixelBuffer = stackalloc byte[4];

            for (var y = 0; y < normalized.Height; y += safeStep)
            {
                var rowOffset = y * data.Stride;

                for (var x = 0; x < normalized.Width; x += safeStep)
                {
                    var offset = rowOffset + (x * 4);
                    var argb = Marshal.ReadInt32(data.Scan0, offset);

                    BinaryPrimitives.WriteInt32LittleEndian(pixelBuffer, argb);
                    hash.AppendData(pixelBuffer);
                }
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            normalized.UnlockBits(data);
        }
    }

    private static Bitmap CreateNormalizedBitmap(Bitmap source)
    {
        var normalized = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(normalized);
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);

        return normalized;
    }
}
