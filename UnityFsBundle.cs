using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PcTogetherRus
{
    internal sealed class UnityFsBundle
    {
        private const int ChunkSize = 128 * 1024;
        private const int BundleFlags = 0x240; // Directory at the front, with aligned block data.
        private const string LocaleEntry = "sharedassets1.assets";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private sealed class Block
        {
            public long Offset;
            public long FileOffset;
            public int Size;
            public int PackedSize;
            public int Compression;
        }

        private sealed class Entry
        {
            public long Offset;
            public long Size;
            public int Flags;
            public string Name;
        }

        private readonly Stream source;
        private readonly string playerVersion;
        private readonly string engineVersion;
        private readonly List<Block> blocks;
        private readonly List<Entry> entries;
        private readonly Entry locale;

        internal UnityFsBundle(Stream source)
        {
            this.source = source;
            using (var header = new BinaryReader(source, Utf8, true))
            {
                Require(ReadString(header) == "UnityFS" && ReadBigEndian(header, 4) == 8);
                playerVersion = ReadString(header);
                engineVersion = ReadString(header);
                Require(ReadBigEndian(header, 8) == source.Length);
                int packedSize = checked((int)ReadBigEndian(header, 4));
                int size = checked((int)ReadBigEndian(header, 4));
                int flags = checked((int)ReadBigEndian(header, 4));
                Require((flags & ~63) == BundleFlags && size > 0 && size <= 8 * 1024 * 1024);
                source.Position = Align(source.Position, 16);
                Require(packedSize > 0 && packedSize <= size && packedSize <= source.Length - source.Position);
                byte[] metadata = Decode(ReadBytes(header, packedSize), size, flags & 63);
                long fileOffset = Align(source.Position, 16);
                long offset = 0;
                using (var info = new BinaryReader(new MemoryStream(metadata, false), Utf8))
                {
                    Require(ReadBytes(info, 16).All(value => value == 0));
                    long blockCount = ReadBigEndian(info, 4);
                    Require(blockCount > 0 && blockCount <= Remaining(info) / 10);
                    blocks = new List<Block>((int)blockCount);
                    for (int i = 0; i < blockCount; i++)
                    {
                        int bytes = checked((int)ReadBigEndian(info, 4));
                        int packed = checked((int)ReadBigEndian(info, 4));
                        int compression = checked((int)ReadBigEndian(info, 2));
                        Require(bytes > 0 && bytes <= ChunkSize && (i == blockCount - 1 || bytes == ChunkSize) &&
                            packed > 0 && packed <= bytes && packed <= source.Length - fileOffset &&
                            (compression == 2 || compression == 3 || (compression == 0 && packed == bytes)));
                        blocks.Add(new Block { Offset = offset, FileOffset = fileOffset, Size = bytes,
                            PackedSize = packed, Compression = compression });
                        offset = checked(offset + bytes);
                        fileOffset = checked(fileOffset + packed);
                    }
                    Require(fileOffset == source.Length);

                    long entryCount = ReadBigEndian(info, 4);
                    Require(entryCount > 0 && entryCount <= Remaining(info) / 21);
                    entries = new List<Entry>((int)entryCount);
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < entryCount; i++)
                    {
                        var entry = new Entry
                        {
                            Offset = ReadBigEndian(info, 8),
                            Size = ReadBigEndian(info, 8),
                            Flags = unchecked((int)ReadBigEndian(info, 4)),
                            Name = ReadString(info)
                        };
                        Require(entry.Offset >= 0 && entry.Size >= 0 && entry.Offset <= offset - entry.Size &&
                            entry.Name.Length != 0 && names.Add(entry.Name));
                        entries.Add(entry);
                    }
                    Require(Remaining(info) == 0);
                    long end = 0;
                    foreach (var entry in entries.Where(entry => entry.Size > 0).OrderBy(entry => entry.Offset))
                    {
                        Require(entry.Offset >= end);
                        end = entry.Offset + entry.Size;
                    }
                }
            }

            locale = entries.SingleOrDefault(entry => entry.Name == LocaleEntry);
            Require(locale != null && locale.Flags == 4 && locale.Size > 0 && locale.Size <= 64 * 1024 * 1024);
        }

        internal byte[] ReadLocale()
        {
            var result = new byte[(int)locale.Size];
            long end = locale.Offset + locale.Size;
            for (int i = (int)(locale.Offset / ChunkSize); blocks[i].Offset < end; i++)
            {
                var block = blocks[i];
                long start = Math.Max(locale.Offset, block.Offset);
                int count = (int)(Math.Min(end, block.Offset + block.Size) - start);
                Buffer.BlockCopy(ReadBlock(block), (int)(start - block.Offset), result,
                    (int)(start - locale.Offset), count);
                if (start + count == end)
                {
                    break;
                }
            }
            return result;
        }

        internal void WriteWithLocale(Stream output, byte[] replacement)
        {
            long end = locale.Offset + locale.Size;
            int firstIndex = (int)(locale.Offset / ChunkSize);
            int lastIndex = (int)((end - 1) / ChunkSize);
            var first = blocks[firstIndex];
            var last = blocks[lastIndex];
            int prefix = (int)(locale.Offset - first.Offset);
            int suffix = (int)(last.Offset + last.Size - end);
            int growth = checked(replacement.Length - (int)locale.Size);
            Require(growth >= 0);
            // Preserve the chunk boundaries for every original block after the changed window.
            int shift = checked((int)Align(growth, ChunkSize));
            int windowSize = checked(prefix + replacement.Length + (shift - growth) + suffix);
            using (var infoStream = new MemoryStream())
            using (var info = new BinaryWriter(infoStream, Utf8))
            using (var writer = new BinaryWriter(output, Utf8, true))
            {
                info.Write(new byte[16]);
                int newBlockCount = (int)(Align(windowSize, ChunkSize) / ChunkSize);
                WriteBigEndian(info, blocks.Count - (lastIndex - firstIndex + 1) + newBlockCount, 4);
                for (int i = 0; i < firstIndex; i++)
                {
                    WriteBlockInfo(info, blocks[i].Size, blocks[i].PackedSize, blocks[i].Compression);
                }
                for (int remaining = windowSize; remaining > 0; remaining -= ChunkSize)
                {
                    int length = Math.Min(remaining, ChunkSize);
                    WriteBlockInfo(info, length, length, 0);
                }
                for (int i = lastIndex + 1; i < blocks.Count; i++)
                {
                    WriteBlockInfo(info, blocks[i].Size, blocks[i].PackedSize, blocks[i].Compression);
                }
                WriteBigEndian(info, entries.Count, 4);
                foreach (var entry in entries)
                {
                    WriteBigEndian(info, entry.Offset >= end ? entry.Offset + shift : entry.Offset, 8);
                    WriteBigEndian(info, entry == locale ? replacement.Length : entry.Size, 8);
                    WriteBigEndian(info, entry.Flags, 4);
                    WriteString(info, entry.Name);
                }

                WriteString(writer, "UnityFS");
                WriteBigEndian(writer, 8, 4);
                WriteString(writer, playerVersion);
                WriteString(writer, engineVersion);
                long lengthPosition = output.Position;
                WriteBigEndian(writer, 0, 8);
                WriteBigEndian(writer, infoStream.Length, 4);
                WriteBigEndian(writer, infoStream.Length, 4);
                WriteBigEndian(writer, BundleFlags, 4);
                output.Position = Align(output.Position, 16);
                infoStream.WriteTo(output);
                output.Position = Align(output.Position, 16);
                var buffer = new byte[1024 * 1024];
                CopyRange(output, blocks[0].FileOffset, first.FileOffset, buffer);
                if (prefix > 0)
                {
                    output.Write(ReadBlock(first), 0, prefix);
                }
                output.Write(replacement, 0, replacement.Length);
                output.Position += shift - growth;
                if (suffix > 0)
                {
                    output.Write(ReadBlock(last), last.Size - suffix, suffix);
                }
                CopyRange(output, last.FileOffset + last.PackedSize, source.Length, buffer);
                output.SetLength(output.Position);
                output.Position = lengthPosition;
                WriteBigEndian(writer, output.Length, 8);
            }
        }

        private byte[] ReadBlock(Block block)
        {
            source.Position = block.FileOffset;
            using (var reader = new BinaryReader(source, Utf8, true))
            {
                return Decode(ReadBytes(reader, block.PackedSize), block.Size, block.Compression);
            }
        }

        private void CopyRange(Stream output, long start, long end, byte[] buffer)
        {
            source.Position = start;
            while (source.Position < end)
            {
                int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, end - source.Position));
                if (count == 0)
                {
                    throw new EndOfStreamException();
                }
                output.Write(buffer, 0, count);
            }
        }

        private static byte[] Decode(byte[] packed, int size, int compression)
        {
            if (compression == 0)
            {
                Require(packed.Length == size);
                return packed;
            }
            Require(compression == 2 || compression == 3);
            var bytes = new byte[size];
            int input = 0;
            int output = 0;
            while (input < packed.Length)
            {
                int token = packed[input++];
                int literals = ReadLength(packed, ref input, token >> 4);
                Require(literals <= packed.Length - input && literals <= bytes.Length - output);
                Buffer.BlockCopy(packed, input, bytes, output, literals);
                input += literals;
                output += literals;
                if (input == packed.Length)
                {
                    break;
                }

                Require(packed.Length - input >= 2);
                int distance = packed[input] | (packed[input + 1] << 8);
                input += 2;
                int match = checked(ReadLength(packed, ref input, token & 15) + 4);
                Require(distance > 0 && distance <= output && match <= bytes.Length - output);
                int matchEnd = output + match;
                for (; output < matchEnd; output++)
                {
                    bytes[output] = bytes[output - distance];
                }
            }
            Require(output == size);
            return bytes;
        }

        private static int ReadLength(byte[] packed, ref int position, int length)
        {
            if (length == 15)
            {
                int extra;
                do
                {
                    Require(position < packed.Length);
                    extra = packed[position++];
                    length = checked(length + extra);
                }
                while (extra == 255);
            }
            return length;
        }

        private static void WriteBlockInfo(BinaryWriter writer, int size, int packed, int compression)
        {
            WriteBigEndian(writer, size, 4);
            WriteBigEndian(writer, packed, 4);
            WriteBigEndian(writer, compression, 2);
        }

        private static long ReadBigEndian(BinaryReader reader, int count)
        {
            long value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 8) | reader.ReadByte();
            }
            return value;
        }

        private static void WriteBigEndian(BinaryWriter writer, long value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                writer.Write((byte)(value >> (8 * i)));
            }
        }

        private static string ReadString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            for (byte value = reader.ReadByte(); value != 0; value = reader.ReadByte())
            {
                Require(bytes.Count < 4096);
                bytes.Add(value);
            }
            return Utf8.GetString(bytes.ToArray());
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(Utf8.GetBytes(value));
            writer.Write((byte)0);
        }

        private static byte[] ReadBytes(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            Require(bytes.Length == count);
            return bytes;
        }

        private static long Remaining(BinaryReader reader)
        {
            return reader.BaseStream.Length - reader.BaseStream.Position;
        }

        private static long Align(long value, int boundary)
        {
            return checked(value + boundary - 1) & -boundary;
        }

        private static void Require(bool condition)
        {
            if (!condition)
            {
                throw new InvalidDataException("Не поддерживается структура архива игры data.unity3d.");
            }
        }
    }
}
