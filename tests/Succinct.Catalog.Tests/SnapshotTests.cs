using System.Collections.Immutable;
using static Succinct.Catalog.Tests.TestData;

namespace Succinct.Catalog.Tests;

public sealed class CatalogMergeTests
{
    private static readonly AttributePath s_name = AttributePath.Parse("name");

    [Fact]
    public void Upserts_updates_and_deletes_follow_the_documented_semantics()
    {
        CatalogSnapshot g1 = CatalogMerge.Apply(CatalogSnapshot.Empty, ProductDelta.ForUpserts(
            Upsert(1, Attr("name", AttributeValue.FromFreeText("one"))),
            Upsert(2, Attr("name", AttributeValue.FromFreeText("two")))));
        Assert.Equal(1, g1.Version);
        Assert.Equal(2, g1.LiveRows);

        // Update: the old row is shadowed, not edited
        CatalogSnapshot g2 = CatalogMerge.Apply(g1, ProductDelta.ForUpserts(Upsert(1, Attr("name", AttributeValue.FromFreeText("uno")))));
        Assert.Equal(2, g2.Version);
        Assert.Equal(1, g2.DeadRows);
        Assert.True(g2.TryGet(new ProductKey(1), s_name, out AttributeValue updated));
        Assert.Equal(AttributeValue.FromFreeText("uno"), updated);
        // The previous generation is untouched: readers holding it see the old value
        Assert.True(g1.TryGet(new ProductKey(1), s_name, out AttributeValue old));
        Assert.Equal(AttributeValue.FromFreeText("one"), old);

        // Delete, plus a delete of an absent key, which is a documented no-op
        CatalogSnapshot g3 = CatalogMerge.Apply(g2, ProductDelta.ForDeletes(new ProductKey(2), new ProductKey(99)));
        Assert.False(g3.Contains(new ProductKey(2)));
        Assert.Equal(1, g3.LiveRows);
        Assert.Equal(2, g3.DeadRows);
    }

    [Fact]
    public void Conflicting_deltas_are_rejected()
    {
        // The same key twice in one delta
        Assert.Throws<ArgumentException>(() => CatalogMerge.Apply(CatalogSnapshot.Empty,
            ProductDelta.ForUpserts(Upsert(1), Upsert(1))));
        // Upserted and deleted in one delta
        var both = new ProductDelta([Upsert(1)], [new ProductKey(1)]);
        Assert.Throws<ArgumentException>(() => CatalogMerge.Apply(CatalogSnapshot.Empty, both));
        // Uninitialized arrays
        Assert.Throws<ArgumentException>(() => CatalogMerge.Apply(CatalogSnapshot.Empty, new ProductDelta(default, [])));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Compaction_preserves_every_live_record_and_every_attribute_id(int seed)
    {
        var random = new Random(seed);
        CatalogSnapshot snapshot = CatalogSnapshot.Empty;
        // Several overlapping batches: keys 1..60 rewritten repeatedly, then a few deleted
        for (int batch = 0; batch < 5; batch++)
        {
            snapshot = CatalogMerge.Apply(snapshot, ProductDelta.ForUpserts(RandomUpserts(random, 40, firstKey: 1 + (batch * 5))));
        }
        snapshot = CatalogMerge.Apply(snapshot, ProductDelta.ForDeletes(new ProductKey(3), new ProductKey(4)));
        Assert.True(snapshot.DeadRows > 0);

        CatalogSnapshot compacted = CatalogMerge.Compact(snapshot);
        Assert.Equal(snapshot.Version + 1, compacted.Version);
        Assert.Single(compacted.Segments);
        Assert.Equal(0, compacted.DeadRows);
        Assert.Equal(snapshot.LiveRows, compacted.LiveRows);
        // Same catalog instance in, same ids out
        Assert.Same(snapshot.Catalog, compacted.Catalog);
        foreach (ProductKey key in snapshot.Index.Keys)
        {
            Assert.True(snapshot.TryGetRecord(key, out ImmutableArray<ProductAttributeInput> before));
            Assert.True(compacted.TryGetRecord(key, out ImmutableArray<ProductAttributeInput> after));
            Assert.Equal(ByPath(before), ByPath(after));
        }
    }

    [Fact]
    public void Apply_and_compact_advances_the_version_by_one()
    {
        CatalogSnapshot snapshot = CatalogMerge.Apply(CatalogSnapshot.Empty, ProductDelta.ForUpserts(Upsert(1), Upsert(2)));
        // Rewriting both keys makes dead rows exceed half the live ones, so compaction triggers
        CatalogSnapshot next = CatalogMerge.ApplyAndMaybeCompact(snapshot, ProductDelta.ForUpserts(Upsert(1), Upsert(2)));
        Assert.Equal(snapshot.Version + 1, next.Version);
        Assert.Equal(0, next.DeadRows);
        Assert.Single(next.Segments);
    }

    [Fact]
    public void Attribute_ids_survive_generations_and_segments()
    {
        CatalogSnapshot g1 = CatalogMerge.Apply(CatalogSnapshot.Empty,
            ProductDelta.ForUpserts(Upsert(1, Attr("name", AttributeValue.FromFreeText("a")))));
        CatalogSnapshot g2 = CatalogMerge.Apply(g1,
            ProductDelta.ForUpserts(Upsert(2, Attr("color", AttributeValue.FromCategorical("Red")))));
        Assert.True(g1.Catalog.TryGetId(s_name, out int before));
        Assert.True(g2.Catalog.TryGetId(s_name, out int after));
        Assert.Equal(before, after);
        // Key 1 lives in segment 0, built before "color" existed, and still resolves through the newer catalog
        Assert.True(g2.TryGet(new ProductKey(1), s_name, out _));
        Assert.False(g2.TryGet(new ProductKey(1), AttributePath.Parse("color"), out _));
    }

    [Fact]
    public void Keyset_pages_are_ordered_and_resume_after_a_key()
    {
        CatalogSnapshot snapshot = CatalogMerge.Apply(CatalogSnapshot.Empty,
            ProductDelta.ForUpserts(Upsert(30), Upsert(10), Upsert(20), Upsert(40)));
        Assert.Equal(new long[] { 10, 20 }, snapshot.Page(null, 2).ToArray().Select(static k => k.Value));
        Assert.Equal(new long[] { 30, 40 }, snapshot.Page(new ProductKey(20), 5).ToArray().Select(static k => k.Value));
        // Resuming after a key that no longer exists still lands in the right place
        Assert.Equal(new long[] { 30, 40 }, snapshot.Page(new ProductKey(25), 5).ToArray().Select(static k => k.Value));
        Assert.Equal(0, snapshot.Page(new ProductKey(40), 5).Length);
    }
}

public sealed class SnapshotHostTests
{
    [Fact]
    public void Concurrent_writers_never_lose_a_batch()
    {
        var host = new SnapshotHost<CatalogSnapshot>(CatalogSnapshot.Empty);
        const int Writers = 8;
        const int PerWriter = 40;
        // Releases every writer at the same instant, maximizing overlap
        using var start = new Barrier(Writers);
        // Dedicated threads, not pool tasks: a Barrier must never block thread-pool workers
        Thread[] threads = Enumerable.Range(0, Writers).Select(writer => new Thread(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < PerWriter; i++)
            {
                // Distinct keys per writer, so the expected total is exact
                host.Apply(ProductDelta.ForUpserts(Upsert((writer * 1_000) + i)), CatalogMerge.Apply);
            }
        })).ToArray();
        foreach (Thread thread in threads)
        {
            thread.Start();
        }
        foreach (Thread thread in threads)
        {
            thread.Join();
        }
        // Anything below Writers * PerWriter is a lost update (F3); the version counts every publication
        Assert.Equal(Writers * PerWriter, host.Current.LiveRows);
        Assert.Equal(Writers * PerWriter, host.Current.Version);
    }

    [Fact]
    public void A_merge_that_does_not_advance_the_version_is_refused()
    {
        var host = new SnapshotHost<CatalogSnapshot>(CatalogSnapshot.Empty);
        // Returning the input is how a stale-snapshot bug would look from the host's side
        Assert.Throws<InvalidOperationException>(() => host.Apply(0, static (snapshot, _) => snapshot));
        Assert.Same(CatalogSnapshot.Empty, host.Current);
    }

    [Fact]
    public void Stale_expected_version_is_refused_without_publishing()
    {
        var host = new SnapshotHost<CatalogSnapshot>(CatalogSnapshot.Empty);
        host.Apply(ProductDelta.ForUpserts(Upsert(1)), CatalogMerge.Apply);
        // The client read version 0, but the catalog is at 1: HTTP 412 territory
        bool applied = host.TryApply(0, ProductDelta.ForUpserts(Upsert(2)), CatalogMerge.Apply, out CatalogSnapshot current);
        Assert.False(applied);
        Assert.Equal(1, current.Version);
        Assert.False(host.Current.Contains(new ProductKey(2)));
        // With the right version it goes through
        Assert.True(host.TryApply(1, ProductDelta.ForUpserts(Upsert(2)), CatalogMerge.Apply, out current));
        Assert.Equal(2, current.Version);
    }
}
