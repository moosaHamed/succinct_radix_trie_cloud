using System.Collections.Immutable;
using System.Data;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;

namespace Succinct.Catalog.Data;

/// <summary>
/// The async shell over PostgreSQL: every method awaits only at real I/O and
/// hands typed, validated data to the synchronous core. Everything that crosses
/// the wire is input, so every value is range-checked here, once (finding F9).
/// </summary>
public sealed class ProductIngestRepository
{
    // A hung query must not pin a pooled connection forever
    private const int CommandTimeoutSeconds = 30;

    // SELECT order == read order: SequentialAccess can only move forward (finding F4)
    private const string SelectStaged =
        "SELECT entity_key, path, tag, value_int8, value_float8, value_text FROM staged_attributes ORDER BY entity_key, path";

    // Binary COPY: the bulk path, no per-row round trip
    private const string CopyStaged =
        "COPY staged_attributes (entity_key, path, tag, value_int8, value_float8, value_text) FROM STDIN (FORMAT BINARY)";

    // The one pool-owning root, injected rather than created per call
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Uses <paramref name="dataSource"/>, which owns the connection pool.</summary>
    public ProductIngestRepository(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>Creates the staging table when it does not exist.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Disposal performs I/O, so it is awaited too, without capturing a context
        await using ConfiguredAsyncDisposable connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(StagingSchema.CreateTable, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using ConfiguredAsyncDisposable commandScope = command.ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every staged row.</summary>
    public async Task ClearStagedAsync(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand("DELETE FROM staged_attributes", connection) { CommandTimeout = CommandTimeoutSeconds };
        await using ConfiguredAsyncDisposable commandScope = command.ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stages <paramref name="upserts"/> with binary COPY.</summary>
    public async Task WriteStagedAsync(IEnumerable<ProductUpsert> upserts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionScope = connection.ConfigureAwait(false);
        NpgsqlBinaryImporter importer = await connection.BeginBinaryImportAsync(CopyStaged, cancellationToken).ConfigureAwait(false);
        // Disposing without Complete rolls the COPY back, so a failure part-way leaves nothing staged
        await using ConfiguredAsyncDisposable importerScope = importer.ConfigureAwait(false);
        foreach (ProductUpsert upsert in upserts)
        {
            foreach (ProductAttributeInput input in upsert.Attributes)
            {
                // Split synchronously into the three typed columns; exactly one is non-null
                (long? int8, double? float8, string? text) = SplitValue(input.Value);
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(upsert.Key.Value, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(input.Path.Value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await importer.WriteAsync(TagCodes.ToDatabase(input.Value.Tag), NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
                await WriteOrNullAsync(importer, int8, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
                await WriteOrNullAsync(importer, float8, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
                await WriteTextOrNullAsync(importer, text, cancellationToken).ConfigureAwait(false);
            }
        }
        // The commit point of the COPY
        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams every staged row into one validated, typed delta.</summary>
    public async Task<ProductDelta> ReadStagedAsync(CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionScope = connection.ConfigureAwait(false);
        var command = new NpgsqlCommand(SelectStaged, connection) { CommandTimeout = CommandTimeoutSeconds };
        await using ConfiguredAsyncDisposable commandScope = command.ConfigureAwait(false);
        // SequentialAccess streams columns in order instead of buffering whole rows (free text can be large)
        NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable readerScope = reader.ConfigureAwait(false);

        ImmutableArray<ProductUpsert>.Builder upserts = ImmutableArray.CreateBuilder<ProductUpsert>();
        ImmutableArray<ProductAttributeInput>.Builder attributes = ImmutableArray.CreateBuilder<ProductAttributeInput>();
        ProductKey? currentKey = null;

        // Cancellation reaches the socket on every row
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The stable identity is kept, never replaced by a position
            var key = new ProductKey(reader.GetInt64(0));
            // ORDER BY entity_key makes a key change the group boundary: streaming, no dictionary
            if (currentKey is ProductKey previous && previous != key)
            {
                upserts.Add(new ProductUpsert(previous, attributes.DrainToImmutable()));
            }
            currentKey = key;
            // Validated into types at the wire, so unvalidated data cannot reach the catalog
            AttributePath path = ParsePath(reader.GetString(1));
            ValueTag tag = TagCodes.FromDatabase(reader.GetInt16(2));
            attributes.Add(new ProductAttributeInput(path, ReadValue(reader, tag)));
        }
        // Flush the final group
        if (currentKey is ProductKey last)
        {
            upserts.Add(new ProductUpsert(last, attributes.DrainToImmutable()));
        }
        return new ProductDelta(upserts.ToImmutable(), []);
    }

    /// <summary>Reads the staged delta and merges it into <paramref name="host"/>; the merge itself is synchronous.</summary>
    public async Task<CatalogSnapshot> IngestAsync(SnapshotHost<CatalogSnapshot> host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        // The one real wait
        ProductDelta delta = await ReadStagedAsync(cancellationToken).ConfigureAwait(false);
        // Plain call into the sync core: no Task.Run, the merge and publish run on this thread
        return host.Apply(delta, CatalogMerge.ApplyAndMaybeCompact);
    }

    private static AttributePath ParsePath(string raw)
    {
        // Error codes, never the raw text, in the exception
        return AttributePath.TryCreate(raw, out AttributePath path, out PathError error)
            ? path
            : throw new InvalidDataException($"Staged attribute path rejected ({error}).");
    }

    // Each tag reads exactly one later column, so every read still moves forward under SequentialAccess
    private static AttributeValue ReadValue(NpgsqlDataReader reader, ValueTag tag)
    {
        try
        {
            switch (tag)
            {
                case ValueTag.UInt32:
                {
                    long raw = reader.GetInt64(3);
                    // Range-checked, never a wrapping cast (finding F9)
                    if (raw < 0 || raw > uint.MaxValue)
                    {
                        throw new InvalidDataException("Staged UInt32 value is out of range.");
                    }
                    return AttributeValue.FromUInt32((uint)raw);
                }
                case ValueTag.Int64:
                    return AttributeValue.FromInt64(reader.GetInt64(3));
                case ValueTag.Float32:
                    // Narrowing can overflow to infinity; FromFloat32 rejects non-finite values
                    return AttributeValue.FromFloat32((float)reader.GetDouble(4));
                case ValueTag.Float64:
                    return AttributeValue.FromFloat64(reader.GetDouble(4));
                case ValueTag.InternedString:
                    return AttributeValue.FromCategorical(reader.GetString(5));
                case ValueTag.FreeText:
                    return AttributeValue.FromFreeText(reader.GetString(5));
                default:
                    throw new InvalidDataException("Staged value tag has no reader.");
            }
        }
        catch (ArgumentException ex)
        {
            // A value rejected by the domain's validation is bad wire data from this layer's point of view
            throw new InvalidDataException("Staged value failed validation.", ex);
        }
    }

    private static (long? Int8, double? Float8, string? Text) SplitValue(AttributeValue value)
    {
        // Integers share the bigint column, floats the double column, text the text column
        if (value.TryGetUInt32(out uint u))
        {
            return (u, null, null);
        }
        if (value.TryGetInt64(out long l))
        {
            return (l, null, null);
        }
        if (value.TryGetFloat32(out float f))
        {
            return (null, f, null);
        }
        if (value.TryGetFloat64(out double d))
        {
            return (null, d, null);
        }
        if (value.TryGetText(out string? text))
        {
            return (null, null, text);
        }
        // Unset never reaches storage
        throw new ArgumentException("Value is unset.", nameof(value));
    }

    private static Task WriteOrNullAsync<T>(NpgsqlBinaryImporter importer, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
        where T : struct =>
        // NULL is its own wire value, not a default number
        value is T present ? importer.WriteAsync(present, type, cancellationToken) : importer.WriteNullAsync(cancellationToken);

    private static Task WriteTextOrNullAsync(NpgsqlBinaryImporter importer, string? value, CancellationToken cancellationToken) =>
        value is null ? importer.WriteNullAsync(cancellationToken) : importer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
}
