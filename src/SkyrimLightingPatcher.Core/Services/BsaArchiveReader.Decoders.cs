using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SkyrimLightingPatcher.Core.Services;

internal sealed partial class BsaArchiveReader
{
    private static class Lz4BlockDecoder
    {
        public static byte[] Decode(ReadOnlySpan<byte> input, int outputLength)
        {
            var output = new byte[outputLength];
            var decodedLength = DecodeToBuffer(
                input,
                output,
                outputStartIndex: 0,
                maxOutputIndexExclusive: outputLength,
                minimumMatchSourceIndex: 0);

            if (decodedLength != output.Length)
            {
                throw new InvalidDataException("Invalid LZ4 block: decoded data length does not match the expected size.");
            }

            return output;
        }

        public static int DecodeToBuffer(
            ReadOnlySpan<byte> input,
            byte[] output,
            int outputStartIndex,
            int maxOutputIndexExclusive,
            int minimumMatchSourceIndex)
        {
            var inputIndex = 0;
            var outputIndex = outputStartIndex;

            while (inputIndex < input.Length)
            {
                var token = input[inputIndex++];
                var literalLength = token >> 4;
                literalLength += ReadLength(ref inputIndex, input, literalLength);

                if (literalLength > 0)
                {
                    CopyLiteral(
                        input,
                        ref inputIndex,
                        output,
                        ref outputIndex,
                        literalLength,
                        maxOutputIndexExclusive);
                }

                if (inputIndex >= input.Length)
                {
                    break;
                }

                if (inputIndex + 1 >= input.Length)
                {
                    throw new InvalidDataException("Invalid LZ4 block: missing match offset.");
                }

                var offset = input[inputIndex] | (input[inputIndex + 1] << 8);
                inputIndex += 2;
                var matchSource = outputIndex - offset;
                if (offset <= 0 || matchSource < minimumMatchSourceIndex)
                {
                    throw new InvalidDataException("Invalid LZ4 block: match offset is out of range.");
                }

                var matchLength = token & 0x0f;
                matchLength += ReadLength(ref inputIndex, input, matchLength);
                matchLength += 4;

                if (outputIndex + matchLength > maxOutputIndexExclusive)
                {
                    throw new InvalidDataException("Invalid LZ4 block: output exceeds the expected size.");
                }

                for (var copyIndex = 0; copyIndex < matchLength; copyIndex++)
                {
                    output[outputIndex] = output[outputIndex - offset];
                    outputIndex++;
                }
            }

            return outputIndex - outputStartIndex;
        }

        private static int ReadLength(ref int inputIndex, ReadOnlySpan<byte> input, int initialLength)
        {
            if (initialLength != 0x0f)
            {
                return 0;
            }

            var total = 0;
            while (true)
            {
                if (inputIndex >= input.Length)
                {
                    throw new InvalidDataException("Invalid LZ4 block: truncated length.");
                }

                var next = input[inputIndex++];
                total += next;
                if (next != 0xff)
                {
                    return total;
                }
            }
        }

        private static void CopyLiteral(
            ReadOnlySpan<byte> input,
            ref int inputIndex,
            byte[] output,
            ref int outputIndex,
            int length,
            int maxOutputIndexExclusive)
        {
            if (inputIndex + length > input.Length || outputIndex + length > maxOutputIndexExclusive)
            {
                throw new InvalidDataException("Invalid LZ4 block: literal exceeds input or output bounds.");
            }

            input.Slice(inputIndex, length).CopyTo(output.AsSpan(outputIndex, length));
            inputIndex += length;
            outputIndex += length;
        }
    }

    private static class Lz4FrameDecoder
    {
        public static byte[] Decode(ReadOnlySpan<byte> input, int expectedLength)
        {
            var index = 0;
            var magic = ReadUInt32LittleEndian(input, ref index);
            if (magic != Lz4FrameMagic)
            {
                throw new InvalidDataException("Invalid LZ4 frame: missing frame magic.");
            }

            var flg = ReadByte(input, ref index);
            var bd = ReadByte(input, ref index);

            var version = (flg >> 6) & 0x03;
            if (version != 0x01)
            {
                throw new InvalidDataException("Invalid LZ4 frame: unsupported version.");
            }

            var hasBlockChecksum = (flg & 0x10) != 0;
            var hasContentSize = (flg & 0x08) != 0;
            var hasContentChecksum = (flg & 0x04) != 0;
            var hasDictionaryId = (flg & 0x01) != 0;
            var blockIndependent = (flg & 0x20) != 0;

            ulong contentSize = 0;
            if (hasContentSize)
            {
                contentSize = ReadUInt64LittleEndian(input, ref index);
            }

            if (hasDictionaryId)
            {
                _ = ReadUInt32LittleEndian(input, ref index);
            }

            _ = ReadByte(input, ref index); // Header checksum (optional validation omitted)

            var maxBlockSize = DecodeMaxBlockSize(bd);
            var output = new byte[expectedLength];
            var outputIndex = 0;

            while (true)
            {
                var blockDescriptor = ReadUInt32LittleEndian(input, ref index);
                if (blockDescriptor == 0)
                {
                    break;
                }

                var isUncompressed = (blockDescriptor & 0x8000_0000) != 0;
                var blockSize = checked((int)(blockDescriptor & 0x7FFF_FFFF));
                if (blockSize <= 0)
                {
                    throw new InvalidDataException("Invalid LZ4 frame: invalid block size.");
                }
                if (blockSize > maxBlockSize)
                {
                    throw new InvalidDataException("Invalid LZ4 frame: block size exceeds max block size.");
                }

                var block = ReadSpan(input, ref index, blockSize);
                if (isUncompressed)
                {
                    if (outputIndex + block.Length > output.Length)
                    {
                        throw new InvalidDataException("Invalid LZ4 frame: decoded output exceeds expected size.");
                    }

                    block.CopyTo(output.AsSpan(outputIndex));
                    outputIndex += block.Length;
                }
                else
                {
                    var maxOutputIndex = Math.Min(output.Length, checked(outputIndex + maxBlockSize));
                    var minimumMatchSourceIndex = blockIndependent
                        ? outputIndex
                        : Math.Max(0, outputIndex - (64 * 1024));

                    var decodedLength = Lz4BlockDecoder.DecodeToBuffer(
                        block,
                        output,
                        outputIndex,
                        maxOutputIndex,
                        minimumMatchSourceIndex);
                    outputIndex += decodedLength;
                }

                if (hasBlockChecksum)
                {
                    _ = ReadUInt32LittleEndian(input, ref index); // Optional checksum (validation omitted)
                }
            }

            if (hasContentChecksum)
            {
                _ = ReadUInt32LittleEndian(input, ref index); // Optional checksum (validation omitted)
            }

            if (hasContentSize && contentSize != checked((ulong)outputIndex))
            {
                throw new InvalidDataException("Invalid LZ4 frame: content size does not match decoded output.");
            }

            if (outputIndex != expectedLength)
            {
                throw new InvalidDataException("Invalid LZ4 frame: decoded output length does not match expected size.");
            }

            return output;
        }

        private static int DecodeMaxBlockSize(byte bd)
        {
            return ((bd >> 4) & 0x07) switch
            {
                4 => 64 * 1024,
                5 => 256 * 1024,
                6 => 1024 * 1024,
                7 => 4 * 1024 * 1024,
                _ => throw new InvalidDataException("Invalid LZ4 frame: unsupported maximum block size."),
            };
        }

        private static byte ReadByte(ReadOnlySpan<byte> input, ref int index)
        {
            if (index >= input.Length)
            {
                throw new InvalidDataException("Invalid LZ4 frame: unexpected end of frame.");
            }

            return input[index++];
        }

        private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> input, ref int index)
        {
            var span = ReadSpan(input, ref index, 4);
            return BinaryPrimitives.ReadUInt32LittleEndian(span);
        }

        private static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> input, ref int index)
        {
            var span = ReadSpan(input, ref index, 8);
            return BinaryPrimitives.ReadUInt64LittleEndian(span);
        }

        private static ReadOnlySpan<byte> ReadSpan(ReadOnlySpan<byte> input, ref int index, int length)
        {
            if (length < 0 || index + length > input.Length)
            {
                throw new InvalidDataException("Invalid LZ4 frame: unexpected end of frame.");
            }

            var span = input.Slice(index, length);
            index += length;
            return span;
        }
    }
}

internal sealed record BsaArchiveIndex(
    string ArchivePath,
    int Version,
    bool ContainsMeshes,
    IReadOnlyList<BsaArchiveEntry> Entries)
{
    public IReadOnlyList<BsaArchiveEntry> MeshEntries =>
        Entries.Where(static entry =>
                entry.EntryPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) &&
                entry.EntryPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .ToArray();
}

internal sealed record BsaArchiveEntry(
    string ArchivePath,
    string EntryPath,
    uint Size,
    uint Offset,
    bool IsCompressed,
    int Version,
    bool EmbedFileNames);
