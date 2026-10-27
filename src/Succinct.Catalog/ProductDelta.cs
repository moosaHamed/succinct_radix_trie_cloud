using System.Collections.Immutable;

namespace Succinct.Catalog;

/// <summary>A full record for one product: insert when the key is new, replace when it exists.</summary>
public readonly record struct ProductUpsert(ProductKey Key, ImmutableArray<ProductAttributeInput> Attributes);

/// <summary>
/// One immutable change set: the only way callers express mutation. A delta is
/// merged into a snapshot by a pure function and published atomically; nothing
/// is ever written in place.
/// </summary>
public sealed record ProductDelta(ImmutableArray<ProductUpsert> Upserts, ImmutableArray<ProductKey> Deletes)
{
    /// <summary>A delta that changes nothing.</summary>
    public static ProductDelta Empty { get; } = new([], []);

    /// <summary>A delta of upserts only.</summary>
    public static ProductDelta ForUpserts(params ReadOnlySpan<ProductUpsert> upserts) =>
        // Spread copies the caller's span, so the delta owns its data
        new([.. upserts], []);

    /// <summary>A delta of deletes only.</summary>
    public static ProductDelta ForDeletes(params ReadOnlySpan<ProductKey> keys) =>
        new([], [.. keys]);
}
