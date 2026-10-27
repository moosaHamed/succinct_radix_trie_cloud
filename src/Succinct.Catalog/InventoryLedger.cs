using System.Collections.Immutable;

namespace Succinct.Catalog;

/// <summary>
/// On-hand stock, with exactly one owner, keyed by the stable
/// <see cref="ProductKey"/> (finding F12: stock lived both in the frozen catalog
/// and in an array indexed by positions that shifted every generation). Even
/// this hot state follows snapshot, merge, publish: each operation is a pure
/// transformer applied by <see cref="ImmutableInterlocked"/>, which owns the
/// compare-and-swap retry loop. The check always runs against the exact snapshot
/// the swap replaces, so a read-check-write race cannot oversell.
/// </summary>
public sealed class InventoryLedger
{
    // Snapshot of on-hand quantities; replaced wholesale, never edited in place
    private ImmutableDictionary<ProductKey, long> _onHand;

    /// <summary>Starts from <paramref name="initial"/>, or empty.</summary>
    public InventoryLedger(ImmutableDictionary<ProductKey, long>? initial = null) =>
        _onHand = initial ?? ImmutableDictionary<ProductKey, long>.Empty;

    /// <summary>One consistent snapshot of every quantity; capture it once per request.</summary>
    public ImmutableDictionary<ProductKey, long> Snapshot => Volatile.Read(ref _onHand);

    /// <summary>On-hand quantity of <paramref name="key"/>, if the ledger tracks it.</summary>
    public bool TryGetOnHand(ProductKey key, out long quantity) => Snapshot.TryGetValue(key, out quantity);

    /// <summary>Removes <paramref name="quantity"/> units; false, changing nothing, when stock is short or unknown.</summary>
    public bool TryTake(ProductKey key, long quantity)
    {
        // Validated at the domain edge too, not only at the HTTP edge
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        // Snapshot, merge, publish; the transformer is pure and may run more than once under contention
        return ImmutableInterlocked.Update(ref _onHand, static (snapshot, change) =>
        {
            // Returning the SAME instance means "no change", and Update then returns false
            if (!snapshot.TryGetValue(change.Key, out long onHand) || onHand < change.Quantity)
            {
                return snapshot;
            }
            // One structural-sharing edit
            return snapshot.SetItem(change.Key, onHand - change.Quantity);
        }, new StockChange(key, quantity));
    }

    /// <summary>Adds <paramref name="quantity"/> units, creating the entry when needed.</summary>
    public void Restock(ProductKey key, long quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ImmutableInterlocked.Update(ref _onHand, static (snapshot, change) =>
        {
            // Absent counts as zero for a restock
            long onHand = snapshot.TryGetValue(change.Key, out long current) ? current : 0;
            // checked: a quantity that overflows long is corrupt input, not a number to wrap
            return snapshot.SetItem(change.Key, checked(onHand + change.Quantity));
        }, new StockChange(key, quantity));
    }

    // The transformer argument: a named type keeps the static lambdas free of captures
    private readonly record struct StockChange(ProductKey Key, long Quantity);
}
