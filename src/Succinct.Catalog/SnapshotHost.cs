namespace Succinct.Catalog;

/// <summary>A snapshot that knows its generation number, so a publisher can check every merge advances it by one.</summary>
public interface IVersioned
{
    /// <summary>Generation number; each published merge increases it by exactly one.</summary>
    long Version { get; }
}

/// <summary>
/// Publishes immutable snapshots: Category 6 ownership, transferred by
/// publication. Readers take <see cref="Current"/> once per logical operation,
/// lock-free. Writers capture the current snapshot, run a pure merge off to the
/// side, and publish the result; a single-writer gate makes each publish
/// replace exactly the snapshot its merge started from (finding F3: the
/// original let two writers merge from one snapshot and erase each other).
/// </summary>
/// <remarks>
/// A gate, not CAS, because the catalog's merge is expensive: under contention
/// a lost CAS would re-run that merge, and a hot loop of them can livelock.
/// Readers never touch the gate.
/// </remarks>
public sealed class SnapshotHost<TSnapshot>
    where TSnapshot : class, IVersioned
{
    // System.Threading.Lock (.NET 9+): serializes writers only
    private readonly Lock _writeGate = new();
    // The single mutable field: replaced wholesale, never edited
    private TSnapshot _current;

    /// <summary>Starts publishing from <paramref name="initial"/>.</summary>
    public SnapshotHost(TSnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    // Acquire-read: a reader sees a fully built snapshot, never a partial one (this matters on ARM64)
    /// <summary>The latest published snapshot. Capture it once and reuse it for the whole operation.</summary>
    public TSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Merges <paramref name="delta"/> into the current snapshot and publishes the result.</summary>
    public TSnapshot Apply<TDelta>(TDelta delta, Func<TSnapshot, TDelta, TSnapshot> merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        // One writer at a time: the snapshot captured below is exactly the one this call replaces
        lock (_writeGate)
        {
            // Inside the gate no other writer can publish, so this capture cannot go stale
            TSnapshot previous = _current;
            // Pure merge, built off to the side; readers keep using 'previous' undisturbed
            TSnapshot next = merge(previous, delta);
            // A merge returning a stale or foreign snapshot is a bug; stop it before publication
            ThrowIfNotSuccessor(previous, next);
            // Release-write: every field of 'next' is visible before the reference is
            Volatile.Write(ref _current, next);
            return next;
        }
    }

    /// <summary>
    /// Optimistic concurrency across processes (HTTP If-Match): merges only when
    /// the current version still equals <paramref name="expectedVersion"/>.
    /// Returns false, publishing nothing, when another write got there first.
    /// </summary>
    public bool TryApply<TDelta>(long expectedVersion, TDelta delta,
        Func<TSnapshot, TDelta, TSnapshot> merge, out TSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(merge);
        lock (_writeGate)
        {
            TSnapshot previous = _current;
            // The client edited version N; if the world moved on, refuse instead of overwriting (HTTP 412)
            if (previous.Version != expectedVersion)
            {
                current = previous;
                return false;
            }
            TSnapshot next = merge(previous, delta);
            ThrowIfNotSuccessor(previous, next);
            Volatile.Write(ref _current, next);
            current = next;
            return true;
        }
    }

    private static void ThrowIfNotSuccessor(TSnapshot previous, TSnapshot next)
    {
        // Null, or anything but previous + 1, means the merge did not start from 'previous'
        if (next is null || next.Version != previous.Version + 1)
        {
            ThrowHelper.ThrowInvalidOperation("A merge must return the next version of the snapshot it was given.");
        }
    }
}
