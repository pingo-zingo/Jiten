namespace Jiten.Parser;

/// <summary>Exact set of 64-bit hashes behind a 4 MB bit filter; most probes miss, and the filter stays in cache where the set cannot.</summary>
internal sealed class FilteredHashSet
{
    // 2^25 bits = 4 MiB. Seven double-hashed probes keep the false-positive rate
    // very low at the current ~1.55M key count. False positives are harmless:
    // every positive candidate is verified against the exact SQLite lookup table.
    private const int FilterBits = 25;
    private const int FilterMask = (1 << FilterBits) - 1;
    private const int HashFunctions = 7;
    private readonly ulong[] _filter = new ulong[(1 << FilterBits) / 64];

    public FilteredHashSet(int capacity) { _ = capacity; }

    public void Add(long hash)
    {
        ulong h1 = Mix(unchecked((ulong)hash));
        ulong h2 = Mix(unchecked((ulong)hash ^ 0xD6E8FEB86659FD93UL)) | 1UL;
        for (int i = 0; i < HashFunctions; i++)
        {
            ulong slot = (h1 + (ulong)i * h2) & FilterMask;
            _filter[slot >> 6] |= 1UL << (int)(slot & 63);
        }
    }

    public bool Contains(long hash)
    {
        ulong h1 = Mix(unchecked((ulong)hash));
        ulong h2 = Mix(unchecked((ulong)hash ^ 0xD6E8FEB86659FD93UL)) | 1UL;
        for (int i = 0; i < HashFunctions; i++)
        {
            ulong slot = (h1 + (ulong)i * h2) & FilterMask;
            if ((_filter[slot >> 6] & (1UL << (int)(slot & 63))) == 0) return false;
        }
        return true;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
