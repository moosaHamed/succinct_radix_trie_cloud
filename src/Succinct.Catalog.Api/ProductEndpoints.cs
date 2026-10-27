using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Succinct.Catalog.Data;

namespace Succinct.Catalog.Api;

/// <summary>Names shared by policy registration and endpoint mapping, so they cannot drift apart.</summary>
internal static class Policies
{
    public const string InventoryWriter = "inventory-writer";
    public const string CatalogAdmin = "catalog-admin";
    public const string Writes = "writes";
    public const string InventoryRole = "inventory";
    public const string CatalogAdminRole = "catalog-admin";
}

// Narrow DTOs: what the wire carries, never the domain types. Nullable fields keep "absent" distinct from 0 (F20).

/// <summary>One product as the API shows it.</summary>
public sealed record ProductSummaryDto(long Key, string? Name, double? UnitPrice, long? OnHand);

/// <summary>One keyset page; pass <see cref="NextAfter"/> back as 'after' for the next page.</summary>
public sealed record ProductPageDto(IReadOnlyList<ProductSummaryDto> Items, long? NextAfter, long Version);

/// <summary>Body of a stock-take request.</summary>
public sealed record TakeStockRequest(long Quantity);

/// <summary>One attribute in an upsert: kind is uint32, int64, float32, float64, category or text.</summary>
public sealed record AttributeDto(string Path, string Kind, string Value);

/// <summary>Body of a product upsert.</summary>
public sealed record UpsertProductRequest(IReadOnlyList<AttributeDto>? Attributes);

/// <summary>Result of an ingestion run.</summary>
public sealed record IngestResultDto(long Version, int LiveRows, int Segments);

/// <summary>Minimal API endpoints. Each request captures one snapshot and reads only from it.</summary>
internal static class ProductEndpoints
{
    // Largest page and largest single stock take accepted from a caller
    private const int MaxPageSize = 100;
    private const long MaxTake = 10_000;

    // Parsed once at startup; a typo here fails the first request loudly instead of silently missing data
    private static readonly AttributePath s_name = AttributePath.Parse("name");
    private static readonly AttributePath s_unitPrice = AttributePath.Parse("unit_price");

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder products = app.MapGroup("/api/products");
        // Reads need only an authenticated caller (the fallback policy)
        products.MapGet("/{key:long}", GetProduct);
        products.MapGet("/", ListProducts);
        // Writes need a role and are rate limited
        products.MapPut("/{key:long}", UpsertProduct)
            .RequireAuthorization(Policies.CatalogAdmin)
            .RequireRateLimiting(Policies.Writes);
        products.MapPost("/{key:long}/stock/take", TakeStock)
            .RequireAuthorization(Policies.InventoryWriter)
            .RequireRateLimiting(Policies.Writes);
        app.MapPost("/api/catalog/ingest", IngestAsync)
            .RequireAuthorization(Policies.CatalogAdmin)
            .RequireRateLimiting(Policies.Writes);
        return app;
    }

    private static IResult GetProduct(long key, SnapshotHost<CatalogSnapshot> catalog, InventoryLedger ledger, HttpResponse response)
    {
        // Capture once: every read below sees the same generation
        CatalogSnapshot snapshot = catalog.Current;
        var productKey = new ProductKey(key);
        if (!snapshot.Contains(productKey))
        {
            return Results.NotFound();
        }
        // Coarse ETag: the catalog version; any change anywhere invalidates it, which is safe if not minimal
        response.Headers.ETag = EntityTag(snapshot.Version);
        return Results.Ok(ToDto(snapshot, ledger.Snapshot, productKey));
    }

    private static IResult ListProducts(long? after, int? take, SnapshotHost<CatalogSnapshot> catalog, InventoryLedger ledger)
    {
        // Clamp caller-controlled sizes before they reach any allocation
        int size = Math.Clamp(take ?? 50, 1, MaxPageSize);
        CatalogSnapshot snapshot = catalog.Current;
        ImmutableDictionary<ProductKey, long> stock = ledger.Snapshot;
        // Keyset paging over stable keys, not skip/take over positions that shift between generations
        ReadOnlySpan<ProductKey> page = snapshot.Page(after is long last ? new ProductKey(last) : null, size);
        var items = new ProductSummaryDto[page.Length];
        for (int i = 0; i < page.Length; i++)
        {
            items[i] = ToDto(snapshot, stock, page[i]);
        }
        // A full page may have more after it; a short page is the end
        long? nextAfter = page.Length == size ? page[^1].Value : null;
        return Results.Ok(new ProductPageDto(items, nextAfter, snapshot.Version));
    }

    private static IResult TakeStock(long key, TakeStockRequest request, InventoryLedger ledger)
    {
        // Range validated at the edge, before the domain sees it
        if (request.Quantity is <= 0 or > MaxTake)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["quantity"] = ["Must be between 1 and 10000."],
            });
        }
        // false means short or unknown stock: a business outcome, so 409, not 500
        return ledger.TryTake(new ProductKey(key), request.Quantity) ? Results.NoContent() : Results.Conflict();
    }

    private static IResult UpsertProduct(
        long key,
        UpsertProductRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        SnapshotHost<CatalogSnapshot> catalog,
        HttpResponse response)
    {
        // Every attribute is parsed into domain types here, once; errors are codes and field names, never echoed values
        if (!TryParseAttributes(request.Attributes, out ImmutableArray<ProductAttributeInput> inputs, out Dictionary<string, string[]> errors))
        {
            return Results.ValidationProblem(errors);
        }
        ProductDelta delta = ProductDelta.ForUpserts(new ProductUpsert(new ProductKey(key), inputs));
        try
        {
            CatalogSnapshot next;
            if (ifMatch is null)
            {
                // Unconditional write: the gate still serializes it against every other writer
                next = catalog.Apply(delta, CatalogMerge.ApplyAndMaybeCompact);
            }
            else if (!TryParseEntityTag(ifMatch, out long expected))
            {
                return Results.BadRequest();
            }
            else if (!catalog.TryApply(expected, delta, CatalogMerge.ApplyAndMaybeCompact, out next))
            {
                // Someone changed the catalog after this client read it: refuse instead of overwriting
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
            }
            response.Headers.ETag = EntityTag(next.Version);
            return Results.NoContent();
        }
        catch (ArgumentException ex)
        {
            // Domain shape rules (duplicate path, leaf-versus-group): fixed messages, safe to return
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> IngestAsync(
        [FromServices] ProductIngestRepository? repository,
        SnapshotHost<CatalogSnapshot> catalog,
        CancellationToken cancellationToken)
    {
        // Optional service: absent when no connection string is configured
        if (repository is null)
        {
            return Results.Problem(detail: "Ingestion is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        // The one real wait; the merge inside is synchronous
        CatalogSnapshot snapshot = await repository.IngestAsync(catalog, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new IngestResultDto(snapshot.Version, snapshot.LiveRows, snapshot.Segments.Length));
    }

    private static ProductSummaryDto ToDto(CatalogSnapshot snapshot, ImmutableDictionary<ProductKey, long> stock, ProductKey key)
    {
        // Each TryGet translates the path inside the generation that owns the row
        string? name = snapshot.TryGet(key, s_name, out AttributeValue nameValue) && nameValue.TryGetText(out string? text) ? text : null;
        double? price = snapshot.TryGet(key, s_unitPrice, out AttributeValue priceValue) ? AsDouble(priceValue) : null;
        // Stock comes from its single owner, never from the frozen catalog (F12)
        long? onHand = stock.TryGetValue(key, out long quantity) ? quantity : null;
        return new ProductSummaryDto(key.Value, name, price, onHand);
    }

    private static double? AsDouble(AttributeValue value)
    {
        // Every numeric kind widens to double for display; text has no price
        if (value.TryGetFloat64(out double d))
        {
            return d;
        }
        if (value.TryGetFloat32(out float f))
        {
            return f;
        }
        if (value.TryGetInt64(out long l))
        {
            return l;
        }
        if (value.TryGetUInt32(out uint u))
        {
            return u;
        }
        return null;
    }

    private static bool TryParseAttributes(
        IReadOnlyList<AttributeDto>? attributes,
        out ImmutableArray<ProductAttributeInput> inputs,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        inputs = [];
        // JSON can omit a non-nullable member; treat that as a validation error, not a crash
        if (attributes is null || attributes.Count == 0)
        {
            errors["attributes"] = ["At least one attribute is required."];
            return false;
        }
        // Same cap as the builder, enforced before any per-item work
        if (attributes.Count > ProductStoreBuilder.MaxAttributesPerRecord)
        {
            errors["attributes"] = ["Too many attributes."];
            return false;
        }
        ImmutableArray<ProductAttributeInput>.Builder builder = ImmutableArray.CreateBuilder<ProductAttributeInput>(attributes.Count);
        for (int i = 0; i < attributes.Count; i++)
        {
            AttributeDto? dto = attributes[i];
            // Field names are indexes we generate, so they are safe to echo
            string field = string.Create(CultureInfo.InvariantCulture, $"attributes[{i}]");
            if (dto is null)
            {
                errors[field] = ["Attribute is missing."];
                continue;
            }
            if (!AttributePath.TryCreate(dto.Path, out AttributePath path, out PathError pathError))
            {
                errors[field] = [$"Invalid path ({pathError})."];
                continue;
            }
            if (!TryParseValue(dto.Kind, dto.Value, out AttributeValue value))
            {
                errors[field] = ["Invalid kind or value."];
                continue;
            }
            builder.Add(new ProductAttributeInput(path, value));
        }
        if (errors.Count > 0)
        {
            return false;
        }
        inputs = builder.MoveToImmutable();
        return true;
    }

    private static bool TryParseValue(string? kind, string? text, out AttributeValue value)
    {
        value = default;
        if (kind is null || text is null)
        {
            return false;
        }
        // Invariant culture everywhere: "3,5" is not a number on this API, whatever the server's culture
        switch (kind)
        {
            case "uint32" when uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint u):
                value = AttributeValue.FromUInt32(u);
                return true;
            case "int64" when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l):
                value = AttributeValue.FromInt64(l);
                return true;
            case "float32" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f):
                value = AttributeValue.FromFloat32(f);
                return true;
            case "float64" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d):
                value = AttributeValue.FromFloat64(d);
                return true;
            case "category" when text.Length <= AttributeValue.MaxCategoricalLength && UnicodeGuard.IsWellFormed(text):
                value = AttributeValue.FromCategorical(text);
                return true;
            case "text" when text.Length <= AttributeValue.MaxFreeTextLength && UnicodeGuard.IsWellFormed(text):
                value = AttributeValue.FromFreeText(text);
                return true;
            default:
                return false;
        }
    }

    // Strong ETag carrying the catalog version
    private static string EntityTag(long version) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{version}\"");

    private static bool TryParseEntityTag(string header, out long version)
    {
        ReadOnlySpan<char> span = header.AsSpan().Trim();
        // Weak validators are not accepted for a conditional write
        if (span.Length < 2 || span[0] != '"' || span[^1] != '"')
        {
            version = 0;
            return false;
        }
        return long.TryParse(span[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
