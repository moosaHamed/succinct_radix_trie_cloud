using System.Collections.Immutable;
using System.Threading.Channels;

namespace Succinct.Catalog;

/// <summary>
/// One tenant in the hierarchy. <see cref="OwnDataLayer"/> holds only this
/// tenant's own overrides; inherited data is resolved by walking
/// <see cref="ParentId"/>, never copied into descendants.
/// </summary>
public sealed record TenantNode
{
    // required: the compiler refuses a partially initialized node
    public required Guid TenantId { get; init; }

    public required Guid? ParentId { get; init; }

    public required ImmutableArray<Guid> ChildIds { get; init; }

    public required CatalogSnapshot? OwnDataLayer { get; init; }
}

/// <summary>
/// The whole tenant hierarchy as ONE immutable snapshot, replaced atomically.
/// A ConcurrentDictionary of individually mutable nodes would let two reads
/// observe different generations of a reparented tree. Nodes are built INSIDE
/// the pure transformers, never accepted from callers (finding F13: a
/// caller-built node could overwrite or orphan an existing subtree).
/// </summary>
public sealed class TenantTree
{
    /// <summary>Deepest allowed chain; a guard against corrupted or cyclic data, not honest growth.</summary>
    public const int MaxDepth = 32;

    /// <summary>Widest allowed fan-out of one node.</summary>
    public const int MaxChildrenPerNode = 10_000;

    // Replaced wholesale by ImmutableInterlocked.Update, never edited
    private ImmutableDictionary<Guid, TenantNode> _nodes = ImmutableDictionary<Guid, TenantNode>.Empty;

    /// <summary>One consistent generation of the hierarchy; capture once and reuse for a whole walk.</summary>
    public ImmutableDictionary<Guid, TenantNode> Snapshot => Volatile.Read(ref _nodes);

    /// <summary>Adds a root tenant (one without a parent).</summary>
    public void AddRoot(Guid tenantId)
    {
        // Snapshot, merge, publish: ImmutableInterlocked owns the CAS loop
        ImmutableInterlocked.Update(ref _nodes, static (nodes, id) =>
        {
            // Adding over an existing id would silently replace a whole subtree
            if (nodes.ContainsKey(id))
            {
                ThrowHelper.ThrowInvalidOperation("Tenant already exists.");
            }
            // Built here, so its fields cannot disagree with the tree
            return nodes.Add(id, new TenantNode { TenantId = id, ParentId = null, ChildIds = [], OwnDataLayer = null });
        }, tenantId);
    }

    /// <summary>Adds <paramref name="childId"/> under <paramref name="parentId"/>.</summary>
    public void AddChild(Guid parentId, Guid childId)
    {
        ImmutableInterlocked.Update(ref _nodes, static (nodes, change) =>
        {
            // The transformer may run several times under contention, so it only computes; no side effects
            if (nodes.ContainsKey(change.ChildId))
            {
                ThrowHelper.ThrowInvalidOperation("Tenant already exists.");
            }
            // A missing parent fails closed instead of creating an orphan
            if (!nodes.TryGetValue(change.ParentId, out TenantNode? parent))
            {
                ThrowHelper.ThrowInvalidOperation("Parent tenant not found.");
            }
            // Fan-out cap bounds one node's child array
            if (parent.ChildIds.Length >= MaxChildrenPerNode)
            {
                ThrowHelper.ThrowInvalidOperation("Tenant fan-out cap exceeded.");
            }
            // The new node's depth is the parent's plus one, measured on this snapshot only
            if (DepthOf(nodes, parent) + 1 >= MaxDepth)
            {
                ThrowHelper.ThrowInvalidOperation("Tenant depth cap exceeded.");
            }
            var child = new TenantNode { TenantId = change.ChildId, ParentId = change.ParentId, ChildIds = [], OwnDataLayer = null };
            // Two structural-sharing edits; everything else is shared with the previous generation
            return nodes
                .SetItem(change.ParentId, parent with { ChildIds = parent.ChildIds.Add(change.ChildId) })
                .Add(change.ChildId, child);
        }, new ChildChange(parentId, childId));
    }

    /// <summary>Replaces a tenant's own data layer (null removes its overrides).</summary>
    public void SetDataLayer(Guid tenantId, CatalogSnapshot? layer)
    {
        ImmutableInterlocked.Update(ref _nodes, static (nodes, change) =>
        {
            if (!nodes.TryGetValue(change.TenantId, out TenantNode? node))
            {
                ThrowHelper.ThrowInvalidOperation("Tenant not found.");
            }
            // 'with' creates a new record sharing every unchanged field
            return nodes.SetItem(change.TenantId, node with { OwnDataLayer = change.Layer });
        }, new LayerChange(tenantId, layer));
    }

    // Root depth is 0; bounded and iterative, so corrupted cyclic data becomes an exception, never a hang
    private static int DepthOf(ImmutableDictionary<Guid, TenantNode> nodes, TenantNode node)
    {
        int depth = 0;
        for (Guid? cursor = node.ParentId; cursor is Guid id;)
        {
            depth++;
            if (depth > MaxDepth)
            {
                ThrowHelper.ThrowInvalidOperation("Tenant chain exceeds the depth cap.");
            }
            cursor = nodes.TryGetValue(id, out TenantNode? ancestor) ? ancestor.ParentId : null;
        }
        return depth;
    }

    // Transformer arguments as named types: the static lambdas capture nothing
    private readonly record struct ChildChange(Guid ParentId, Guid ChildId);

    private readonly record struct LayerChange(Guid TenantId, CatalogSnapshot? Layer);
}

/// <summary>Inheritance and authorization walks over one captured hierarchy snapshot.</summary>
public static class TenantAttributeResolver
{
    /// <summary>
    /// Nearest tenant (self first, then ancestors) whose own layer has this
    /// attribute for this product. Keys and paths mean the same thing in every
    /// layer; row and attribute ids do not, so each layer translates its own
    /// (finding F6: the original applied one store's ids to every ancestor).
    /// </summary>
    public static bool TryResolve(
        ImmutableDictionary<Guid, TenantNode> snapshot,
        Guid startTenantId,
        ProductKey key,
        AttributePath path,
        out AttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Guid? cursor = startTenantId;
        for (int hops = 0; cursor is Guid id; hops++)
        {
            // Hop cap turns a cyclic hierarchy into an exception instead of a hang
            if (hops >= TenantTree.MaxDepth)
            {
                ThrowHelper.ThrowInvalidOperation("Tenant chain exceeds the depth cap.");
            }
            if (!snapshot.TryGetValue(id, out TenantNode? node))
            {
                break;
            }
            // Each layer resolves key and path in its own id spaces
            if (node.OwnDataLayer is { } layer && layer.TryGet(key, path, out value))
            {
                return true;
            }
            cursor = node.ParentId;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Authorization, not concurrency: true when <paramref name="candidateId"/> is
    /// <paramref name="ancestorId"/> or below it. Check it server-side against a
    /// signed claim; never trust a client-supplied tenant id.
    /// </summary>
    public static bool IsSameOrDescendant(ImmutableDictionary<Guid, TenantNode> snapshot, Guid candidateId, Guid ancestorId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Guid? cursor = candidateId;
        for (int hops = 0; cursor is Guid id && hops <= TenantTree.MaxDepth; hops++)
        {
            if (id == ancestorId)
            {
                return true;
            }
            cursor = snapshot.TryGetValue(id, out TenantNode? node) ? node.ParentId : null;
        }
        // Default deny: an unknown id, a cycle, or an over-deep chain all fail closed
        return false;
    }
}

/// <summary>What changed about a tenant.</summary>
public enum TenantChangeKind
{
    /// <summary>The tenant's own data layer changed.</summary>
    DataUpdated = 0,

    /// <summary>The hierarchy around the tenant changed.</summary>
    StructureChanged = 1,
}

/// <summary>One change notification.</summary>
public readonly record struct TenantChangeEvent(Guid TenantId, TenantChangeKind Kind);

/// <summary>
/// Async-native invalidation queue with bounded backpressure: a full queue
/// makes producers wait instead of growing without limit or dropping events.
/// </summary>
public sealed class TenantInvalidationPipeline
{
    // Bounded: an unbounded queue is a resource-exhaustion primitive, a silent drop a correctness bug
    private readonly Channel<TenantChangeEvent> _channel = Channel.CreateBounded<TenantChangeEvent>(
        new BoundedChannelOptions(10_000)
        {
            // Exactly one consumer owns recomputation order, so recomputations cannot interleave
            SingleReader = true,
            // Many request threads may report concurrently
            SingleWriter = false,
            // Wait, not DropOldest
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>Queues an event; completes synchronously, allocation-free, while the queue has room.</summary>
    public ValueTask ReportAsync(TenantChangeEvent change, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(change, cancellationToken);

    /// <summary>Runs <paramref name="handler"/> for every event until <see cref="Complete"/> or cancellation.</summary>
    public async Task RunConsumerAsync(
        Func<TenantChangeEvent, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // ReadAllAsync ends cleanly on Complete(): no sentinel value, no polling
        await foreach (TenantChangeEvent change in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Each handler call is awaited before the next event: ordering is the point of a single reader
            await handler(change, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tells the consumer no more events are coming, which an empty queue can never say.</summary>
    public void Complete() => _channel.Writer.Complete();
}
