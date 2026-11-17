using System.Buffers.Binary;
using Snappier;

namespace Aura.Presence.Services.Chromium;

public readonly record struct LevelDbEntry(byte[] Key, ulong Sequence, byte[]? Value);

public static class LevelDb
{
    private const int LogBlockSize = 32 * 1024;
    private const int TableFooterSize = 48;

    public static IEnumerable<LevelDbEntry> ReadDirectory(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(file);

            if (extension is not (".log" or ".ldb"))
                continue;

            List<LevelDbEntry> entries;

            try
            {
                var data = ReadShared(file);
                entries = extension == ".log" ? ReadLog(data) : ReadTable(data);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException
                                                  or IndexOutOfRangeException or InvalidDataException)
            {
                continue;
            }

            foreach (var entry in entries)
                yield return entry;
        }
    }

    public static List<LevelDbEntry> ReadLog(byte[] data)
    {
        var entries = new List<LevelDbEntry>();
        var record = new MemoryStream();

        for (var block = 0; block < data.Length; block += LogBlockSize)
        {
            var end = Math.Min(block + LogBlockSize, data.Length);
            var position = block;

            while (position + 7 <= end)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4));
                var type = data[position + 6];

                if (type == 0 || position + 7 + length > end)
                    break;

                if (type is 1 or 2)
                    record.SetLength(0);

                record.Write(data, position + 7, length);

                if (type is 1 or 4)
                    ReadWriteBatch(record.ToArray(), entries);

                position += 7 + length;
            }
        }

        return entries;
    }

    public static List<LevelDbEntry> ReadTable(byte[] data)
    {
        var entries = new List<LevelDbEntry>();

        if (data.Length < TableFooterSize)
            return entries;

        var position = data.Length - TableFooterSize;
        ReadVarint(data, ref position);
        ReadVarint(data, ref position);
        var indexOffset = (int)ReadVarint(data, ref position);
        var indexSize = (int)ReadVarint(data, ref position);

        foreach (var (_, handle) in ReadBlock(data, indexOffset, indexSize))
        {
            var handlePosition = 0;
            var offset = (int)ReadVarint(handle, ref handlePosition);
            var size = (int)ReadVarint(handle, ref handlePosition);

            foreach (var (internalKey, value) in ReadBlock(data, offset, size))
            {
                if (internalKey.Length < 8)
                    continue;

                var trailer = BinaryPrimitives.ReadUInt64LittleEndian(internalKey.AsSpan(internalKey.Length - 8));
                entries.Add(new LevelDbEntry(internalKey[..^8], trailer >> 8, (trailer & 0xff) == 1 ? value : null));
            }
        }

        return entries;
    }

    private static void ReadWriteBatch(byte[] batch, List<LevelDbEntry> entries)
    {
        if (batch.Length < 12)
            return;

        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(batch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(batch.AsSpan(8));
        var position = 12;

        for (var index = 0u; index < count && position < batch.Length; index++)
        {
            var isValue = batch[position++] == 1;
            var key = ReadSlice(batch, ref position);
            var value = isValue ? ReadSlice(batch, ref position) : null;

            entries.Add(new LevelDbEntry(key, sequence + index, value));
        }
    }

    private static List<(byte[] Key, byte[] Value)> ReadBlock(byte[] data, int offset, int size)
    {
        var block = data[offset + size] == 1
            ? Snappy.DecompressToArray(data.AsSpan(offset, size))
            : data.AsSpan(offset, size).ToArray();

        var restarts = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(block.Length - 4));
        var limit = block.Length - 4 - restarts * 4;
        var entries = new List<(byte[], byte[])>();
        var key = Array.Empty<byte>();
        var position = 0;

        while (position < limit)
        {
            var shared = (int)ReadVarint(block, ref position);
            var unshared = (int)ReadVarint(block, ref position);
            var valueLength = (int)ReadVarint(block, ref position);

            key = [.. key.AsSpan(0, shared), .. block.AsSpan(position, unshared)];
            position += unshared;
            entries.Add((key, block.AsSpan(position, valueLength).ToArray()));
            position += valueLength;
        }

        return entries;
    }

    private static byte[] ReadSlice(byte[] data, ref int position)
    {
        var length = (int)ReadVarint(data, ref position);
        var slice = data.AsSpan(position, length).ToArray();
        position += length;

        return slice;
    }

    private static ulong ReadVarint(byte[] data, ref int position)
    {
        ulong value = 0;

        for (var shift = 0; ; shift += 7)
        {
            var current = data[position++];
            value |= (ulong)(current & 0x7f) << shift;

            if (current < 0x80)
                return value;
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);

        return buffer;
    }
}
