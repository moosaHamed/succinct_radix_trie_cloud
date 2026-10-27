using System.Collections.Immutable;
using System.Globalization;

namespace Succinct.Catalog.Tests;

/// <summary>Small builders that keep test bodies about behaviour, not setup.</summary>
internal static class TestData
{
    // A sparse, mixed-kind vocabulary; "dims" is only ever a group, never a leaf
    private static readonly string[] s_paths =
        ["name", "color", "size", "dims.width", "dims.height", "price", "stock", "id", "notes.text"];

    // Repeated categoricals with shared prefixes: the exact shape finding F2 corrupted
    private static readonly string[] s_colors = ["Red", "Rose", "Ruby", "Blue"];

    public static ProductAttributeInput Attr(string path, AttributeValue value) =>
        new(AttributePath.Parse(path), value);

    public static ProductUpsert Upsert(long key, params ReadOnlySpan<ProductAttributeInput> attributes) =>
        new(new ProductKey(key), [.. attributes]);

    public static ProductUpsert[] RandomUpserts(Random random, int count, long firstKey = 1)
    {
        var upserts = new ProductUpsert[count];
        for (int i = 0; i < count; i++)
        {
            ImmutableArray<ProductAttributeInput>.Builder attributes = ImmutableArray.CreateBuilder<ProductAttributeInput>();
            foreach (string path in s_paths)
            {
                // Roughly two thirds of the attributes per record: rows have different shapes
                if (random.Next(3) == 0)
                {
                    continue;
                }
                attributes.Add(Attr(path, RandomValue(random, path)));
            }
            upserts[i] = new ProductUpsert(new ProductKey(firstKey + i), attributes.ToImmutable());
        }
        return upserts;
    }

    public static IEnumerable<ProductAttributeInput> ByPath(IEnumerable<ProductAttributeInput> attributes) =>
        // Order-independent comparison of records
        attributes.OrderBy(static a => a.Path.Value, StringComparer.Ordinal);

    private static AttributeValue RandomValue(Random random, string path) => path switch
    {
        "name" => AttributeValue.FromFreeText("Product " + random.Next(1000).ToString(CultureInfo.InvariantCulture)),
        "color" => AttributeValue.FromCategorical(s_colors[random.Next(s_colors.Length)]),
        "size" => AttributeValue.FromUInt32((uint)random.Next(1, 50)),
        "dims.width" => AttributeValue.FromFloat32((float)random.NextDouble() * 100f),
        "dims.height" => AttributeValue.FromFloat64(random.NextDouble() * 1000),
        "price" => AttributeValue.FromFloat64(Math.Round(random.NextDouble() * 500, 3)),
        "stock" => AttributeValue.FromUInt32((uint)random.Next(0, 10_000)),
        // Wider than uint: the HVAC identifier case that motivated ValueTag.Int64
        "id" => AttributeValue.FromInt64(12_345_678_950L + random.Next(1000)),
        // Delimiters inside a value must survive storage and canonical form
        _ => AttributeValue.FromFreeText("line one\nline two = tricky;|"),
    };
}
