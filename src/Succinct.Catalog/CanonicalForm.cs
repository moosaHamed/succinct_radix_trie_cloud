using System.Globalization;
using System.Text;

namespace Succinct.Catalog;

/// <summary>
/// One deterministic string per record, such that two records produce the same
/// string exactly when their content is equal (finding F5). Every field is
/// length-prefixed, so a value containing a newline or '=' is just data and can
/// never forge a field boundary; numbers use invariant, round-trip formatting;
/// and each value carries its tag, so UInt32 5, Int64 5 and the text "5" differ.
/// </summary>
/// <remarks>Field layout: <c>{pathLength}:{path}|{tag}|{textLength}:{text};</c>, sorted by path, ordinally.</remarks>
public static class CanonicalForm
{
    /// <summary>Canonical form of one stored row.</summary>
    public static string Build(ProductStore store, int row)
    {
        ArgumentNullException.ThrowIfNull(store);
        // One code path for stored rows and staged inputs, so they always agree
        return Build(store.ToInputs(row).AsSpan());
    }

    /// <summary>Canonical form of a staged record.</summary>
    public static string Build(ReadOnlySpan<ProductAttributeInput> attributes)
    {
        // (path, tag, text) triples; each value is formatted exactly once
        var fields = new (string Path, ValueTag Tag, string Text)[attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
        {
            ProductAttributeInput input = attributes[i];
            // Identity formatting: invariant culture, round-trip precision, never the display format
            fields[i] = (input.Path.Value, input.Value.Tag, input.Value.FormatInvariant());
        }
        // Paths are unique within a record, so ordinal path order is total and machine-independent
        Array.Sort(fields, static (a, b) => string.CompareOrdinal(a.Path, b.Path));
        var builder = new StringBuilder();
        foreach ((string path, ValueTag tag, string text) in fields)
        {
            // Length prefixes make every field self-delimiting; the explicit provider keeps lengths in ASCII digits
            builder.Append(CultureInfo.InvariantCulture, $"{path.Length}:{path}|{(byte)tag}|{text.Length}:{text};");
        }
        return builder.ToString();
    }
}
