using System.Collections.Immutable;

namespace Succinct.Catalog;

/// <summary>
/// Pure functions from (previous snapshot, delta) to the next snapshot. They
/// read only their arguments and write nothing shared, so they are safe to run
/// off to the side while readers keep using the previous generation, and safe
/// to re-run. Each upsert batch becomes one new immutable segment, so a merge
/// costs O(batch), not O(total) as in the original full rebuild (finding F11).
/// </summary>
public static class CatalogMerge
{
    /// <summary>Compact once shadowed rows exceed half the live ones...</summary>
    public const double DeadRowRatio = 0.5;

    /// <summary>...or once this many segments pile up and slow lookups.</summary>
    public const int MaxSegments = 16;

    /// <summary>Applies upserts and deletes; the result's version is the previous one plus one.</summary>
    public static CatalogSnapshot Apply(CatalogSnapshot previous, ProductDelta delta)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(delta);
        // default(ImmutableArray) is not "empty"; reject it rather than guess
        if (delta.Upserts.IsDefault || delta.Deletes.IsDefault)
        {
            ThrowHelper.ThrowArgument(nameof(delta), "Delta arrays must be initialized.");
        }
        // A key appears once per delta, as an upsert or a delete: conflicts are rejected, never silently resolved
        var seen = new HashSet<ProductKey>();
        foreach (ProductUpsert upsert in delta.Upserts)
        {
            if (!seen.Add(upsert.Key))
            {
                ThrowHelper.ThrowArgument(nameof(delta), "A key appears more than once in the delta.");
            }
        }
        foreach (ProductKey key in delta.Deletes)
        {
            if (!seen.Add(key))
            {
                ThrowHelper.ThrowArgument(nameof(delta), "A key is both upserted and deleted in the delta.");
            }
        }

        // Builder over the previous index: edits share structure, nothing is copied wholesale
        ImmutableDictionary<ProductKey, RecordLocator>.Builder index = previous.Index.ToBuilder();
        ImmutableArray<ProductStore> segments = previous.Segments;
        AttributeCatalog catalog = previous.Catalog;
        int dead = previous.DeadRows;

        if (!delta.Upserts.IsEmpty)
        {
            // The only data written this generation: O(batch); ids extend the snapshot's catalog
            ProductStore segment = ProductStoreBuilder.Build(delta.Upserts.AsSpan(), previous.Catalog);
            int segmentIndex = segments.Length;
            segments = segments.Add(segment);
            // The new segment's catalog extends the previous one, so it becomes the snapshot's catalog
            catalog = segment.Catalog;
            for (int row = 0; row < segment.EntityCount; row++)
            {
                ProductKey key = segment.KeyAt(row);
                // Replacing a key shadows its old row, which stays dead weight until compaction
                if (index.ContainsKey(key))
                {
                    dead++;
                }
                index[key] = new RecordLocator(segmentIndex, row);
            }
        }

        foreach (ProductKey key in delta.Deletes)
        {
            // Deleting an absent key is a documented no-op, not an error and not an ambiguity
            if (index.Remove(key))
            {
                dead++;
            }
        }

        // ToImmutable on a builder is O(1); the version advances by exactly one
        return new CatalogSnapshot(previous.Version + 1, segments, index.ToImmutable(), catalog, dead);
    }

    /// <summary>True when compaction would pay for itself.</summary>
    public static bool ShouldCompact(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Either too much dead weight, or too many segments to probe
        return snapshot.DeadRows > snapshot.LiveRows * DeadRowRatio
            || snapshot.Segments.Length > MaxSegments;
    }

    /// <summary>Folds every live row into one segment; dead rows become garbage with the old segments.</summary>
    public static CatalogSnapshot Compact(CatalogSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        // Live rows only, in key order, so the compacted segment is deterministic
        KeyValuePair<ProductKey, RecordLocator>[] live = [.. previous.Index.OrderBy(static pair => pair.Key)];
        var upserts = new ProductUpsert[live.Length];
        for (int i = 0; i < live.Length; i++)
        {
            (ProductKey key, RecordLocator at) = live[i];
            // Typed values copied straight across: no boxing, unlike the original ToInputs
            upserts[i] = new ProductUpsert(key, previous.Segments[at.Segment].ToInputs(at.Row));
        }
        // Same catalog in, same ids out: compaction never renumbers attributes
        ProductStore merged = ProductStoreBuilder.Build(upserts, previous.Catalog);
        ImmutableDictionary<ProductKey, RecordLocator>.Builder index =
            ImmutableDictionary.CreateBuilder<ProductKey, RecordLocator>();
        for (int row = 0; row < merged.EntityCount; row++)
        {
            index.Add(merged.KeyAt(row), new RecordLocator(0, row));
        }
        // Readers of the previous generation keep its segments alive until they let go; nothing is freed early
        return new CatalogSnapshot(previous.Version + 1, [merged], index.ToImmutable(), merged.Catalog, deadRows: 0);
    }

    /// <summary>Applies a delta, then compacts in the same publication when the policy says so.</summary>
    public static CatalogSnapshot ApplyAndMaybeCompact(CatalogSnapshot previous, ProductDelta delta)
    {
        CatalogSnapshot next = Apply(previous, delta);
        // Compacting inside the same merge keeps the version step at exactly one per publication
        return ShouldCompact(next) ? WithVersion(Compact(next), next.Version) : next;
    }

    private static CatalogSnapshot WithVersion(CatalogSnapshot snapshot, long version) =>
        // Same content, renumbered: used only to fold a compaction into the merge that triggered it
        new(version, snapshot.Segments, snapshot.Index, snapshot.Catalog, snapshot.DeadRows);
}
