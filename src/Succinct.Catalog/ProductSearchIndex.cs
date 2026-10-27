namespace Succinct.Catalog;

/// <summary>
/// Content index over one snapshot: every live record's canonical form in a
/// <see cref="RadixTrie"/>, giving exact content-equality lookup and prefix
/// existence with no schema registration. Records with identical content are
/// grouped, not collapsed: the original indexed last-write-wins, which hid
/// every duplicate but one.
/// </summary>
public sealed class ProductSearchIndex
{
    // Canonical form -> group number (the trie's scalar payload)
    private readonly RadixTrie _trie;
    // CSR over _keys: group g spans [_groupStart[g], _groupStart[g + 1])
    private readonly int[] _groupStart;
    // Members of every group, each group sorted by key
    private readonly ProductKey[] _keys;

    // Private: built only from a snapshot
    private ProductSearchIndex(RadixTrie trie, int[] groupStart, ProductKey[] keys)
    {
        _trie = trie;
        _groupStart = groupStart;
        _keys = keys;
    }

    /// <summary>Number of distinct contents.</summary>
    public int DistinctContentCount => _groupStart.Length - 1;

    /// <summary>Indexes every live record of <paramref name="snapshot"/>.</summary>
    public static ProductSearchIndex Build(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Group keys by content; ordinal, because canonical forms are compared code unit by code unit
        var groups = new Dictionary<string, List<ProductKey>>(StringComparer.Ordinal);
        foreach ((ProductKey key, RecordLocator at) in snapshot.Index)
        {
            string canonical = CanonicalForm.Build(snapshot.Segments[at.Segment], at.Row);
            if (!groups.TryGetValue(canonical, out List<ProductKey>? members))
            {
                members = [];
                groups.Add(canonical, members);
            }
            members.Add(key);
        }
        // Deterministic group numbering, independent of dictionary enumeration order
        string[] contents = [.. groups.Keys];
        Array.Sort(contents, StringComparer.Ordinal);
        int[] groupStart = new int[contents.Length + 1];
        var keys = new ProductKey[snapshot.LiveRows];
        var entries = new (string Key, uint Scalar, float Value)[contents.Length];
        int cursor = 0;
        for (int g = 0; g < contents.Length; g++)
        {
            groupStart[g] = cursor;
            List<ProductKey> members = groups[contents[g]];
            // Deterministic order inside each group too
            members.Sort();
            members.CopyTo(keys, cursor);
            cursor += members.Count;
            entries[g] = (contents[g], (uint)g, 0f);
        }
        groupStart[^1] = cursor;
        // Contents are distinct by construction; Throw turns a broken invariant into an exception
        return new ProductSearchIndex(RadixTrieBuilder.Build(entries, DuplicateKeyPolicy.Throw), groupStart, keys);
    }

    /// <summary>Every live key whose record has exactly this canonical form; empty when none.</summary>
    public ReadOnlySpan<ProductKey> FindIdentical(ReadOnlySpan<char> canonicalForm)
    {
        if (!_trie.TryGetPayload(canonicalForm, out uint group, out _))
        {
            return [];
        }
        // Group numbers are array indexes, so they fit in int
        int g = (int)group;
        int start = _groupStart[g];
        return _keys.AsSpan(start, _groupStart[g + 1] - start);
    }

    /// <summary>True when some live record's canonical form starts with <paramref name="prefix"/>.</summary>
    public bool HasPrefix(ReadOnlySpan<char> prefix) => _trie.StartsWith(prefix);
}
