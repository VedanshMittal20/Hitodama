using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Companion.Services;

public sealed class ScreenCaptureService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rect);

    public static Rectangle GetForegroundWindowBounds(Rectangle screenBounds)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out NativeRect rect))
        {
            return screenBounds;
        }

        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return Rectangle.Intersect(screenBounds, bounds);
    }

    public static byte[] CaptureRegionToJpeg(Rectangle region, int maxDimension = 1280, long jpegQuality = 85L)
    {
        if (region.Width < 2 || region.Height < 2)
        {
            throw new ArgumentException("Region must have positive dimensions.", nameof(region));
        }

        using var capture = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(capture))
        {
            graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        }

        using Bitmap resized = ResizeIfNeeded(capture, maxDimension);
        using var stream = new MemoryStream();
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(item => item.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);
        resized.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private static Bitmap ResizeIfNeeded(Bitmap source, int maxDimension)
    {
        int longestSide = Math.Max(source.Width, source.Height);
        if (longestSide <= maxDimension)
        {
            return (Bitmap)source.Clone();
        }

        double scale = (double)maxDimension / longestSide;
        var resized = new Bitmap(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, resized.Width, resized.Height);
        return resized;
    }
}
