using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Succinct.Catalog;
using Succinct.Catalog.Api;
using Succinct.Catalog.Data;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Kestrel already caps bodies at 30,000,000 bytes by default; small JSON commands need far less
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);

// RFC 9457 problem responses for errors, without leaking exception details
builder.Services.AddProblemDetails();

// Bearer tokens; validation settings come from configuration (dotnet user-jwts writes them in development)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

// Deny by default: an endpoint with no policy of its own still requires an authenticated caller
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.InventoryWriter, policy => policy.RequireRole(Policies.InventoryRole))
    .AddPolicy(Policies.CatalogAdmin, policy => policy.RequireRole(Policies.CatalogAdminRole));

// Rate limiting is opt-in in ASP.NET Core: declare the policy here, attach it per endpoint
builder.Services.AddRateLimiter(options =>
{
    // 429, not the default 503: the client is being told to slow down, not that the server is down
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(Policies.Writes, window =>
    {
        window.PermitLimit = 20;
        window.Window = TimeSpan.FromSeconds(1);
        // No queue: excess writes fail fast instead of piling up in memory
        window.QueueLimit = 0;
        window.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

// Category 6: one publisher per process; snapshots are immutable, so the singleton needs no further locking
builder.Services.AddSingleton(new SnapshotHost<CatalogSnapshot>(CatalogSnapshot.Empty));
// The single owner of on-hand stock
builder.Services.AddSingleton(new InventoryLedger());

// Ingestion is wired only when a connection string is configured; the endpoint reports 503 otherwise
string? connectionString = builder.Configuration.GetConnectionString("Catalog");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    // The pool-owning root; the container creates it once and disposes it at shutdown
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
    builder.Services.AddSingleton<ProductIngestRepository>();
}

WebApplication app = builder.Build();

// Unhandled exceptions become problem responses, never stack traces
app.UseExceptionHandler();
// Authentication before authorization, both before the endpoints
app.UseAuthentication();
app.UseAuthorization();
// After routing and auth, so limits can depend on the endpoint
app.UseRateLimiter();

// Liveness probe, the one anonymous endpoint
app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
// Product, stock and ingestion endpoints
app.MapProductEndpoints();

app.Run();
