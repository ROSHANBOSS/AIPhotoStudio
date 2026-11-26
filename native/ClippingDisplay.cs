using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIPhotoStudio
{
    internal static class ClippingDisplay
    {
        internal static int Flags(Dictionary<string,object> args)
        {
            object value;
            if (!args.TryGetValue("clipping", out value)) return 0;
            if (!(value is int) || (int)value < 0 || (int)value > 3)
                throw new ArgumentException("曝光提示设置无效。");
            return (int)value;
        }

        // Evaluate the rendered sRGB channels before display quantization or preview JPEG compression.
        internal static BitmapSource Mark(BitmapSource source, BitmapSource display, int flags, CancellationToken token)
        {
            if (flags == 0) return display;
            BitmapSource full = source.Format == PixelFormats.Rgba64 ? source :
                new FormatConvertedBitmap(source, PixelFormats.Rgba64, null, 0);
            int width = full.PixelWidth, height = full.PixelHeight;
            int block = Math.Min(height, Math.Max(1, 32768 / width));
            var rgb = new ushort[checked(width * block * 4)];
            var marked = new byte[rgb.Length];
            var output = new WriteableBitmap(width, height, display.DpiX, display.DpiY, PixelFormats.Bgra32, null);
            for (int y = 0; y < height; y += block)
            {
                token.ThrowIfCancellationRequested();
                var rect = new Int32Rect(0, y, width, Math.Min(block, height - y));
                full.CopyPixels(rect, rgb, width * 8, 0);
                display.CopyPixels(rect, marked, width * 4, 0);
                for (int i = 0; i < rect.Height * width * 4; i += 4)
                {
                    if (rgb[i+3] == 0) continue;
                    bool white = (flags & 1) != 0 && (rgb[i] == 65535 || rgb[i+1] == 65535 || rgb[i+2] == 65535);
                    bool black = (flags & 2) != 0 && (rgb[i] | rgb[i+1] | rgb[i+2]) == 0;
                    if (!white && !black) continue;
                    marked[i] = white ? (byte)48 : (byte)255;
                    marked[i+1] = white ? (byte)46 : (byte)114;
                    marked[i+2] = white ? (byte)255 : (byte)38;
                }
                output.WritePixels(rect, marked, width * 4, 0);
            }
            output.Freeze();
            return output;
        }
    }
}
