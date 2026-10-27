using System.Collections.Immutable;

namespace Succinct.Catalog;

/// <summary>
/// Builds one immutable <see cref="ProductStore"/> segment from a batch of
/// upserts. Pass 1 validates every record and discovers new paths; pass 2 fills
/// the typed CSR columns. All lists here are build-phase scratch (Category 3)
/// that become frozen arrays and then garbage.
/// </summary>
public static class ProductStoreBuilder
{
    /// <summary>Per-record cap: the schemaless counterpart to "the schema has at most N fields".</summary>
    public const int MaxAttributesPerRecord = 512;

    /// <summary>
    /// Builds a segment. Attribute ids extend <paramref name="baseCatalog"/>, so
    /// every id it already issued keeps its meaning in the new segment.
    /// </summary>
    public static ProductStore Build(ReadOnlySpan<ProductUpsert> upserts, AttributeCatalog? baseCatalog = null)
    {
        // --- Pass 1: validate every record, discover the vocabulary ---
        // A key may appear once per batch; a conflict is rejected, never silently resolved
        var seenKeys = new HashSet<ProductKey>(upserts.Length);
        // Every path used in this batch, for the catalog extension
        var discovered = new HashSet<AttributePath>();
        // long accumulator for the total attribute count, narrowed with a check below
        long totalAttributes = 0;
        for (int i = 0; i < upserts.Length; i++)
        {
            ProductUpsert upsert = upserts[i];
            if (!seenKeys.Add(upsert.Key))
            {
                ThrowHelper.ThrowArgument(nameof(upserts), "A key appears more than once in the batch.");
            }
            // Shape rules per record: caps, duplicates, leaf-versus-group collisions
            ValidateRecord(upsert.Attributes);
            foreach (ProductAttributeInput input in upsert.Attributes)
            {
                discovered.Add(input.Path);
            }
            totalAttributes += upsert.Attributes.Length;
        }

        // Append-only extension: stable ids, cumulative cap
        AttributeCatalog catalog = AttributeCatalog.Extend(baseCatalog, discovered);

        // --- Pass 2: fill the typed CSR columns ---
        // Exact sizes: overflow-checked narrowing of the accumulated count
        int attributeCount = checked((int)totalAttributes);
        var keys = new ProductKey[upserts.Length];
        int[] rowStart = new int[upserts.Length + 1];
        int[] attributeIds = new int[attributeCount];
        var tags = new ValueTag[attributeCount];
        int[] slots = new int[attributeCount];
        var columns = new Columns();
        // One reusable scratch row, cleared per record
        var row = new List<(int AttributeId, AttributeValue Value)>();
        int cursor = 0;
        for (int r = 0; r < upserts.Length; r++)
        {
            keys[r] = upserts[r].Key;
            rowStart[r] = cursor;
            row.Clear();
            foreach (ProductAttributeInput input in upserts[r].Attributes)
            {
                // The catalog was extended from exactly this data, so a miss is a broken invariant
                if (!catalog.TryGetId(input.Path, out int attributeId))
                {
                    ThrowHelper.ThrowInvalidOperation("Catalog is missing a discovered path.");
                }
                row.Add((attributeId, input.Value));
            }
            // Ascending ids make the row searchable by IndexOf or BinarySearch
            row.Sort(static (a, b) => a.AttributeId.CompareTo(b.AttributeId));
            foreach ((int attributeId, AttributeValue value) in row)
            {
                attributeIds[cursor] = attributeId;
                tags[cursor] = value.Tag;
                // Each kind lands in its own column; the slot is its index there
                slots[cursor] = columns.Append(value);
                cursor++;
            }
        }
        // CSR sentinel: row r spans [rowStart[r], rowStart[r + 1]) with no special case
        rowStart[^1] = cursor;

        // ToArray and Freeze end the build phase: from here the segment is immutable
        return new ProductStore(
            catalog,
            keys,
            rowStart,
            attributeIds,
            tags,
            slots,
            [.. columns.UInt32s],
            [.. columns.Int64s],
            [.. columns.Float32s],
            [.. columns.Float64s],
            columns.Categoricals.Freeze(),
            columns.FreeText.Freeze());
    }

    private static void ValidateRecord(ImmutableArray<ProductAttributeInput> attributes)
    {
        // default(ImmutableArray) is a distinct, invalid state; reject it before touching it
        if (attributes.IsDefault)
        {
            ThrowHelper.ThrowArgument(nameof(attributes), "Attributes must be initialized.");
        }
        // Cap before iterating: refuse early, cheaply
        if (attributes.Length > MaxAttributesPerRecord)
        {
            ThrowHelper.ThrowArgument(nameof(attributes), "Record exceeds the per-record attribute cap.");
        }
        string[] paths = new string[attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
        {
            // An unset value has no column to live in
            if (attributes[i].Value.IsUnset)
            {
                ThrowHelper.ThrowArgument(nameof(attributes), "Attribute value is unset.");
            }
            // Value throws for default(AttributePath), so an unvalidated path cannot slip through
            paths[i] = attributes[i].Path.Value;
        }
        // Ordinal sort; '.' sorts below every other allowed path character
        Array.Sort(paths, StringComparer.Ordinal);
        for (int i = 1; i < paths.Length; i++)
        {
            string previous = paths[i - 1];
            string current = paths[i];
            // A duplicate path within one record is rejected rather than silently resolved
            if (string.Equals(previous, current, StringComparison.Ordinal))
            {
                ThrowHelper.ThrowArgument(nameof(attributes), "A path appears more than once in the record.");
            }
            // F14: because '.' sorts lowest, any descendant of a path immediately follows it, so one adjacent pass is complete
            if (current.Length > previous.Length
                && current.StartsWith(previous, StringComparison.Ordinal)
                && current[previous.Length] == '.')
            {
                ThrowHelper.ThrowArgument(nameof(attributes), "A path is used both as a value and as a group.");
            }
        }
    }

    /// <summary>Build-phase typed columns.</summary>
    private sealed class Columns
    {
        public List<uint> UInt32s { get; } = [];

        public List<long> Int64s { get; } = [];

        public List<float> Float32s { get; } = [];

        public List<double> Float64s { get; } = [];

        // Equal categoricals share one id: the deduplication the original design promised
        public StringPool.Builder Categoricals { get; } = new(deduplicate: true);

        // Free text is unique per row in practice, so a dedup dictionary would only cost memory
        public StringPool.Builder FreeText { get; } = new(deduplicate: false);

        public int Append(AttributeValue value)
        {
            // The tag decides the column; every kind is handled, Unset was rejected in pass 1
            switch (value.Tag)
            {
                case ValueTag.UInt32:
                    UInt32s.Add(value.UInt32Payload);
                    return UInt32s.Count - 1;
                case ValueTag.Int64:
                    Int64s.Add(value.Int64Payload);
                    return Int64s.Count - 1;
                case ValueTag.Float32:
                    Float32s.Add(value.Float32Payload);
                    return Float32s.Count - 1;
                case ValueTag.Float64:
                    Float64s.Add(value.Float64Payload);
                    return Float64s.Count - 1;
                case ValueTag.InternedString:
                    return Categoricals.Add(value.TextPayload).Value;
                case ValueTag.FreeText:
                    return FreeText.Add(value.TextPayload).Value;
                default:
                    throw new InvalidOperationException("Value tag has no column.");
            }
        }
    }
}
