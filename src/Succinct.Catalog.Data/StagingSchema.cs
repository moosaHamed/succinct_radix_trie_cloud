namespace Succinct.Catalog.Data;

/// <summary>
/// The staging table, as one source of truth for production setup and tests.
/// Typed value columns plus CHECK constraints make "exactly one value per row,
/// in the column its tag names" a database invariant, not an assumption the
/// reader has to hope for (finding F4 read one column as three types).
/// </summary>
public static class StagingSchema
{
    /// <summary>Idempotent DDL for the staging table.</summary>
    public const string CreateTable =
        """
        CREATE TABLE IF NOT EXISTS staged_attributes (
            entity_key   bigint           NOT NULL,
            path         text             NOT NULL,
            tag          smallint         NOT NULL,
            value_int8   bigint,
            value_float8 double precision,
            value_text   text,
            PRIMARY KEY (entity_key, path),
            CHECK (num_nonnulls(value_int8, value_float8, value_text) = 1),
            CHECK (
                (tag IN (1, 2) AND value_int8 IS NOT NULL)
             OR (tag IN (3, 4) AND value_float8 IS NOT NULL)
             OR (tag IN (5, 6) AND value_text IS NOT NULL)
            )
        )
        """;
}

/// <summary>
/// Database tag codes. These numbers are a contract with stored data; the
/// in-memory <see cref="ValueTag"/> layout is not, so the mapping is explicit
/// and an unknown code fails at the wire instead of three layers later (F9).
/// </summary>
public static class TagCodes
{
    /// <summary>Maps a stored code to a tag; throws <see cref="InvalidDataException"/> for an unknown code.</summary>
    public static ValueTag FromDatabase(short code) => code switch
    {
        1 => ValueTag.UInt32,
        2 => ValueTag.Int64,
        3 => ValueTag.Float32,
        4 => ValueTag.Float64,
        5 => ValueTag.InternedString,
        6 => ValueTag.FreeText,
        // The code is a small number from our own table, safe to report
        _ => throw new InvalidDataException($"Unknown value tag code {code}."),
    };

    /// <summary>Maps a tag to its stored code.</summary>
    public static short ToDatabase(ValueTag tag) => tag switch
    {
        // Explicit short constants, so the switch's type is short without relying on target typing
        ValueTag.UInt32 => (short)1,
        ValueTag.Int64 => (short)2,
        ValueTag.Float32 => (short)3,
        ValueTag.Float64 => (short)4,
        ValueTag.InternedString => (short)5,
        ValueTag.FreeText => (short)6,
        // Unset never reaches storage
        _ => throw new ArgumentOutOfRangeException(nameof(tag)),
    };
}
