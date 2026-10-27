using System.Numerics;

namespace Succinct;

/// <summary>
/// Elias-Fano encoding of a non-decreasing sequence of unsigned integers:
/// about 2 + floor(log2(U / n)) bits per element instead of 64. Each value
/// splits into low bits, packed at their true width, and high bits, written
/// in unary into a <see cref="BitVector"/>. Decoding is one sampled select.
/// </summary>
public sealed class EliasFano
{
    // Unary high parts: element i sets bit (high_i + i), strictly increasing because the input is sorted
    private readonly BitVector _highBits;
    // Low parts at exactly _lowBitCount bits each
    private readonly PackedUIntArray _lowBits;
    // l = floor(log2(max / n)), the split that minimizes total size
    private readonly int _lowBitCount;

    /// <summary>Encodes <paramref name="sortedValues"/>, which must be non-decreasing; the span is read, never kept.</summary>
    public EliasFano(ReadOnlySpan<ulong> sortedValues)
    {
        // The encoding's only precondition is checked, not trusted: one O(n) pass at the boundary (F7)
        for (int i = 1; i < sortedValues.Length; i++)
        {
            // A descent would OR two values into one high-bit slot and decode both wrong, silently
            if (sortedValues[i] < sortedValues[i - 1])
            {
                ThrowHelper.ThrowArgument_NotNonDecreasing(nameof(sortedValues), i);
            }
        }

        Length = sortedValues.Length;
        // The last value is the largest, now provably
        ulong max = Length == 0 ? 0 : sortedValues[^1];
        // floor(log2(max / n)) in integer math: LZCNT, exact for every ulong, no double rounding; Log2(0) is 0
        _lowBitCount = Length == 0 ? 0 : BitOperations.Log2(max / (ulong)Length);
        // Low-half mask; (1UL << 0) - 1 = 0 handles l = 0 without a special case, and l never exceeds 63
        ulong lowMask = (1UL << _lowBitCount) - 1;
        // n + (max >> l) + 1 bits; checked narrows the ulong high part and rejects inputs outside this type's range
        int highLength = checked(Length + (int)(max >> _lowBitCount) + 1);

        // Build-phase writer, frozen below
        var lowBuilder = new PackedUIntArray.Builder(Length, _lowBitCount);
        // Raw high-bit words, zero-initialized; ownership moves to the BitVector below
        ulong[] highWords = new ulong[(highLength + 63) >> 6];
        for (int i = 0; i < Length; i++)
        {
            ulong v = sortedValues[i];
            // Low half goes to the packed array at its true width
            lowBuilder[i] = v & lowMask;
            // (v >> l) + i is strictly increasing for sorted input, so each bit is set exactly once and stays below highLength
            int bitIndex = (int)(v >> _lowBitCount) + i;
            highWords[bitIndex >> 6] |= 1UL << (bitIndex & 63);
        }

        // Category 3 becomes Category 4
        _lowBits = lowBuilder.Freeze();
        // highWords is never touched again, so ownership can transfer without a copy
        _highBits = BitVector.WrapOwned(highWords, highLength);
    }

    /// <summary>Number of encoded elements.</summary>
    public int Length { get; }

    /// <summary>Decodes element <paramref name="index"/>.</summary>
    public ulong this[int index]
    {
        get
        {
            // Explicit: Select1 alone would accept ranks that exist but belong to no element
            if ((uint)index >= (uint)Length)
            {
                ThrowHelper.ThrowArgumentOutOfRange(nameof(index));
            }
            // Undo the "+ index" to recover the high half, then splice the low half back in
            ulong high = (ulong)(_highBits.Select1(index + 1) - index);
            return (high << _lowBitCount) | _lowBits[index];
        }
    }

    /// <summary>Decodes elements <paramref name="index"/> and <paramref name="index"/> + 1 with one select instead of two.</summary>
    public (ulong Start, ulong End) GetAdjacent(int index)
    {
        // Two comparisons on purpose: the (uint) idiom breaks when the bound itself can be -1 (Length == 0)
        if (index < 0 || index >= Length - 1)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(index));
        }
        // One sampled select for the first element
        int pos = _highBits.Select1(index + 1);
        // The next element's 1-bit is simply the next set bit: usually one TZCNT in the same word
        int nextPos = _highBits.NextSetBit(pos + 1);
        // Same decode rule, each element with its own index
        ulong start = ((ulong)(pos - index) << _lowBitCount) | _lowBits[index];
        ulong end = ((ulong)(nextPos - index - 1) << _lowBitCount) | _lowBits[index + 1];
        return (start, end);
    }
}
