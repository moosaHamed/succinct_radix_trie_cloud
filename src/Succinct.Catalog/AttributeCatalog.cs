namespace Succinct.Catalog;

/// <summary>
/// An open, append-only registry of attribute paths discovered from ingested
/// data: path to id through a <see cref="RadixTrie"/>, id to path through an
/// array. Ids never change once issued, because every extension keeps the
/// previous catalog's ids verbatim and appends new paths in ordinal order. That
/// makes one id space valid across every segment and generation of a snapshot
/// (finding F6), instead of ids that follow hash-set enumeration order.
/// </summary>
public sealed class AttributeCatalog
{
    /// <summary>Hard ceiling on the cumulative vocabulary: open, but never unbounded.</summary>
    public const int MaxDistinctPaths = 1 << 18;

    // path -> id, as the terminal's scalar payload
    private readonly RadixTrie _trie;
    // id -> path; index i IS id i
    private readonly string[] _pathById;

    // Private: catalogs are made only by Empty and Extend
    private AttributeCatalog(RadixTrie trie, string[] pathById)
    {
        _trie = trie;
        _pathById = pathById;
    }

    /// <summary>The catalog with no paths.</summary>
    public static AttributeCatalog Empty { get; } = new(RadixTrieBuilder.Build([]), []);

    /// <summary>Number of distinct paths; valid ids are 0 through Count - 1.</summary>
    public int Count => _pathById.Length;

    /// <summary>Id of <paramref name="path"/>, when the catalog knows it.</summary>
    public bool TryGetId(AttributePath path, out int attributeId) =>
        // AttributePath guarantees a validated, non-null value
        TryGetId(path.Value.AsSpan(), out attributeId);

    /// <summary>Allocation-free lookup for callers holding a slice of a longer string.</summary>
    internal bool TryGetId(ReadOnlySpan<char> path, out int attributeId)
    {
        if (_trie.TryGetPayload(path, out uint scalar, out _))
        {
            // Ids are array indexes bounded by MaxDistinctPaths, far below int.MaxValue
            attributeId = (int)scalar;
            return true;
        }
        attributeId = -1;
        return false;
    }

    /// <summary>Path text of <paramref name="attributeId"/>.</summary>
    public string PathOf(int attributeId)
    {
        // Validated against this catalog's own id space
        if ((uint)attributeId >= (uint)_pathById.Length)
        {
            ThrowHelper.ThrowArgumentOutOfRange(nameof(attributeId));
        }
        return _pathById[attributeId];
    }

    /// <summary>
    /// Returns a catalog containing <paramref name="previous"/>'s paths with
    /// their ids unchanged, plus every path in <paramref name="discovered"/> it
    /// lacked, appended in ordinal order. Returns <paramref name="previous"/>
    /// itself when nothing is new, so unchanged vocabularies are shared.
    /// </summary>
    internal static AttributeCatalog Extend(AttributeCatalog? previous, IEnumerable<AttributePath> discovered)
    {
        // The first generation extends the empty catalog: one code path for every generation
        AttributeCatalog baseline = previous ?? Empty;
        // Genuinely new paths only, deduplicated and ordered ordinally; allocated only if needed
        SortedSet<string>? fresh = null;
        foreach (AttributePath path in discovered)
        {
            if (!baseline.TryGetId(path, out _))
            {
                (fresh ??= new SortedSet<string>(StringComparer.Ordinal)).Add(path.Value);
            }
        }
        // Nothing new: share the existing instance
        if (fresh is null)
        {
            return baseline;
        }
        // The cap applies to the cumulative vocabulary, so many small batches cannot sum past it
        int total = checked(baseline.Count + fresh.Count);
        if (total > MaxDistinctPaths)
        {
            ThrowHelper.ThrowInvalidOperation("Attribute vocabulary cap exceeded.");
        }
        // Existing ids copied verbatim, new ones appended: an id issued once names the same path forever
        string[] paths = new string[total];
        baseline._pathById.CopyTo(paths, 0);
        fresh.CopyTo(paths, baseline.Count);
        // Index i IS id i, so PathOf stays a direct array read
        IEnumerable<(string Key, uint Scalar, float Value)> entries =
            paths.Select(static (p, i) => (p, (uint)i, 0f));
        // Duplicates are impossible here; Throw turns a broken invariant into an exception instead of a silent overwrite
        return new AttributeCatalog(RadixTrieBuilder.Build(entries, DuplicateKeyPolicy.Throw), paths);
    }
}
