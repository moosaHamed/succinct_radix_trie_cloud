namespace Succinct;

/// <summary>
/// Fixed-width bit packing over a flat <see cref="ulong"/> array. A value may
/// straddle two words. Shared by <see cref="PackedUIntArray"/> and its builder,
/// so the read and write halves can never disagree about the layout.
/// </summary>
internal static class BitPacking
{
    /// <summary>Words needed to hold <paramref name="count"/> values of <paramref name="bitsPerValue"/> bits.</summary>
    public static int WordCount(int count, int bitsPerValue) =>
        // long math: count * bits overflows int near 67M values of 32 bits; checked narrows back safely
        checked((int)(((long)count * bitsPerValue + 63) >> 6));

    /// <summary>Reads value <paramref name="index"/>. The caller has already validated the index.</summary>
    public static ulong Read(ulong[] data, int index, int bitsPerValue)
    {
        // Zero width means every value is 0, and no word may be read
        if (bitsPerValue == 0)
        {
            return 0;
        }
        // Absolute bit position, in long math for the same overflow reason as WordCount
        long bitPos = (long)index * bitsPerValue;
        // Word holding the value's lowest bit
        int word = (int)(bitPos >> 6);
        // Bit offset of the value inside that word
        int offset = (int)(bitPos & 63);
        // Low part: whatever of the value lives in this word
        ulong value = data[word] >> offset;
        // Bits this word could contribute
        int bitsFromFirstWord = 64 - offset;
        // High part: only when the value genuinely crosses into the next word (offset > 0 here, so the shift is 1..63)
        if (bitsFromFirstWord < bitsPerValue)
        {
            value |= data[word + 1] << bitsFromFirstWord;
        }
        // C# masks shift counts to 6 bits, so 1UL << 64 would be 1, not 0: the full-width case skips the mask
        return bitsPerValue < 64 ? value & ((1UL << bitsPerValue) - 1) : value;
    }

    /// <summary>Writes value <paramref name="index"/>. The caller has validated the index and that the value fits.</summary>
    public static void Write(ulong[] data, int index, int bitsPerValue, ulong value)
    {
        // Zero width stores nothing; the caller has already proven the value is 0
        if (bitsPerValue == 0)
        {
            return;
        }
        // Same addressing as Read
        long bitPos = (long)index * bitsPerValue;
        int word = (int)(bitPos >> 6);
        int offset = (int)(bitPos & 63);
        // Mask of the value's width, again avoiding the masked 64-bit shift
        ulong mask = bitsPerValue < 64 ? (1UL << bitsPerValue) - 1 : ulong.MaxValue;
        // Clear this value's bits, then OR the new ones in; neighbours stay untouched
        data[word] = (data[word] & ~(mask << offset)) | (value << offset);
        // Bits this word could hold
        int bitsFromFirstWord = 64 - offset;
        // Spill the high part into the next word when the value straddles the boundary
        if (bitsFromFirstWord < bitsPerValue)
        {
            data[word + 1] = (data[word + 1] & ~(mask >> bitsFromFirstWord)) | (value >> bitsFromFirstWord);
        }
    }
}
