# Succinct radix trie and schemaless catalog for .NET

A safe, allocation-conscious rewrite of the *Succinct .NET* design, built for .NET 10 and C# 14:

- a LOUDS radix trie with Elias–Fano label offsets,
- a schemaless product store,
- snapshot-based publication,
- a PostgreSQL ingestion shell,
- a minimal API.

Every line carries a short comment saying why it is written that way. The code follows dotnet/runtime conventions:

- Allman braces and `_camelCase` / `s_camelCase` field names;
- cold throws in a `ThrowHelper` marked `[DoesNotReturn]`;
- the single-compare `(uint)i >= (uint)n` bounds idiom;
- `Debug.Assert` for internal invariants.

## Layout

| Project | What it holds |
|---|---|
| `src/Succinct.Core` | `BitVector`, `PackedUIntArray`, `EliasFano`, `RadixTrie` and its builder, `StringPool`, `UnicodeGuard`, `CodePointComparer` |
| `src/Succinct.Catalog` | `AttributePath`, `AttributeValue`, `ProductStore` and its builder, `CatalogSnapshot`, `CatalogMerge`, `SnapshotHost<T>`, `InventoryLedger`, tenancy, `CanonicalForm`, `RecordTreeRenderer`, `ProductSearchIndex` |
| `src/Succinct.Catalog.Data` | PostgreSQL staging schema, tag codes and `ProductIngestRepository` (Npgsql, binary COPY) |
| `src/Succinct.Catalog.Api` | Minimal API with deny-by-default authorization, rate-limited writes, keyset paging and `If-Match` concurrency |
| `tests/*` | xUnit: differential, round-trip, concurrency, canonical-form and boundary tests, plus PostgreSQL integration tests |
| `benchmarks/Succinct.Benchmarks` | BenchmarkDotNet: rank/select, trie lookups, segmented merge versus full rebuild |

## Build and test

You need the .NET 10 SDK.

```bash
dotnet build Succinct.sln -c Release
dotnet test Succinct.sln -c Debug
```

Debug builds use checked arithmetic, so a silent overflow fails a test instead of corrupting a size.

The PostgreSQL integration tests run only when `SUCCINCT_PG` holds a connection string; otherwise they are reported as skipped:

```bash
export SUCCINCT_PG="Host=localhost;Username=postgres;Password=postgres;Database=succinct"
dotnet test tests/Succinct.Catalog.Data.Tests
```

Benchmarks run in Release:

```bash
dotnet run -c Release --project benchmarks/Succinct.Benchmarks -- --filter "*Select*"
```

CI (`.github/workflows/ci.yml`) runs on every push to `main` and on every pull request. It:

1. builds Release with warnings as errors,
2. runs every test in Debug against a PostgreSQL 17 service container,
3. re-runs the core tests with `DOTNET_EnableBMI2=0`, so the portable select path is covered too.

## The mutation model: snapshot, merge, publish

Nothing published is ever written in place:

1. **Capture once.** A reader takes `SnapshotHost<T>.Current` once per operation and reads only from that snapshot.
2. **Merge is pure.** `CatalogMerge.Apply(previous, delta)` reads its arguments, writes nothing shared, and returns a new snapshot whose version is exactly one higher.
3. **Publish linearizably.** `SnapshotHost<T>` serializes writers behind a `Lock`, so each publish replaces exactly the snapshot its merge started from. `TryApply` adds cross-process optimistic concurrency (HTTP `If-Match` → `412`).

Each upsert batch becomes one immutable `ProductStore` segment. An `ImmutableDictionary<ProductKey, RecordLocator>` maps stable keys to their rows, and compaction folds live rows back into one segment when dead rows or segment count pass a threshold. Merges therefore cost O(batch) rather than O(total).

`InventoryLedger` and `TenantTree` follow the same model at a finer grain. Each operation is a pure transformer applied by `ImmutableInterlocked.Update`, which owns the compare-and-swap retry loop.

## Findings from the review, and where each is fixed

| # | Defect in the original design | Fix |
|---|---|---|
| F1 | `BitVector.TotalOnes` counted tail padding, so `Select0` rejected valid ranks and every small trie threw | `_totalOnes = Rank1(length)`; regression tests at every word boundary |
| F2 | Interned slot (a list index) read as a trie node id; labels are fragments | `StringPool` with a typed `StringId` |
| F3 | Two writers merged from one snapshot; the later publish erased the earlier batch | `SnapshotHost<T>` single-writer gate plus a version check |
| F4 | `SequentialAccess` read column 3 before column 2 | SELECT order equals read order; typed value columns |
| F5 | Canonical form was culture-dependent, lossy, untyped and unframed | Length-prefixed fields, tag byte, invariant `"R"` formatting |
| F6 | Ids from one store applied to another; catalog ids shifted per generation | Append-only catalog; `ProductKey` and `AttributePath` across layers |
| F7 | Constructors kept caller arrays; Elias–Fano trusted "sorted" | Defensive copies from `ReadOnlySpan<T>`; monotonicity check |
| F8 | A 64-char category overflowed the formatter buffer | `Span<char>.TryWrite` with an invariant provider |
| F9 | Unchecked narrowing at the database boundary | Range checks, finite checks, explicit tag-code map |
| F10 | Row and packed-array indexes checked against storage, not meaning | Row-sliced spans; logical-length bounds |
| F11 | Append-only O(total) rebuild on every ingest | Keyed deltas, immutable segments, compaction |
| F12 | Stock lived in two places | `InventoryLedger` keyed by `ProductKey` |
| F13 | Tenant tree trusted caller-built nodes | Nodes built inside pure transformers |
| F14 | A path used as both value and group rendered twice | Rejected at ingestion with one adjacent-pair pass |
| F15 | Comment claimed key validation prevents surrogate tearing | Documented fragment contract and a test that shows it |
| F16 | Select called O(1)-amortized while searching to the end | Windows clamped on both sides; honest doc comment |
| F18 | "Body limits are off by default" (Kestrel caps at 30,000,000 bytes) | 64 KB limit set explicitly, plus a fallback authorization policy |
| F19 | Span local inside an iterator | Span work moved to a non-iterator helper |
| F20 | No authorization on writes; absent read as 0 | Role policies, rate limiting, nullable DTO fields |

F17 is a documentation correction about why `Span<T>` is a `ref struct`; it has no code.

## Running the API locally

Authorization denies everything except `/health` until a bearer token is configured. For development, `dotnet user-jwts` creates signing keys in user secrets and a token with the roles the write endpoints need:

```bash
cd src/Succinct.Catalog.Api
dotnet user-jwts create --role catalog-admin --role inventory
dotnet run
```

To enable ingestion, set `ConnectionStrings:Catalog`, preferably in user secrets rather than `appsettings.json`.

| Method and route | Policy | Purpose |
|---|---|---|
| `GET /api/products/{key}` | authenticated | One product; `ETag` carries the catalog version |
| `GET /api/products?after=&take=` | authenticated | Keyset page of products |
| `PUT /api/products/{key}` | `catalog-admin`, rate limited | Upsert; honours `If-Match` |
| `POST /api/products/{key}/stock/take` | `inventory`, rate limited | Take stock; `409` when short |
| `POST /api/catalog/ingest` | `catalog-admin`, rate limited | Merge staged PostgreSQL rows |

## Analyzer policy

Compiler, nullable, NuGet high-severity and banned-API (`RS0030`) warnings fail the build.

Code-analysis rules run at `latest-recommended` and report as warnings for now. Once CI has been clean for a while, raise `AnalysisLevel` to `latest-all` and set `CodeAnalysisTreatWarningsAsErrors` to `true` in `Directory.Build.props`.
