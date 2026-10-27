namespace Succinct;

/// <summary>A dense id into one <see cref="StringPool"/>; its own type, so a list index or node id cannot be passed by accident.</summary>
public readonly record struct StringId(int Value);

/// <summary>
/// Strings stored back to back in one <see cref="char"/> array (CSR layout) with
/// dense ids: O(1), zero-allocation reads of the full value. Replaces the
/// original design's misuse of a trie for id-to-string lookup (finding F2):
/// a trie answers string-to-id; this answers id-to-string.
/// </summary>
public sealed class StringPool
{
    /// <summary>A pool with no strings.</summary>
    public static readonly StringPool Empty = new([0], []);

    // value(id) = _data[_offsets[id] .. _offsets[id + 1]); one sentinel slot at the end
    private readonly int[] _offsets;
    // Every value, concatenated in id order
    private readonly char[] _data;

    // Private: only Builder.Freeze produces a consistent pair of arrays
    private StringPool(int[] offsets, char[] data)
    {
        _offsets = offsets;
        _data = data;
    }

    /// <summary>Number of stored strings; derived from the sentinel layout, so it cannot disagree with the arrays.</summary>
    public int Count => _offsets.Length - 1;

    /// <summary>The full value of <paramref name="id"/>, as a view into the pool.</summary>
    public ReadOnlySpan<char> this[StringId id]
    {
        get
        {
            // Validated against this pool's own id space
            if ((uint)id.Value >= (uint)Count)
            {
                ThrowHelper.ThrowArgumentOutOfRange(nameof(id));
            }
            int start = _offsets[id.Value];
            // A view, not a copy: the whole value, never a fragment
            return _data.AsSpan(start, _offsets[id.Value + 1] - start);
        }
    }

    /// <summary>Allocates the value of <paramref name="id"/> as a string.</summary>
    public string GetString(StringId id) =>
        // The indexer validates; new string copies once
        new(this[id]);

    /// <summary>Single-owner writer; <see cref="Freeze"/> turns it into an immutable pool exactly once.</summary>
    public sealed class Builder
    {
        // Ordinal comparer: randomized (Marvin) string hashing resists hash flooding from hostile values
        private readonly Dictionary<string, int>? _ids;
        // Insertion order defines the dense id space; nulled by Freeze
        private List<string>? _values = [];
        // long accumulator, narrowed with a check at Freeze
        private long _totalChars;

        /// <summary>
        /// Creates a builder. With <paramref name="deduplicate"/> equal values share one id
        /// (categoricals); without it every value gets its own id (free text, where a
        /// dictionary would only cost memory).
        /// </summary>
        public Builder(bool deduplicate)
        {
            // The dictionary exists only when deduplication was asked for
            _ids = deduplicate ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
        }

        /// <summary>Number of distinct ids issued so far.</summary>
        public int Count => _values?.Count ?? throw new InvalidOperationException("Builder already frozen.");

        /// <summary>Adds <paramref name="value"/> and returns its id; an existing id when deduplicating.</summary>
        public StringId Add(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            // Adding after Freeze is a use-after-transfer bug
            List<string> values = _values ?? throw new InvalidOperationException("Builder already frozen.");
            // Hit: the deduplication the original design promised but never performed
            if (_ids is not null && _ids.TryGetValue(value, out int existing))
            {
                return new StringId(existing);
            }
            // Miss: next dense id
            int id = values.Count;
            _ids?.Add(value, id);
            values.Add(value);
            _totalChars += value.Length;
            return new StringId(id);
        }

        /// <summary>Copies every value into the CSR arrays and returns the immutable pool.</summary>
        public StringPool Freeze()
        {
            List<string> values = _values ?? throw new InvalidOperationException("Builder already frozen.");
            // Sentinel slot so every id has an end offset
            int[] offsets = new int[values.Count + 1];
            // checked: more than int.MaxValue chars cannot live in one char[]
            char[] data = new char[checked((int)_totalChars)];
            int cursor = 0;
            for (int i = 0; i < values.Count; i++)
            {
                offsets[i] = cursor;
                values[i].CopyTo(data.AsSpan(cursor));
                cursor += values[i].Length;
            }
            offsets[^1] = cursor;
            // The builder is spent: its list and dictionary become garbage
            _values = null;
            _ids?.Clear();
            return new StringPool(offsets, data);
        }
    }
}
