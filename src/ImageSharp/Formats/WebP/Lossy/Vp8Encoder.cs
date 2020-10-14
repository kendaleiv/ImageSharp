// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

using SixLabors.ImageSharp.Formats.WebP.BitWriter;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace SixLabors.ImageSharp.Formats.WebP.Lossy
{
    /// <summary>
    /// Encoder for lossy webp images.
    /// </summary>
    internal class Vp8Encoder : IDisposable
    {
        /// <summary>
        /// The <see cref="MemoryAllocator"/> to use for buffer allocations.
        /// </summary>
        private readonly MemoryAllocator memoryAllocator;

        /// <summary>
        /// A bit writer for writing lossy webp streams.
        /// </summary>
        private readonly Vp8BitWriter bitWriter;

        /// <summary>
        /// Fixed-point precision for RGB->YUV.
        /// </summary>
        private const int YuvFix = 16;

        private const int YuvHalf = 1 << (YuvFix - 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="Vp8Encoder"/> class.
        /// </summary>
        /// <param name="memoryAllocator">The memory allocator.</param>
        /// <param name="width">The width of the input image.</param>
        /// <param name="height">The height of the input image.</param>
        public Vp8Encoder(MemoryAllocator memoryAllocator, int width, int height)
        {
            this.memoryAllocator = memoryAllocator;

            var pixelCount = width * height;
            var uvSize = (width >> 1) * (height >> 1);
            this.Y = this.memoryAllocator.Allocate<byte>(pixelCount);
            this.U = this.memoryAllocator.Allocate<byte>(uvSize);
            this.V = this.memoryAllocator.Allocate<byte>(uvSize);

            // TODO: properly initialize the bitwriter
            this.bitWriter = new Vp8BitWriter();
        }

        private IMemoryOwner<byte> Y { get; }

        private IMemoryOwner<byte> U { get; }

        private IMemoryOwner<byte> V { get; }

        public void Encode<TPixel>(Image<TPixel> image, Stream stream)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            int uvWidth = (image.Width + 1) >> 1;

            // Temporary storage for accumulated R/G/B values during conversion to U/V.
            using (IMemoryOwner<ushort> tmpRgb = this.memoryAllocator.Allocate<ushort>(4 * uvWidth))
            {
                Span<ushort> tmpRgbSpan = tmpRgb.GetSpan();
                int uvRowIndex = 0;
                for (int rowIndex = 0; rowIndex < image.Height - 1; rowIndex += 2)
                {
                    // Downsample U/V planes, two rows at a time.
                    // TODO: RGBA case AccumulateRgba
                    Span<TPixel> rowSpan = image.GetPixelRowSpan(rowIndex);
                    Span<TPixel> nextRowSpan = image.GetPixelRowSpan(rowIndex + 1);
                    this.AccumulateRgb(rowSpan, nextRowSpan, tmpRgbSpan, image.Width);
                    this.ConvertRgbaToUv(tmpRgbSpan, this.U.Slice(uvRowIndex * uvWidth), this.V.Slice(uvRowIndex * uvWidth), uvWidth);
                    uvRowIndex++;

                    this.ConvertRgbaToY(rowSpan, this.Y.Slice(rowIndex * image.Width), image.Width);
                    this.ConvertRgbaToY(nextRowSpan, this.Y.Slice((rowIndex + 1) * image.Width), image.Width);
                }

                // TODO: last row
            }

            throw new NotImplementedException();
        }

        private void ConvertRgbaToY<TPixel>(Span<TPixel> rowSpan, Span<byte> y, int width)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            Rgba32 rgba = default;
            for (int x = 0; x < width; x++)
            {
                TPixel color = rowSpan[x];
                color.ToRgba32(ref rgba);
                y[x] = (byte)this.RgbToY(rgba.R, rgba.G, rgba.B, YuvHalf);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.Y.Dispose();
            this.U.Dispose();
            this.V.Dispose();
        }

        private void ConvertRgbaToUv(Span<ushort> rgb, Span<byte> u, Span<byte> v, int width)
        {
            int rgbIdx = 0;
            for (int i = 0; i < width; i += 1, rgbIdx += 4)
            {
                int r = rgb[rgbIdx], g = rgb[rgbIdx + 1], b = rgb[rgbIdx + 2];
                u[i] = (byte)this.RgbToU(r, g, b, YuvHalf << 2);
                v[i] = (byte)this.RgbToV(r, g, b, YuvHalf << 2);
            }
        }

        private void AccumulateRgb<TPixel>(Span<TPixel> rowSpan, Span<TPixel> nextRowSpan, Span<ushort> dst, int width)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            Rgba32 rgba0 = default;
            Rgba32 rgba1 = default;
            Rgba32 rgba2 = default;
            Rgba32 rgba3 = default;
            int i, j;
            int dstIdx = 0;
            for (i = 0, j = 0; i < (width >> 1); i += 1, j += 2, dstIdx += 4)
            {
                TPixel color = rowSpan[j];
                color.ToRgba32(ref rgba0);
                color = rowSpan[j + 1];
                color.ToRgba32(ref rgba1);
                color = nextRowSpan[j];
                color.ToRgba32(ref rgba2);
                color = nextRowSpan[j + 1];
                color.ToRgba32(ref rgba3);

                dst[dstIdx] = (ushort)this.LinearToGamma(
                    this.GammaToLinear(rgba0.R) +
                            this.GammaToLinear(rgba1.R) +
                            this.GammaToLinear(rgba2.R) +
                            this.GammaToLinear(rgba3.R), 0);
                dst[dstIdx + 1] = (ushort)this.LinearToGamma(
                    this.GammaToLinear(rgba0.G) +
                            this.GammaToLinear(rgba1.G) +
                            this.GammaToLinear(rgba2.G) +
                            this.GammaToLinear(rgba3.G), 0);
                dst[dstIdx + 2] = (ushort)this.LinearToGamma(
                    this.GammaToLinear(rgba0.B) +
                            this.GammaToLinear(rgba1.B) +
                            this.GammaToLinear(rgba2.B) +
                            this.GammaToLinear(rgba3.B), 0);
            }

            if ((width & 1) != 0)
            {
                TPixel color = rowSpan[j];
                color.ToRgba32(ref rgba0);
                color = nextRowSpan[j];
                color.ToRgba32(ref rgba1);

                dst[dstIdx] = (ushort)this.LinearToGamma(this.GammaToLinear(rgba0.R) + this.GammaToLinear(rgba1.R), 1);
                dst[dstIdx + 1] = (ushort)this.LinearToGamma(this.GammaToLinear(rgba0.G) + this.GammaToLinear(rgba1.G), 1);
                dst[dstIdx + 2] = (ushort)this.LinearToGamma(this.GammaToLinear(rgba0.B) + this.GammaToLinear(rgba1.B), 1);
            }
        }

        // Convert a linear value 'v' to YUV_FIX+2 fixed-point precision
        // U/V value, suitable for RGBToU/V calls.
        [MethodImpl(InliningOptions.ShortMethod)]
        private int LinearToGamma(uint baseValue, int shift)
        {
            int y = this.Interpolate((int)(baseValue << shift));   // Final uplifted value.
            return (y + WebPConstants.GammaTabRounder) >> WebPConstants.GammaTabFix;    // Descale.
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private uint GammaToLinear(byte v)
        {
            return WebPLookupTables.GammaToLinearTab[v];
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private int Interpolate(int v)
        {
            int tabPos = v >> (WebPConstants.GammaTabFix + 2);    // integer part
            int x = v & ((WebPConstants.GammaTabScale << 2) - 1);  // fractional part
            int v0 = WebPLookupTables.LinearToGammaTab[tabPos];
            int v1 = WebPLookupTables.LinearToGammaTab[tabPos + 1];
            int y = (v1 * x) + (v0 * ((WebPConstants.GammaTabScale << 2) - x));   // interpolate

            return y;
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private int RgbToY(byte r, byte g, byte b, int rounding)
        {
            int luma = (16839 * r) + (33059 * g) + (6420 * b);
            return (luma + rounding + (16 << YuvFix)) >> YuvFix;  // No need to clip.
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private int RgbToU(int r, int g, int b, int rounding)
        {
            int u = (-9719 * r) - (19081 * g) + (28800 * b);
            return this.ClipUv(u, rounding);
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private int RgbToV(int r, int g, int b, int rounding)
        {
            int v = (+28800 * r) - (24116 * g) - (4684 * b);
            return this.ClipUv(v, rounding);
        }

        [MethodImpl(InliningOptions.ShortMethod)]
        private int ClipUv(int uv, int rounding)
        {
            uv = (uv + rounding + (128 << (YuvFix + 2))) >> (YuvFix + 2);
            return ((uv & ~0xff) == 0) ? uv : (uv < 0) ? 0 : 255;
        }
    }
}
