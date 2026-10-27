using System.Collections.Immutable;
using System.Globalization;

namespace Succinct.Catalog;

/// <summary>One rendered line: indentation depth, segment name, and the value for leaves (null for group headers).</summary>
public readonly record struct TreeRow(int Depth, string Segment, string? Value);

/// <summary>
/// Rebuilds a record's indented tree on the fly from its own sorted paths: the
/// shared-prefix idea of the radix trie, one level up, applied to '.' segments.
/// No tree is stored. Leaf-versus-group collisions are rejected at ingestion
/// (finding F14), so no segment can render twice.
/// </summary>
public static class RecordTreeRenderer
{
    /// <summary>Renders one stored row; display values use <paramref name="provider"/>, invariant by default.</summary>
    public static ImmutableArray<TreeRow> Render(ProductStore store, int row, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Render(store.ToInputs(row).AsSpan(), provider);
    }

    /// <summary>Renders a staged record.</summary>
    public static ImmutableArray<TreeRow> Render(ReadOnlySpan<ProductAttributeInput> attributes, IFormatProvider? provider = null)
    {
        // Display formatting is the caller's choice; invariant is the safe default
        provider ??= CultureInfo.InvariantCulture;
        // Split once per path, so the grouping loop below stays linear
        var resolved = new (string Path, string[] Segments, AttributeValue Value)[attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
        {
            string path = attributes[i].Path.Value;
            resolved[i] = (path, path.Split('.'), attributes[i].Value);
        }
        // Ordinal sort: paths sharing a '.'-prefix become contiguous, the only property this algorithm needs
        Array.Sort(resolved, static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        ImmutableArray<TreeRow>.Builder rows = ImmutableArray.CreateBuilder<TreeRow>();
        // Currently open ancestor segments; bounded by AttributePath.MaxDepth
        var open = new List<string>(AttributePath.MaxDepth);
        foreach ((string Path, string[] Segments, AttributeValue Value) entry in resolved)
        {
            string[] segments = entry.Segments;
            // How much of the open path this entry still shares; the leaf itself is never a header
            int shared = 0;
            while (shared < open.Count
                && shared < segments.Length - 1
                && string.Equals(open[shared], segments[shared], StringComparison.Ordinal))
            {
                shared++;
            }
            // Close every group this path has left
            open.RemoveRange(shared, open.Count - shared);
            // Open any new intermediate groups as value-less header rows
            for (int depth = shared; depth < segments.Length - 1; depth++)
            {
                rows.Add(new TreeRow(depth, segments[depth], null));
                open.Add(segments[depth]);
            }
            // The leaf carries the display-formatted value
            rows.Add(new TreeRow(segments.Length - 1, segments[^1], entry.Value.FormatDisplay(provider)));
        }
        return rows.ToImmutable();
    }
}
