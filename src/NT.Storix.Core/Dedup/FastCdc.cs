namespace NT.Storix.Core.Dedup;

/// <summary>
/// Content-defined chunking (FastCDC with normalized chunking). Cut points depend on the content, so inserting
/// or removing bytes only changes the chunks around the edit; the rest of the file deduplicates.
/// </summary>
public sealed class FastCdc
{
    public const int DefaultMin = 512 * 1024;
    public const int DefaultAverage = 1024 * 1024;
    public const int DefaultMax = 4 * 1024 * 1024;

    private static readonly ulong[] Gear = CreateGear();
    private readonly int _min;
    private readonly int _average;
    private readonly int _max;
    private readonly ulong _maskSmall;
    private readonly ulong _maskLarge;

    public FastCdc(int min = DefaultMin, int average = DefaultAverage, int max = DefaultMax)
    {
        if (min <= 0 || average <= min || max <= average)
        {
            throw new ArgumentException("Chunk sizes must satisfy 0 < min < average < max.");
        }

        (_min, _average, _max) = (min, average, max);
        var bits = (int)Math.Round(Math.Log2(average));
        _maskSmall = Mask(bits + 2);
        _maskLarge = Mask(bits - 2);
    }

    public int MaxSize => _max;

    /// <summary>Splits a stream into chunks. The data of a chunk is only valid until the next one is returned.</summary>
    public IEnumerable<(long Offset, ReadOnlyMemory<byte> Data)> Split(Stream stream)
    {
        var buffer = new byte[_max];
        var filled = 0;
        long offset = 0;
        var end = false;
        while (true)
        {
            while (!end && filled < _max)
            {
                var read = stream.Read(buffer, filled, _max - filled);
                if (read == 0)
                {
                    end = true;
                }

                filled += read;
            }

            if (filled == 0)
            {
                yield break;
            }

            var cut = Cut(buffer.AsSpan(0, filled));
            yield return (offset, buffer.AsMemory(0, cut));
            offset += cut;
            Buffer.BlockCopy(buffer, cut, buffer, 0, filled - cut);
            filled -= cut;
        }
    }

    /// <summary>Length of the first chunk of <paramref name="data"/>.</summary>
    internal int Cut(ReadOnlySpan<byte> data)
    {
        var length = data.Length;
        if (length <= _min)
        {
            return length;
        }

        var normal = Math.Min(_average, length);
        var limit = Math.Min(_max, length);
        ulong hash = 0;
        var i = _min;
        for (; i < normal; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & _maskSmall) == 0)
            {
                return i + 1;
            }
        }

        for (; i < limit; i++)
        {
            hash = (hash << 1) + Gear[data[i]];
            if ((hash & _maskLarge) == 0)
            {
                return i + 1;
            }
        }

        return limit;
    }

    private static ulong Mask(int bits)
    {
        // Spread the mask bits over the upper part of the hash (as in the FastCDC paper).
        ulong mask = 0;
        for (var i = 0; i < bits; i++)
        {
            mask |= 1UL << (63 - (i * 2 % 48));
        }

        return mask;
    }

    private static ulong[] CreateGear()
    {
        // Fixed table: chunk boundaries (and therefore deduplication) must never change between versions.
        var table = new ulong[256];
        ulong state = 0x9E3779B97F4A7C15;
        for (var i = 0; i < table.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            table[i] = state;
        }

        return table;
    }
}
