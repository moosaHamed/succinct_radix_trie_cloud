using System.Collections.Immutable;
using static Succinct.Catalog.Tests.TestData;

namespace Succinct.Catalog.Tests;

public sealed class InventoryLedgerTests
{
    [Fact]
    public void Concurrent_takes_never_oversell()
    {
        var key = new ProductKey(1);
        var ledger = new InventoryLedger(ImmutableDictionary<ProductKey, long>.Empty.Add(key, 1_000));
        const int Workers = 8;
        const int AttemptsPerWorker = 200;
        int succeeded = 0;
        using var start = new Barrier(Workers);
        Thread[] threads = Enumerable.Range(0, Workers).Select(_ => new Thread(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < AttemptsPerWorker; i++)
            {
                // 1,600 attempts against 1,000 units: exactly 1,000 may succeed
                if (ledger.TryTake(key, 1))
                {
                    Interlocked.Increment(ref succeeded);
                }
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
        Assert.Equal(1_000, succeeded);
        Assert.True(ledger.TryGetOnHand(key, out long left));
        Assert.Equal(0, left);
    }

    [Fact]
    public void Unknown_keys_and_short_stock_change_nothing()
    {
        var ledger = new InventoryLedger();
        Assert.False(ledger.TryTake(new ProductKey(5), 1));
        ledger.Restock(new ProductKey(5), 3);
        Assert.False(ledger.TryTake(new ProductKey(5), 4));
        Assert.True(ledger.TryGetOnHand(new ProductKey(5), out long onHand));
        Assert.Equal(3, onHand);
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TryTake(new ProductKey(5), 0));
    }
}

public sealed class TenantTests
{
    private static readonly Guid s_root = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid s_region = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid s_store = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly AttributePath s_price = AttributePath.Parse("price");

    [Fact]
    public void Children_are_built_by_the_tree_not_accepted_from_callers()
    {
        var tree = new TenantTree();
        tree.AddRoot(s_root);
        tree.AddChild(s_root, s_region);
        // Re-adding an existing id would have replaced its subtree silently (F13)
        Assert.Throws<InvalidOperationException>(() => tree.AddChild(s_root, s_region));
        Assert.Throws<InvalidOperationException>(() => tree.AddChild(Guid.NewGuid(), s_store));
        ImmutableDictionary<Guid, TenantNode> snapshot = tree.Snapshot;
        Assert.Equal(s_root, snapshot[s_region].ParentId);
        Assert.Contains(s_region, snapshot[s_root].ChildIds);
    }

    [Fact]
    public void Resolution_walks_ancestors_by_key_and_path()
    {
        var tree = new TenantTree();
        tree.AddRoot(s_root);
        tree.AddChild(s_root, s_region);
        tree.AddChild(s_region, s_store);
        // Root layer: two products. Its row and attribute ids differ from any other layer's
        tree.SetDataLayer(s_root, CatalogMerge.Apply(CatalogSnapshot.Empty, ProductDelta.ForUpserts(
            Upsert(1, Attr("price", AttributeValue.FromFloat64(10))),
            Upsert(2, Attr("price", AttributeValue.FromFloat64(20))))));
        // Store layer overrides product 2 only, with a different vocabulary order
        tree.SetDataLayer(s_store, CatalogMerge.Apply(CatalogSnapshot.Empty, ProductDelta.ForUpserts(
            Upsert(2, Attr("name", AttributeValue.FromFreeText("local")), Attr("price", AttributeValue.FromFloat64(25))))));
        ImmutableDictionary<Guid, TenantNode> snapshot = tree.Snapshot;

        // Nearest override wins
        Assert.True(TenantAttributeResolver.TryResolve(snapshot, s_store, new ProductKey(2), s_price, out AttributeValue local));
        Assert.Equal(AttributeValue.FromFloat64(25), local);
        // Inherited from the root through a layer that has no data
        Assert.True(TenantAttributeResolver.TryResolve(snapshot, s_store, new ProductKey(1), s_price, out AttributeValue inherited));
        Assert.Equal(AttributeValue.FromFloat64(10), inherited);
        // Absent everywhere
        Assert.False(TenantAttributeResolver.TryResolve(snapshot, s_store, new ProductKey(3), s_price, out _));
    }

    [Fact]
    public void Descendant_checks_fail_closed()
    {
        var tree = new TenantTree();
        tree.AddRoot(s_root);
        tree.AddChild(s_root, s_region);
        ImmutableDictionary<Guid, TenantNode> snapshot = tree.Snapshot;
        Assert.True(TenantAttributeResolver.IsSameOrDescendant(snapshot, s_region, s_root));
        Assert.True(TenantAttributeResolver.IsSameOrDescendant(snapshot, s_root, s_root));
        Assert.False(TenantAttributeResolver.IsSameOrDescendant(snapshot, s_root, s_region));
        // Unknown ids are denied, not trusted
        Assert.False(TenantAttributeResolver.IsSameOrDescendant(snapshot, Guid.NewGuid(), s_root));
    }

    [Fact]
    public async Task Invalidation_pipeline_delivers_in_order_and_completes()
    {
        var pipeline = new TenantInvalidationPipeline();
        var seen = new List<Guid>();
        Task consumer = pipeline.RunConsumerAsync((change, _) =>
        {
            seen.Add(change.TenantId);
            return ValueTask.CompletedTask;
        });
        await pipeline.ReportAsync(new TenantChangeEvent(s_root, TenantChangeKind.DataUpdated));
        await pipeline.ReportAsync(new TenantChangeEvent(s_store, TenantChangeKind.StructureChanged));
        // Completion is explicit, so the consumer ends cleanly instead of waiting forever
        pipeline.Complete();
        await consumer;
        Assert.Equal(new[] { s_root, s_store }, seen);
    }
}
