using System.Collections.Immutable;

namespace Succinct.Catalog;

/// <summary>
/// One immutable segment of the schemaless store: a sparse Entity-Attribute-Value
/// matrix in CSR form. Per row, a sorted run of (attribute id, tag, slot) triples;
/// values live in typed columns, so a 6-field record and a 25-field record each
/// pay for exactly the attributes they have.
/// </summary>
/// <remarks>
/// OWNERSHIP (Category 4): never mutated after construction; every field is
/// private and readonly, every read path returns a value or a read-only span,
/// and no constructor argument is reachable by anyone else. "Mutation" means a
/// new segment merged into a new <see cref="CatalogSnapshot"/>, never a write here.
/// </remarks>
public sealed class ProductStore
{
    // Rows at or below this length use a vectorized linear scan; longer rows use binary search (measure, then tune)
    private const int LinearSearchThreshold = 32;

    // Stable key per row: the identity, kept beside the position (findings F6, F12)
    private readonly ProductKey[] _keys;
    // CSR row pointer: row r spans [_rowStart[r], _rowStart[r + 1]); EntityCount + 1 entries
    private readonly int[] _rowStart;
    // Ascending within each row
    private readonly int[] _attributeIds;
    // Parallel to _attributeIds
    private readonly ValueTag[] _tags;
    // Parallel; index into the tag's own column
    private readonly int[] _slots;
    // Typed columns: the tagged union's payload side, no object[] and no boxing
    private readonly uint[] _uint32Values;
    private readonly long[] _int64Values;
    private readonly float[] _float32Values;
    private readonly double[] _float64Values;
    // Deduplicated categoricals: id -> full text (finding F2 replaced a trie misread as id -> fragment)
    private readonly StringPool _categoricals;
    // Free text, one entry per stored value
    private readonly StringPool _freeText;

    // Internal: only ProductStoreBuilder produces a consistent set of arrays, and it hands over sole ownership
    internal ProductStore(
        AttributeCatalog catalog,
        ProductKey[] keys,
        int[] rowStart,
        int[] attributeIds,
        ValueTag[] tags,
        int[] slots,
        uint[] uint32Values,
        long[] int64Values,
        float[] float32Values,
        double[] float64Values,
        StringPool categoricals,
        StringPool freeText)
    {
        Catalog = catalog;
        _keys = keys;
        _rowStart = rowStart;
        _attributeIds = attributeIds;
        _tags = tags;
        _slots = slots;
        _uint32Values = uint32Values;
        _int64Values = int64Values;
        _float32Values = float32Values;
        _float64Values = float64Values;
        _categoricals = categoricals;
        _freeText = freeText;
    }

    /// <summary>The vocabulary this segment's attribute ids refer to.</summary>
    public AttributeCatalog Catalog { get; }

    /// <summary>Number of rows; derived, so it cannot disagree with the arrays.</summary>
    public int EntityCount => _keys.Length;

    /// <summary>Stable key of <paramref name="row"/>.</summary>
    public ProductKey KeyAt(int row)
    {
        // Row ids are dense in [0, EntityCount)
        ThrowIfInvalidRow(row);
        return _keys[row];
    }

    /// <summary>Zero-allocation view of one row, bounds-checked against that row.</summary>
    public AttributeRow AttributesOf(int row)
    {
        // EntityCount is a valid CSR sentinel internally, never a valid caller argument
        ThrowIfInvalidRow(row);
        int start = _rowStart[row];
        int length = _rowStart[row + 1] - start;
        // Three slices of one range: no copy, no allocation
        return new AttributeRow(
            _attributeIds.AsSpan(start, length),
            _tags.AsSpan(start, length),
            _slots.AsSpan(start, length));
    }

    /// <summary>Tag of an attribute, or null when the row does not have it.</summary>
    public ValueTag? TagOf(int row, int attributeId)
    {
        int index = FindSlotIndex(row, attributeId);
        // Nullable rather than a sentinel: absent and present are distinct at the type level
        return index >= 0 ? _tags[index] : null;
    }

    /// <summary>Reads any value kind; text values allocate their string.</summary>
    public bool TryGetValue(int row, int attributeId, out AttributeValue value)
    {
        int index = FindSlotIndex(row, attributeId);
        if (index < 0)
        {
            value = default;
            return false;
        }
        ValueTag tag = _tags[index];
        int slot = _slots[index];
        // The stored values were validated on the way in, so they are rebuilt without re-validation
        value = tag switch
        {
            ValueTag.UInt32 => new AttributeValue(tag, _uint32Values[slot], null),
            ValueTag.Int64 => new AttributeValue(tag, _int64Values[slot], null),
            ValueTag.Float32 => new AttributeValue(tag, BitConverter.SingleToInt32Bits(_float32Values[slot]), null),
            ValueTag.Float64 => new AttributeValue(tag, BitConverter.DoubleToInt64Bits(_float64Values[slot]), null),
            ValueTag.InternedString => new AttributeValue(tag, 0, _categoricals.GetString(new StringId(slot))),
            ValueTag.FreeText => new AttributeValue(tag, 0, _freeText.GetString(new StringId(slot))),
            // A stored Unset or unknown tag is a builder bug, never user input
            _ => throw new InvalidOperationException("Stored tag is not a value kind."),
        };
        return true;
    }

    /// <summary>Allocation-free typed read; false when absent or of another kind.</summary>
    public bool TryGetUInt32(int row, int attributeId, out uint value) =>
        TryGetTyped(row, attributeId, ValueTag.UInt32, _uint32Values, out value);

    /// <summary>Allocation-free typed read; false when absent or of another kind.</summary>
    public bool TryGetInt64(int row, int attributeId, out long value) =>
        TryGetTyped(row, attributeId, ValueTag.Int64, _int64Values, out value);

    /// <summary>Allocation-free typed read; false when absent or of another kind.</summary>
    public bool TryGetFloat32(int row, int attributeId, out float value) =>
        TryGetTyped(row, attributeId, ValueTag.Float32, _float32Values, out value);

    /// <summary>Allocation-free typed read; false when absent or of another kind.</summary>
    public bool TryGetFloat64(int row, int attributeId, out double value) =>
        TryGetTyped(row, attributeId, ValueTag.Float64, _float64Values, out value);

    /// <summary>Zero-allocation view of either text kind; the full value, never a fragment.</summary>
    public bool TryGetText(int row, int attributeId, out ReadOnlySpan<char> value)
    {
        int index = FindSlotIndex(row, attributeId);
        if (index >= 0)
        {
            // Each text kind has its own pool; the slot is an id in that pool
            switch (_tags[index])
            {
                case ValueTag.InternedString:
                    value = _categoricals[new StringId(_slots[index])];
                    return true;
                case ValueTag.FreeText:
                    value = _freeText[new StringId(_slots[index])];
                    return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>The row as typed inputs, for compaction and rendering. No boxing.</summary>
    public ImmutableArray<ProductAttributeInput> ToInputs(int row)
    {
        AttributeRow view = AttributesOf(row);
        // Exact-size builder: MoveToImmutable hands its array over without a copy
        ImmutableArray<ProductAttributeInput>.Builder inputs = ImmutableArray.CreateBuilder<ProductAttributeInput>(view.Length);
        for (int i = 0; i < view.Length; i++)
        {
            int attributeId = view.AttributeIds[i];
            // Catalog paths were validated when the catalog was built
            AttributePath path = AttributePath.FromValidated(Catalog.PathOf(attributeId));
            // Present by construction: the id came from this very row
            TryGetValue(row, attributeId, out AttributeValue value);
            inputs.Add(new ProductAttributeInput(path, value));
        }
        return inputs.MoveToImmutable();
    }

    // One generic body, so the tag check cannot be forgotten in one overload; T is a value type, so the JIT specializes it
    private bool TryGetTyped<T>(int row, int attributeId, ValueTag expected, T[] column, out T value)
        where T : struct
    {
        int index = FindSlotIndex(row, attributeId);
        // Tag must match: a float read of an Int64 slot returns false instead of reinterpreting bits
        if (index >= 0 && _tags[index] == expected)
        {
            value = column[_slots[index]];
            return true;
        }
        value = default;
        return false;
    }

    private int FindSlotIndex(int row, int attributeId)
    {
        // Validates the row, then searches only its own ids
        ReadOnlySpan<int> ids = AttributesOf(row).AttributeIds;
        // Short rows: MemoryExtensions.IndexOf is Vector128/256/512-accelerated; long rows: binary search on sorted ids
        int local = ids.Length <= LinearSearchThreshold ? ids.IndexOf(attributeId) : ids.BinarySearch(attributeId);
        // Translate the row-relative hit back to a column index
        return local < 0 ? -1 : _rowStart[row] + local;
    }

    private void ThrowIfInvalidRow(int row)
    {
        // One unsigned compare covers negatives and row >= EntityCount
        if ((uint)row >= (uint)_keys.Length)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(row));
        }
    }
}
