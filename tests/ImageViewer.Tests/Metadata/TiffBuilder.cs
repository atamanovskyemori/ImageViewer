using System.Buffers.Binary;
using System.IO;
using System.Text;
using ImageViewer.Metadata;

namespace ImageViewer.Tests.Metadata
{
    /// <summary>
    /// Builds small TIFF/EXIF blocks for tests: IFD0, Exif IFD and GPS IFD, in either byte order.
    /// Pointers to the Exif / GPS IFDs are added automatically.
    /// </summary>
    internal sealed class TiffBuilder
    {
        private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

        private readonly bool _bigEndian;
        private readonly Dictionary<ExifIfd, List<Entry>> _ifds = new()
        {
            [ExifIfd.Primary] = [],
            [ExifIfd.Exif] = [],
            [ExifIfd.Gps] = [],
        };

        public TiffBuilder(bool bigEndian = false)
        {
            _bigEndian = bigEndian;
        }

        public TiffBuilder Ascii(ExifIfd ifd, ushort tag, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\0");
            return Raw(ifd, tag, 2, (uint)bytes.Length, bytes);
        }

        public TiffBuilder Short(ExifIfd ifd, ushort tag, params ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++) WriteU16(bytes.AsSpan(i * 2), values[i]);
            return Raw(ifd, tag, 3, (uint)values.Length, bytes);
        }

        public TiffBuilder Long(ExifIfd ifd, ushort tag, params uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) WriteU32(bytes.AsSpan(i * 4), values[i]);
            return Raw(ifd, tag, 4, (uint)values.Length, bytes);
        }

        public TiffBuilder Byte(ExifIfd ifd, ushort tag, params byte[] values) =>
            Raw(ifd, tag, 1, (uint)values.Length, values);

        public TiffBuilder Rational(ExifIfd ifd, ushort tag, params (uint Num, uint Den)[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (int i = 0; i < values.Length; i++)
            {
                WriteU32(bytes.AsSpan(i * 8), values[i].Num);
                WriteU32(bytes.AsSpan(i * 8 + 4), values[i].Den);
            }
            return Raw(ifd, tag, 5, (uint)values.Length, bytes);
        }

        public TiffBuilder SRational(ExifIfd ifd, ushort tag, int num, int den)
        {
            var bytes = new byte[8];
            WriteU32(bytes, unchecked((uint)num));
            WriteU32(bytes.AsSpan(4), unchecked((uint)den));
            return Raw(ifd, tag, 10, 1, bytes);
        }

        /// <summary>Adds an entry as is (for broken data: any type, count and payload).</summary>
        public TiffBuilder Raw(ExifIfd ifd, ushort tag, ushort type, uint count, byte[] data)
        {
            _ifds[ifd].Add(new Entry(tag, type, count, data));
            return this;
        }

        public byte[] Build()
        {
            var primary = new List<Entry>(_ifds[ExifIfd.Primary]);
            var exif = _ifds[ExifIfd.Exif];
            var gps = _ifds[ExifIfd.Gps];

            // Placeholders for the pointers; their values are filled in below once the offsets are known
            int exifPointer = -1, gpsPointer = -1;
            if (exif.Count > 0)
            {
                exifPointer = primary.Count;
                primary.Add(new Entry(ExifTags.ExifIfdPointer, 4, 1, new byte[4]));
            }
            if (gps.Count > 0)
            {
                gpsPointer = primary.Count;
                primary.Add(new Entry(ExifTags.GpsIfdPointer, 4, 1, new byte[4]));
            }

            long primaryOffset = 8;
            long exifOffset = primaryOffset + BlockSize(primary);
            long gpsOffset = exifOffset + (exif.Count > 0 ? BlockSize(exif) : 0);

            if (exifPointer >= 0) WriteU32(primary[exifPointer].Data, (uint)exifOffset);
            if (gpsPointer >= 0) WriteU32(primary[gpsPointer].Data, (uint)gpsOffset);

            using var output = new MemoryStream();
            var header = new byte[8];
            header[0] = header[1] = (byte)(_bigEndian ? 'M' : 'I');
            WriteU16(header.AsSpan(2), 42);
            WriteU32(header.AsSpan(4), (uint)primaryOffset);
            output.Write(header);

            WriteBlock(output, primary, primaryOffset);
            if (exif.Count > 0) WriteBlock(output, exif, exifOffset);
            if (gps.Count > 0) WriteBlock(output, gps, gpsOffset);

            return output.ToArray();
        }

        private static long BlockSize(List<Entry> entries) =>
            2 + 12L * entries.Count + 4 + entries.Where(e => e.Data.Length > 4).Sum(e => Even(e.Data.Length));

        private static int Even(int n) => n + (n & 1);

        private void WriteBlock(Stream output, List<Entry> entries, long offset)
        {
            long valueOffset = offset + 2 + 12L * entries.Count + 4;
            var values = new MemoryStream();

            var count = new byte[2];
            WriteU16(count, (ushort)entries.Count);
            output.Write(count);

            foreach (var entry in entries)
            {
                var raw = new byte[12];
                WriteU16(raw, entry.Tag);
                WriteU16(raw.AsSpan(2), entry.Type);
                WriteU32(raw.AsSpan(4), entry.Count);
                if (entry.Data.Length <= 4)
                {
                    entry.Data.CopyTo(raw, 8);
                }
                else
                {
                    WriteU32(raw.AsSpan(8), (uint)(valueOffset + values.Length));
                    values.Write(entry.Data);
                    if ((entry.Data.Length & 1) != 0) values.WriteByte(0);
                }
                output.Write(raw);
            }

            output.Write(new byte[4]); // no next IFD
            values.Position = 0;
            values.CopyTo(output);
        }

        private void WriteU16(Span<byte> target, ushort value)
        {
            if (_bigEndian) BinaryPrimitives.WriteUInt16BigEndian(target, value);
            else BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        }

        private void WriteU32(Span<byte> target, uint value)
        {
            if (_bigEndian) BinaryPrimitives.WriteUInt32BigEndian(target, value);
            else BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        }

        // ------------------------------------------------------------
        // Containers
        // ------------------------------------------------------------

        /// <summary>JPEG: SOI, APP0 (JFIF), an XMP APP1 (must be skipped), the Exif APP1, SOS.</summary>
        public static byte[] WrapJpeg(byte[] tiff, bool includeExif = true, bool exifAfterSos = false)
        {
            using var output = new MemoryStream();
            output.Write([0xFF, 0xD8]);
            WriteJpegSegment(output, 0xE0, "JFIF\0\x01\x02\0\0\x01\0\x01\0\0"u8.ToArray());
            WriteJpegSegment(output, 0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta/>"));

            if (includeExif && !exifAfterSos)
            {
                WriteJpegSegment(output, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]);
            }

            WriteJpegSegment(output, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
            output.Write([0x12, 0x34, 0x56]); // "image data"

            if (includeExif && exifAfterSos)
            {
                WriteJpegSegment(output, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]);
            }

            output.Write([0xFF, 0xD9]);
            return output.ToArray();
        }

        private static void WriteJpegSegment(Stream output, byte marker, byte[] payload)
        {
            output.Write([0xFF, marker]);
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
            output.Write(length);
            output.Write(payload);
        }

        /// <summary>PNG: signature, IHDR, (eXIf), IDAT, IEND. CRCs are dummies (the reader does not check them).</summary>
        public static byte[] WrapPng(byte[]? tiff)
        {
            using var output = new MemoryStream();
            output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            WritePngChunk(output, "IHDR", new byte[13]);
            if (tiff is not null)
            {
                WritePngChunk(output, "eXIf", tiff);
            }
            WritePngChunk(output, "IDAT", new byte[5]);
            WritePngChunk(output, "IEND", []);
            return output.ToArray();
        }

        private static void WritePngChunk(Stream output, string type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            output.Write(length);
            output.Write(Encoding.ASCII.GetBytes(type));
            output.Write(data);
            output.Write(new byte[4]); // CRC
        }

        /// <summary>WebP: RIFF header, VP8X, an odd-sized chunk (tests padding), image chunk, EXIF chunk last.</summary>
        public static byte[] WrapWebP(byte[]? tiff, bool withExifPrefix = false)
        {
            using var body = new MemoryStream();
            body.Write("WEBP"u8);
            WriteRiffChunk(body, "VP8X", new byte[10]);
            WriteRiffChunk(body, "ICCP", new byte[7]); // odd size -> one padding byte
            WriteRiffChunk(body, "VP8 ", new byte[16]);
            if (tiff is not null)
            {
                WriteRiffChunk(body, "EXIF", withExifPrefix ? [.. "Exif\0\0"u8.ToArray(), .. tiff] : tiff);
            }

            using var output = new MemoryStream();
            output.Write("RIFF"u8);
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
            output.Write(size);
            body.Position = 0;
            body.CopyTo(output);
            return output.ToArray();
        }

        private static void WriteRiffChunk(Stream output, string fourcc, byte[] data)
        {
            output.Write(Encoding.ASCII.GetBytes(fourcc));
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            output.Write(size);
            output.Write(data);
            if ((data.Length & 1) != 0) output.WriteByte(0);
        }
    }
}
