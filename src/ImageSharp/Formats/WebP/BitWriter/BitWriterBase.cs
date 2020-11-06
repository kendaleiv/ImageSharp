// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.IO;

namespace SixLabors.ImageSharp.Formats.WebP.BitWriter
{
    internal abstract class BitWriterBase
    {
        /// <summary>
        /// Buffer to write to.
        /// </summary>
        private byte[] buffer;

        /// <summary>
        /// Initializes a new instance of the <see cref="BitWriterBase"/> class.
        /// </summary>
        /// <param name="expectedSize">The expected size in bytes.</param>
        protected BitWriterBase(int expectedSize)
        {
            // TODO: use memory allocator here.
            this.buffer = new byte[expectedSize];
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BitWriterBase"/> class.
        /// Used internally for cloning.
        /// </summary>
        private protected BitWriterBase(byte[] buffer) => this.buffer = buffer;

        public byte[] Buffer
        {
            get { return this.buffer; }
        }

        /// <summary>
        /// Writes the encoded bytes of the image to the stream. Call Finish() before this.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteToStream(Stream stream)
        {
            stream.Write(this.Buffer.AsSpan(0, this.NumBytes()));
        }

        /// <summary>
        /// Resizes the buffer to write to.
        /// </summary>
        /// <param name="extraSize">The extra size in bytes needed.</param>
        public abstract void BitWriterResize(int extraSize);

        /// <summary>
        /// Returns the number of bytes of the encoded image data.
        /// </summary>
        /// <returns>The number of bytes of the image data.</returns>
        public abstract int NumBytes();

        /// <summary>
        /// Flush leftover bits.
        /// </summary>
        public abstract void Finish();

        protected bool ResizeBuffer(int maxBytes, int sizeRequired)
        {
            if (maxBytes > 0 && sizeRequired < maxBytes)
            {
                return true;
            }

            int newSize = (3 * maxBytes) >> 1;
            if (newSize < sizeRequired)
            {
                newSize = sizeRequired;
            }

            // Make new size multiple of 1k.
            newSize = ((newSize >> 10) + 1) << 10;

            // TODO: use memory allocator.
            Array.Resize(ref this.buffer, newSize);

            return false;
        }

        /// <summary>
        /// Writes the encoded image to the stream.
        /// </summary>
        /// <param name="lossy">If true, lossy tag will be written, otherwise a lossless tag.</param>
        /// <param name="stream">The stream to write to.</param>
        public void WriteEncodedImageToStream(bool lossy, Stream stream)
        {
            this.Finish();
            var numBytes = this.NumBytes();
            var size = numBytes;
            if (!lossy)
            {
                size++; // One byte extra for the VP8L signature.
            }

            var pad = size & 1;
            var riffSize = WebPConstants.TagSize + WebPConstants.ChunkHeaderSize + size + pad;
            this.WriteRiffHeader(riffSize, size, lossy, stream);
            this.WriteToStream(stream);
            if (pad == 1)
            {
                stream.WriteByte(0);
            }
        }

        /// <summary>
        /// Writes the RIFF header to the stream.
        /// </summary>
        /// <param name="riffSize">The block length.</param>
        /// <param name="size">The size in bytes of the encoded image.</param>
        /// <param name="lossy">If true, lossy tag will be written, otherwise a lossless tag.</param>
        /// <param name="stream">The stream to write to.</param>
        private void WriteRiffHeader(int riffSize, int size, bool lossy, Stream stream)
        {
            Span<byte> buffer = stackalloc byte[4];

            stream.Write(WebPConstants.RiffFourCc);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)riffSize);
            stream.Write(buffer);
            stream.Write(WebPConstants.WebPHeader);

            if (lossy)
            {
                stream.Write(WebPConstants.Vp8MagicBytes);
            }
            else
            {
                stream.Write(WebPConstants.Vp8LMagicBytes);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)size);
            stream.Write(buffer);
            stream.WriteByte(WebPConstants.Vp8LMagicByte);
        }
    }
}