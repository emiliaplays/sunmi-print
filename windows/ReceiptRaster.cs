using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SunmiPrint
{
    internal sealed class ReceiptRaster
    {
        public const int Width = 384;
        private const int MaxHeight = 8192;
        private const int Margin = 14;
        public readonly int Height;
        public readonly byte[] Packed;
        public readonly BitmapSource Preview;

        private ReceiptRaster(RenderTargetBitmap bitmap, bool dither)
        {
            Height = bitmap.PixelHeight;
            byte[] rgba = new byte[Width * Height * 4];
            bitmap.CopyPixels(rgba, Width * 4, 0);
            Packed = new byte[Width / 8 * Height];
            float[] current = new float[Width + 2];
            float[] next = new float[Width + 2];
            for (int row = 0; row < Height; row++)
            {
                for (int col = 0; col < Width; col++)
                {
                    int pixel = row * Width + col;
                    int offset = pixel * 4;
                    float luminance = (rgba[offset] * 11 + rgba[offset + 1] * 59 + rgba[offset + 2] * 30) / 100f;
                    float corrected = luminance + current[col + 1];
                    bool black = corrected < (dither ? 128 : 168);
                    if (black)
                        Packed[pixel / 8] |= (byte)(0x80 >> (pixel % 8));
                    if (dither)
                    {
                        float error = (corrected - (black ? 0 : 255)) / 16;
                        current[col + 2] += error * 7;
                        next[col] += error * 3;
                        next[col + 1] += error * 5;
                        next[col + 2] += error;
                    }
                }
                float[] used = current;
                current = next;
                next = used;
                Array.Clear(next, 0, next.Length);
            }
            var palette = new BitmapPalette(new[] { Colors.White, Colors.Black });
            Preview = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Indexed1, palette, Packed, Width / 8);
            Preview.Freeze();
        }

        public static ReceiptRaster FromText(string text)
        {
            var layout = new FormattedText(text.Replace("\t", "    "), CultureInfo.GetCultureInfo("de-DE"),
                FlowDirection.LeftToRight, new Typeface("Consolas"), 20, Brushes.Black, 1)
            {
                MaxTextWidth = Width - Margin * 2,
                LineHeight = 27,
                Trimming = TextTrimming.None
            };
            int height = (int)Math.Ceiling(layout.Height) + Margin * 2;
            return Render(height, false, canvas => canvas.DrawText(layout, new Point(Margin, Margin)));
        }

        public static ReceiptRaster FromImage(string path)
        {
            int width;
            int height;
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }
            int printWidth = Math.Min(Width, width);
            int printHeight = Math.Max(1, (int)Math.Round((double)height * printWidth / width));
            CheckHeight(printHeight);
            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(path))
            {
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (width > Width)
                    bitmap.DecodePixelWidth = Width;
                bitmap.EndInit();
            }
            var area = new Rect((Width - printWidth) / 2, 0, printWidth, printHeight);
            return Render(printHeight, true, canvas => canvas.DrawImage(bitmap, area));
        }

        private static void CheckHeight(int height)
        {
            if (height < 1 || height > MaxHeight)
                throw new InvalidOperationException("Der Beleg ist zu lang. Kürze den Text oder teile das Bild auf.");
        }

        private static ReceiptRaster Render(int height, bool dither, Action<DrawingContext> draw)
        {
            CheckHeight(height);
            var visual = new DrawingVisual();
            using (DrawingContext canvas = visual.RenderOpen())
            {
                canvas.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, height));
                draw(canvas);
            }
            var bitmap = new RenderTargetBitmap(Width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            return new ReceiptRaster(bitmap, dither);
        }
    }
}
