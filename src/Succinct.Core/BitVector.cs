using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Succinct;

/// <summary>
/// Immutable bit vector with O(1) rank and sampled select over a flat
/// <see cref="ulong"/> array. Select costs O(log w), where w is the number of
/// words one sample window spans (about 16 for LOUDS-shaped data); it is not
/// worst-case O(1) on very sparse vectors, and this comment says so.
/// </summary>
/// <remarks>
/// Memory: 1 bit per payload bit, plus a 32-bit rank entry per 64-bit word
/// (+0.5 bits per bit) and two select samples per 512 matching bits (about
/// +0.06 bits per bit), so roughly 1.56 bits per payload bit in total.
/// </remarks>
public sealed class BitVector
{
    // One sample per 512 matching bits bounds every select's search window to a few words
    private const int SampleRate = 512;

    // Private copy or ownership-transferred array: no caller can mutate bits after the caches below were derived (F7)
    private readonly ulong[] _words;
    // Logical length; every bit at or past it is padding, never data
    private readonly int _length;
    // _rank1Cache[w] is the number of 1-bits strictly before word w, padding included for the final word
    private readonly int[] _rank1Cache;
    // Real 1-bit total, computed by masked rank so padding can never leak into it (F1)
    private readonly int _totalOnes;
    // Position of every SampleRate-th 1-bit; clamps Select1's search window on both sides (F16)
    private readonly int[] _onesSample;
    // Position of every SampleRate-th 0-bit; clamps Select0's search window on both sides (F16)
    private readonly int[] _zerosSample;

    /// <summary>Copies <paramref name="words"/>; later changes to the caller's buffer cannot reach this vector.</summary>
    public BitVector(ReadOnlySpan<ulong> words, int length)
        // Validate the shape first, then copy: the copy is unavoidable and the contract is in the signature
        : this(CopyValidated(words, length), length)
    {
    }

    // Takes ownership of an array nobody else references; only reachable through the copy or WrapOwned
    private BitVector(ulong[] ownedWords, int length)
    {
        // Ownership is now exclusive to this instance
        _words = ownedWords;
        _length = length;

        // Bits used in the final word; 0 means the final word is full and needs no padding
        int tailBits = length & 63;
        // Padding 1s keep Select0's whole-word zero arithmetic from counting slack as zeros
        if (tailBits != 0)
        {
            _words[^1] |= ulong.MaxValue << tailBits;
        }

        // One extra slot so word w's prefix count is always _rank1Cache[w], including w == word count
        _rank1Cache = new int[_words.Length + 1];
        for (int w = 0; w < _words.Length; w++)
        {
            // POPCNT intrinsic: one instruction per 64 bits
            _rank1Cache[w + 1] = _rank1Cache[w] + BitOperations.PopCount(_words[w]);
        }

        // THE F1 FIX: Rank1 masks the partial final word, so padding is excluded by construction
        _totalOnes = Rank1(length);
        // Samples are built from real bits only
        _onesSample = BuildSample(ones: true);
        _zerosSample = BuildSample(ones: false);
    }

    /// <summary>Ownership transfer for builders that just allocated <paramref name="words"/> and drop it afterwards.</summary>
    internal static BitVector WrapOwned(ulong[] words, int length)
    {
        // Same shape rule as the public constructor
        ValidateShape(words.Length, length);
        // Identity conversion selects the private owning constructor: no copy
        return new BitVector(words, length);
    }

    /// <summary>Number of logical bits.</summary>
    public int Length => _length;

    /// <summary>Number of 1-bits in the logical range; padding never counts.</summary>
    public int TotalOnes => _totalOnes;

    /// <summary>Number of 0-bits in the logical range; derived from the corrected total, so it cannot go negative.</summary>
    public int TotalZeros => _length - _totalOnes;

    /// <summary>The bit at <paramref name="pos"/>.</summary>
    public bool this[int pos]
    {
        get
        {
            // One unsigned compare rejects negatives and every padding position (F10)
            if ((uint)pos >= (uint)_length)
            {
                ThrowHelper.ThrowArgumentOutOfRange(nameof(pos));
            }
            // Word index and bit test; the array access stays CLR-bounds-checked
            return (_words[pos >> 6] & (1UL << (pos & 63))) != 0;
        }
    }

    /// <summary>Number of 1-bits in [0, <paramref name="pos"/>); <paramref name="pos"/> == <see cref="Length"/> is legal.</summary>
    public int Rank1(int pos)
    {
        // Strictly greater: pos == length asks for the whole vector
        if ((uint)pos > (uint)_length)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(pos));
        }
        // Whole words before pos are pre-summed
        int word = pos >> 6;
        // Bits of the partial word that lie below pos
        int bit = pos & 63;
        // bit == 0 needs no partial word, and avoids reading past the last word when pos == 64 * word count
        return bit == 0
            ? _rank1Cache[word]
            : _rank1Cache[word] + BitOperations.PopCount(_words[word] & ((1UL << bit) - 1));
    }

    /// <summary>Number of 0-bits in [0, <paramref name="pos"/>).</summary>
    public int Rank0(int pos) =>
        // Derived, so only one cache is stored; Rank1 validates pos
        pos - Rank1(pos);

    /// <summary>0-based position of the k-th 1-bit; <paramref name="k"/> is 1-based.</summary>
    public int Select1(int k)
    {
        // One compare covers k < 1 and k > TotalOnes
        if ((uint)(k - 1) >= (uint)_totalOnes)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(k));
        }
        // Sample group g covers ranks (g * S, (g + 1) * S]
        int group = (k - 1) / SampleRate;
        // Lower bound: the word holding the previous sample's bit, whose rank is at most k
        int lo = group == 0 ? 0 : _onesSample[group - 1] >> 6;
        // Upper bound: the word holding this group's sample, whose rank is at least k
        int hi = group < _onesSample.Length ? _onesSample[group] >> 6 : _words.Length - 1;
        // First word in [lo, hi] whose inclusive prefix count reaches k
        while (lo < hi)
        {
            // Overflow-free midpoint
            int mid = lo + ((hi - lo) >> 1);
            // Ones through word mid are _rank1Cache[mid + 1]
            if (_rank1Cache[mid + 1] < k)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        // Rank inside the word is 0-based
        return (lo << 6) + SelectInWord(_words[lo], k - _rank1Cache[lo] - 1);
    }

    /// <summary>0-based position of the k-th 0-bit; <paramref name="k"/> is 1-based.</summary>
    public int Select0(int k)
    {
        // Uses the corrected TotalZeros, so every real zero is reachable (F1)
        if ((uint)(k - 1) >= (uint)TotalZeros)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(k));
        }
        int group = (k - 1) / SampleRate;
        int lo = group == 0 ? 0 : _zerosSample[group - 1] >> 6;
        int hi = group < _zerosSample.Length ? _zerosSample[group] >> 6 : _words.Length - 1;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            // Zeros through word mid = bits through mid minus ones through mid; padding bits are 1s, so they cancel out
            long zerosThroughMid = ((long)(mid + 1) << 6) - _rank1Cache[mid + 1];
            if (zerosThroughMid < k)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        // Zeros strictly before word lo, by the same identity
        int zerosBefore = (int)(((long)lo << 6) - _rank1Cache[lo]);
        // Complement turns "k-th zero" into "k-th one"; padding 1s become 0s and are never selected
        return (lo << 6) + SelectInWord(~_words[lo], k - zerosBefore - 1);
    }

    /// <summary>First 1-bit at or after <paramref name="pos"/>, or -1. Usually one TZCNT.</summary>
    public int NextSetBit(int pos) =>
        // Shared scan with the polarity as a JIT-foldable flag
        NextBit(pos, invert: false);

    /// <summary>First 0-bit at or after <paramref name="pos"/>, or -1. Usually one TZCNT.</summary>
    public int NextClearBit(int pos) =>
        // Same scan over the complemented words
        NextBit(pos, invert: true);

    private int NextBit(int pos, bool invert)
    {
        // pos == length is legal and simply finds nothing
        if ((uint)pos > (uint)_length)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(pos));
        }
        int word = pos >> 6;
        // pos == length on a word boundary: nothing left to scan
        if (word >= _words.Length)
        {
            return -1;
        }
        // Drop bits below pos inside the first word
        ulong bits = (invert ? ~_words[word] : _words[word]) & (ulong.MaxValue << (pos & 63));
        while (true)
        {
            if (bits != 0)
            {
                // TZCNT finds the lowest matching bit in one instruction
                int found = (word << 6) + BitOperations.TrailingZeroCount(bits);
                // A padding 1 sits at or past the logical length: it is not data
                return found < _length ? found : -1;
            }
            // Advance a whole word at a time
            word++;
            if (word == _words.Length)
            {
                return -1;
            }
            bits = invert ? ~_words[word] : _words[word];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SelectInWord(ulong word, int rank)
    {
        // Internal invariant: the caller has already proven the rank exists in this word
        Debug.Assert((uint)rank < (uint)BitOperations.PopCount(word));
        // IsSupported is a JIT-time constant, so the untaken branch is removed from the machine code
        if (Bmi2.X64.IsSupported)
        {
            // PDEP deposits a lone 1 onto the rank-th set bit of word; TZCNT reads its position
            return BitOperations.TrailingZeroCount(Bmi2.X64.ParallelBitDeposit(1UL << rank, word));
        }
        // Portable fallback (ARM64, older x64, or DOTNET_EnableBMI2=0): clear the lowest set bit rank times
        for (int i = 0; i < rank; i++)
        {
            word &= word - 1;
        }
        // The wanted bit is now the lowest set bit
        return BitOperations.TrailingZeroCount(word);
    }

    private int[] BuildSample(bool ones)
    {
        // Exact size: one entry per complete group of SampleRate matching bits
        int[] samples = new int[(ones ? _totalOnes : TotalZeros) / SampleRate];
        // Mask of real bits in the final word; a full final word keeps every bit
        ulong tailMask = (_length & 63) == 0 ? ulong.MaxValue : (1UL << (_length & 63)) - 1;
        // Matching bits seen so far, and the next sample slot to fill
        int seen = 0;
        int next = 0;
        for (int w = 0; w < _words.Length && next < samples.Length; w++)
        {
            // Padding never counts as a one or a zero
            ulong valid = w == _words.Length - 1 ? tailMask : ulong.MaxValue;
            // Complement for zeros, then drop padding
            ulong bits = (ones ? _words[w] : ~_words[w]) & valid;
            // Walk only matching bits: cost is O(matching bits), not O(64)
            while (bits != 0)
            {
                // Every SampleRate-th matching bit is recorded
                seen++;
                if (seen % SampleRate == 0)
                {
                    samples[next++] = (w << 6) + BitOperations.TrailingZeroCount(bits);
                }
                // Clear the lowest set bit
                bits &= bits - 1;
            }
        }
        return samples;
    }

    private static ulong[] CopyValidated(ReadOnlySpan<ulong> words, int length)
    {
        // Shape first, so a bad call fails before allocating
        ValidateShape(words.Length, length);
        // The defensive copy that makes this instance immune to caller mutation
        return words.ToArray();
    }

    private static void ValidateShape(int wordCount, int length)
    {
        // A negative length is a caller bug; fail before it reaches any arithmetic
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        // ceil(length / 64) in long math, so a length near int.MaxValue cannot wrap
        if (wordCount != (((long)length + 63) >> 6))
        {
            ThrowHelper.ThrowArgument("words", "Word count must equal ceil(length / 64).");
        }
    }
}
