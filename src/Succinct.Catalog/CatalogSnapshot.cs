using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Succinct.Catalog;

/// <summary>
/// One immutable generation of the catalog: frozen segments, an index from
/// stable <see cref="ProductKey"/> to where each live record sits, and one
/// append-only <see cref="AttributeCatalog"/> whose ids are valid in every
/// segment. Created only by <see cref="CatalogMerge"/>.
/// </summary>
public sealed class CatalogSnapshot : IVersioned
{
    // Keys in order, derived once per generation on first use; Lazy publishes it safely to every reader
    private readonly Lazy<ProductKey[]> _orderedKeys;

    // Internal: CatalogMerge is the only producer, which is what keeps every segment's catalog a prefix of Catalog
    internal CatalogSnapshot(
        long version,
        ImmutableArray<ProductStore> segments,
        ImmutableDictionary<ProductKey, RecordLocator> index,
        AttributeCatalog catalog,
        int deadRows)
    {
        Version = version;
        Segments = segments;
        Index = index;
        Catalog = catalog;
        DeadRows = deadRows;
        // ExecutionAndPublication: concurrent first readers build the array once
        _orderedKeys = new Lazy<ProductKey[]>(BuildOrderedKeys, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Generation 0: the first ingest runs the same merge as every later one.</summary>
    public static CatalogSnapshot Empty { get; } =
        new(0, [], ImmutableDictionary<ProductKey, RecordLocator>.Empty, AttributeCatalog.Empty, 0);

    /// <inheritdoc />
    public long Version { get; }

    /// <summary>Frozen segments, oldest first; never edited after publication.</summary>
    public ImmutableArray<ProductStore> Segments { get; }

    /// <summary>Stable key to physical row for THIS generation, structurally shared with the previous one.</summary>
    public ImmutableDictionary<ProductKey, RecordLocator> Index { get; }

    /// <summary>Cumulative vocabulary; every segment's catalog is a prefix of it, so its ids are valid everywhere.</summary>
    public AttributeCatalog Catalog { get; }

    /// <summary>Rows still stored but shadowed by a later upsert or removed by a delete; drives compaction.</summary>
    public int DeadRows { get; }

    /// <summary>Number of live records.</summary>
    public int LiveRows => Index.Count;

    /// <summary>True when <paramref name="key"/> is live in this generation.</summary>
    public bool Contains(ProductKey key) => Index.ContainsKey(key);

    /// <summary>Where <paramref name="key"/> lives in this generation.</summary>
    public bool TryLocate(ProductKey key, [NotNullWhen(true)] out ProductStore? segment, out int row)
    {
        if (Index.TryGetValue(key, out RecordLocator at))
        {
            segment = Segments[at.Segment];
            row = at.Row;
            return true;
        }
        segment = null;
        row = -1;
        return false;
    }

    /// <summary>Reads one attribute of one product.</summary>
    public bool TryGet(ProductKey key, AttributePath path, out AttributeValue value)
    {
        // Path to id once, through the cumulative catalog; the id means the same path in every segment
        if (TryLocate(key, out ProductStore? segment, out int row) && Catalog.TryGetId(path, out int attributeId))
        {
            // Segments only ever extend the catalog they were built from
            Debug.Assert(segment.Catalog.Count <= Catalog.Count);
            return segment.TryGetValue(row, attributeId, out value);
        }
        value = default;
        return false;
    }

    /// <summary>The whole record of one product, as typed inputs.</summary>
    public bool TryGetRecord(ProductKey key, out ImmutableArray<ProductAttributeInput> attributes)
    {
        if (TryLocate(key, out ProductStore? segment, out int row))
        {
            attributes = segment.ToInputs(row);
            return true;
        }
        attributes = [];
        return false;
    }

    /// <summary>
    /// Keyset paging: up to <paramref name="take"/> live keys strictly after
    /// <paramref name="after"/>, in key order. Stable across generations,
    /// unlike skip/take over positions that shift when rows come and go.
    /// </summary>
    public ReadOnlySpan<ProductKey> Page(ProductKey? after, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        ProductKey[] keys = _orderedKeys.Value;
        int start = 0;
        if (after is ProductKey last)
        {
            // Exact hit: start after it; miss: the complement is the insertion point
            int found = Array.BinarySearch(keys, last);
            start = found >= 0 ? found + 1 : ~found;
        }
        int count = Math.Min(take, keys.Length - start);
        // A read-only view of this generation's own array: callers cannot reorder it
        return keys.AsSpan(start, count);
    }

    private ProductKey[] BuildOrderedKeys()
    {
        // ImmutableDictionary is unordered; sort a copy of its keys once per generation
        ProductKey[] keys = [.. Index.Keys];
        Array.Sort(keys);
        return keys;
    }
}
