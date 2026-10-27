namespace Succinct;

/// <summary>
/// Immutable fixed-width unsigned integers packed at their true bit width.
/// Elias-Fano's low halves need exactly floor(log2(U / n)) bits per value,
/// which is almost never 8, 16, 32 or 64. Built once by <see cref="Builder"/>,
/// then frozen: Category 3 becomes Category 4 exactly once.
/// </summary>
public sealed class PackedUIntArray
{
    // Owned outright: only Builder.Freeze hands an array in, and the builder drops its reference first
    private readonly ulong[] _data;

    // Private: the only way to create one is through Builder.Freeze
    private PackedUIntArray(ulong[] data, int length, int bitsPerValue)
    {
        _data = data;
        Length = length;
        BitsPerValue = bitsPerValue;
    }

    /// <summary>Number of logical values.</summary>
    public int Length { get; }

    /// <summary>Width of every value, 0 through 64.</summary>
    public int BitsPerValue { get; }

    /// <summary>Value at <paramref name="index"/>.</summary>
    public ulong this[int index]
    {
        get
        {
            // Logical bound, not storage bound: slack bits in the final word are never readable (F10)
            if ((uint)index >= (uint)Length)
            {
                ThrowHelper.ThrowArgumentOutOfRange(nameof(index));
            }
            // Straddling read shared with the builder
            return BitPacking.Read(_data, index, BitsPerValue);
        }
    }

    /// <summary>Single-owner writer that becomes an immutable reader exactly once.</summary>
    public sealed class Builder
    {
        // Nulled by Freeze: a write after publication throws instead of mutating shared state
        private ulong[]? _data;
        // Fixed at construction
        private readonly int _length;
        private readonly int _bitsPerValue;

        /// <summary>Allocates zeroed storage for <paramref name="length"/> values of <paramref name="bitsPerValue"/> bits.</summary>
        public Builder(int length, int bitsPerValue)
        {
            // A negative count is a caller bug
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            // The uint cast rejects negatives too; 0 is legal ("every value is 0") and 64 is a full ulong
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)bitsPerValue, 64u, nameof(bitsPerValue));
            _length = length;
            _bitsPerValue = bitsPerValue;
            // Size computed in long math and narrowed with a check
            _data = new ulong[BitPacking.WordCount(length, bitsPerValue)];
        }

        /// <summary>Writes value <paramref name="index"/>; a value wider than the declared width is rejected, not masked.</summary>
        public ulong this[int index]
        {
            set
            {
                // Writing after Freeze is a use-after-transfer bug
                ulong[] data = _data ?? throw new InvalidOperationException("Builder already frozen.");
                // Logical bound, as on the reader
                if ((uint)index >= (uint)_length)
                {
                    ThrowHelper.ThrowArgumentOutOfRange(nameof(index));
                }
                // Reject rather than mask: silent truncation is the same bug class as an unchecked narrowing cast
                if (_bitsPerValue < 64 && (value >> _bitsPerValue) != 0)
                {
                    ThrowHelper.ThrowArgumentOutOfRange(nameof(value));
                }
                // Straddling write shared with the reader
                BitPacking.Write(data, index, _bitsPerValue, value);
            }
        }

        /// <summary>Transfers the storage to an immutable <see cref="PackedUIntArray"/>; the builder is unusable afterwards.</summary>
        public PackedUIntArray Freeze()
        {
            // A second Freeze would hand the same array to two owners
            ulong[] data = _data ?? throw new InvalidOperationException("Builder already frozen.");
            // Ownership transfer: after this line only the frozen reader can reach the array
            _data = null;
            return new PackedUIntArray(data, _length, _bitsPerValue);
        }
    }
}
