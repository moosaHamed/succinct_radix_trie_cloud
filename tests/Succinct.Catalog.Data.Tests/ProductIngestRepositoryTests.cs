using System.Collections.Immutable;
using Npgsql;

namespace Succinct.Catalog.Data.Tests;

/// <summary>Runs only when SUCCINCT_PG holds a PostgreSQL connection string; CI provides one through a service container.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "SUCCINCT_PG";

    public PostgresFactAttribute()
    {
        // Skipped, not silently passed, when no database is available
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a PostgreSQL connection string to run this test.";
        }
    }
}

// One database, so these tests must not run in parallel with each other
[Collection("postgres")]
public sealed class ProductIngestRepositoryTests
{
    [PostgresFact]
    public async Task Staged_rows_round_trip_into_a_typed_delta()
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!);
        var repository = new ProductIngestRepository(dataSource);
        await repository.EnsureSchemaAsync();
        await repository.ClearStagedAsync();

        // Every value kind, including an Int64 wider than uint and free text with delimiters
        ProductUpsert[] upserts =
        [
            Upsert(2, Attr("name", AttributeValue.FromFreeText("Motor\nline=2")), Attr("id", AttributeValue.FromInt64(12_345_678_950))),
            Upsert(1, Attr("size", AttributeValue.FromUInt32(uint.MaxValue)), Attr("w", AttributeValue.FromFloat32(1.25f)),
                Attr("h", AttributeValue.FromFloat64(-3.5)), Attr("color", AttributeValue.FromCategorical("Red"))),
        ];
        await repository.WriteStagedAsync(upserts);

        ProductDelta delta = await repository.ReadStagedAsync();
        // Grouped by key in key order, with every value back at its original kind (F4, F9)
        Assert.Equal(new long[] { 1, 2 }, delta.Upserts.Select(static u => u.Key.Value));
        foreach (ProductUpsert expected in upserts)
        {
            ProductUpsert actual = delta.Upserts.Single(u => u.Key == expected.Key);
            Assert.Equal(ByPath(expected.Attributes), ByPath(actual.Attributes));
        }

        // The delta merges like any other
        var host = new SnapshotHost<CatalogSnapshot>(CatalogSnapshot.Empty);
        CatalogSnapshot snapshot = await repository.IngestAsync(host);
        Assert.Equal(2, snapshot.LiveRows);
        await repository.ClearStagedAsync();
    }

    [PostgresFact]
    public async Task Database_constraints_reject_a_tag_in_the_wrong_column()
    {
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!);
        var repository = new ProductIngestRepository(dataSource);
        await repository.EnsureSchemaAsync();
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "INSERT INTO staged_attributes (entity_key, path, tag, value_text) VALUES (1, 'size', 1, 'not a number')");
        // Tag 1 (UInt32) must live in value_int8: the CHECK constraint makes that an invariant, not a hope
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private static ProductAttributeInput Attr(string path, AttributeValue value) => new(AttributePath.Parse(path), value);

    private static ProductUpsert Upsert(long key, params ReadOnlySpan<ProductAttributeInput> attributes) =>
        new(new ProductKey(key), [.. attributes]);

    private static IEnumerable<ProductAttributeInput> ByPath(ImmutableArray<ProductAttributeInput> attributes) =>
        attributes.OrderBy(static a => a.Path.Value, StringComparer.Ordinal);
}
