# Cratebase — design

An "ASP.NET Core PocketBase": a complete application backend, shipped as **a single container**,
with runtime-defined collections, automatic CRUD, per-collection access rules, authentication,
files, realtime, and an admin console.

**Name.** `Cratebase` — free on NuGet and npm, almost absent from GitHub, same construction as
PocketBase (container + base). Every identifier in this document derives from it
(`Cratebase.Core`, `@cratebase/client`).

---

## 1. The core constraint: grow without changing stack

This is the requirement that outranks every other one and decides most of the trade-offs below.

> When a project outgrows its starting point, **nothing needs replacing**. SQLite becomes
> PostgreSQL, local disk becomes S3, the in-memory bus becomes `LISTEN`/`NOTIFY`. Application code,
> collections, rules, TypeScript clients: unchanged.

This is precisely what PocketBase **cannot** do. PocketBase is inseparable from SQLite: its filter
language exposes `strftime()`, its rules rely on `json_extract`, its engine hardcodes SQLite SQL.
The day an application outgrows what SQLite can take, there is no migration — there is a rewrite.
**This is the defect Cratebase exists to fix**, and it is the reason to write it rather than use
PocketBase.

### 1.1 The four axes of growth

| Axis | Start (one container) | Target (scale) | What makes the switch painless |
| --- | --- | --- | --- |
| **Database** | SQLite (file) | PostgreSQL (external) | `ISqlDialect` + a conformance suite run on both |
| **Files** | local disk | S3 / MinIO / R2 | `IObjectStore`, presigned URLs on both sides |
| **Realtime** | in-memory bus | `LISTEN`/`NOTIFY` | `IRealtimeTransport`, identically named channels |
| **Instances** | 1 | N behind a load balancer | no process-memory state acts as an authority |

### 1.2 The five non-negotiable rules that follow

**R1 — No dialect-specific SQL outside the `Cratebase.Data.*` packages.**
The query engine never produces a SQL string: it produces a tree. The dialect compiles it. A
`grep` for `SELECT `/`json_extract`/`strftime` outside `Data.Sqlite`/`Data.Postgres` must come back
empty, and an architecture test verifies it.

**R2 — The filter language exposes no engine function.**
PocketBase exposes `strftime(...)`: an implementation leak that forbids migration. Cratebase
exposes **logical** functions (`dateTrunc`, `distance`, `lower`, `length`) that each dialect
translates. The grammar is the same everywhere; the SQL it produces is not.

**R3 — Identical semantics, proven by execution, not by intent.**
String sorting, `LIKE` case sensitivity, date comparison, `NULL` vs. empty string, float rounding:
all of these diverge between SQLite and PostgreSQL, **silently**. A single conformance suite runs
against both engines and fails on the smallest divergence. Without it, the portability promise is
a wish, not a fact.

**R4 — No single-writer assumption.**
SQLite in WAL mode admits only one writer; PostgreSQL admits many. Code is written for the latter
and **constrained** for the former: explicit short transactions, retry on `SQLITE_BUSY`, no
`last_insert_rowid`, no process-memory counter, application-generated identifiers.

**R5 — The schema is data, therefore it is portable.**
Collections are described in system tables. Migrating engines means re-reading that description,
regenerating the DDL for the target, then pumping the rows across. That is the structural advantage
of a dynamic-collection model, and it must be honored: `cratebase migrate-provider` is a shipped,
tested tool, not a documentation note.

---

## 2. PocketBase feature parity

Cratebase reuses PocketBase's model closely enough that most of its surface maps one-to-one.

- **Collections** — `base`, `auth`, and `view` collections, system fields (`id` as a sortable
  UUIDv7 instead of PocketBase's random 15 characters), declared indexes, cascading relation
  deletes.
- **Field types** — all of PocketBase's field types are supported (`text`, `editor`, `number`,
  `bool`, `email`, `url`, `date`, `autodate`, `select`, `file`, `relation`, `json`, `geoPoint`),
  each mapped to both a SQLite and a PostgreSQL storage representation. Dates are the single
  biggest portability trap (see §5) and are normalized at write time by the type mapper, never
  left to the caller.
- **Access rules** — the same six PocketBase rules (`listRule`, `viewRule`, `createRule`,
  `updateRule`, `deleteRule`, `manageRule`), the same three states (locked/`null`, open/`""`,
  conditional expression), and the same differentiated status codes (404 rather than 403 on
  `viewRule`, so a denial does not disclose that a row exists).
- **Records API** — list/view/create/update/delete, batch, `expand` (bounded by configuration),
  `fields` projection with `:excerpt`, write modifiers (`+`, `-`) on numeric/select/file/relation
  fields.
- **Authentication** — password auth (PBKDF2-SHA512), OAuth2 (Google, Facebook, Microsoft, GitHub),
  and TOTP two-factor (which PocketBase lacks natively). One deliberate divergence: Cratebase uses
  opaque, revocable, database-backed tokens instead of PocketBase's stateless HS256 JWTs, trading a
  per-request indexed lookup for real sign-out, a password change that invalidates open sessions,
  and a permission change that takes effect immediately.
- **Files** — multipart upload, sanitized filenames, on-the-fly thumbnails (SkiaSharp), protected
  files with short-lived file tokens, local disk or S3 storage behind `IObjectStore`.
- **Realtime** — SSE-based subscriptions (`GET /api/realtime`), the same subject shape
  (`collection/*`, `collection/{id}`), with the access rule **re-evaluated at every broadcast**
  rather than only at subscribe time.
- **Extensibility** — `AddCratebase()` / `MapCratebase()` wire the whole engine into any ASP.NET
  Core app; a hook catalog (record lifecycle, request lifecycle, auth, collections, application,
  mail) mirrors PocketBase's Go hooks, each with a `ctx.Next(ct)` chain of responsibility.
- **Operations** — request logging (buffered writes, sensitive parameters masked, purge on a
  schedule), instance settings stored as a single JSON row (no secret ever enters it), scheduled
  jobs, pluggable email sending, built-in rate limiting, and a superuser CLI. The one thing
  PocketBase does that Cratebase deliberately does not: zip-based physical backups (see §8).

One thing removed on purpose: **raw-SQL `view` collections.** PocketBase allows an arbitrary
`SELECT`; that directly violates R1 and R5. A Cratebase view is expressed in the same language as
filters, so it compiles for both engines.

---

## 3. What Cratebase adds beyond PocketBase

1. **RBAC on top of ABAC.** PocketBase only has per-collection rules (ABAC). Cratebase keeps those
   rules **and** adds a roles/permissions model: `roles`, `role_permissions`, `user_permissions`
   (individual overrides), claim-based permissions with single-level wildcards. The two compose:
   the permission guards the endpoint, the rule guards the row.
   ```
   @request.auth.perms ?~ 'posts.moderate' || owner = @request.auth.id
   ```
2. **Tested engine portability** — the whole point of §1.
3. **Multi-instance by design.** No process-memory authority anywhere; PocketBase is single-process
   by construction.

---

## 4. Package catalog

One package = one accepted external dependency.

```
Cratebase.Core             abstractions, ZERO dependency
                            logical types, Id (UUIDv7), ICurrentUser, IClock,
                            error hierarchy, PagedResult<T>, IEmailSender

Cratebase.Expressions       filter DSL: lexer, parser, AST, semantic analysis
                            ⚠️ never emits SQL — produces a predicate tree
                            ← Core

Cratebase.Data              ISqlDialect, connection factory, AST→SQL compiler,
                            transaction/retry policy, system migrator
                            ← Core, Expressions; dependency: Dapper
   ├─ .Sqlite               SqliteDialect                 ← Microsoft.Data.Sqlite
   └─ .Postgres             PostgresDialect               ← Npgsql

Cratebase.Schema            Collection/Field model, store, DDL planner,
                             SQLite table rebuilds, migration snapshots
                             ← Data

Cratebase.Records           CRUD engine: list/view/create/update/delete, expand,
                             projection, batch, validation, hooks
                             ← Schema

Cratebase.Auth              accounts, revocable tokens, RBAC, OAuth2, TOTP
                             ← Records

Cratebase.Storage           IObjectStore + LocalObjectStore + thumbnails
                             ← Core; dependency: SkiaSharp
   └─ .S3                   S3ObjectStore, dual internal/public client  ← AWSSDK.S3

Cratebase.Realtime          IRealtimeTransport + InMemory + SSE
                             ← Records

Cratebase.Admin             operations: request log (_logs) and settings (_settings)
                             ← Data; dependency: Dapper
                             ⚠️ contains NO endpoint: these are stores, called by
                             Cratebase.Server. A background worker must be able to log
                             without embedding ASP.NET Core.

Cratebase.Server            host: DI wiring, MapCratebase(), SPA serving, CLI
                             ← everything
```

npm:

```
@cratebase/client   typed SDK (pocketbase-js equivalent): CRUD, auth, files, realtime
@cratebase/admin    React-TS admin console (embedded in Cratebase.Server)
```

---

## 5. The logical type system — the core of portability

One rule: **the logical type is the authority, the physical column is a dialect detail.**

```csharp
public interface ITypeMapper
{
    string ColumnType(FieldDefinition field);          // DDL
    object? ToStorage(FieldDefinition f, object? v);   // write — NORMALIZES
    object? FromStorage(FieldDefinition f, object? v); // read
}
```

Three non-negotiable normalizations, each one a silent failure otherwise:

| Normalization | Without it |
| --- | --- |
| **Dates** → UTC, ISO-8601, fixed precision, zero-padded | Range filters return different results per engine |
| **Booleans** → strict 0/1 in SQLite | `WHERE active = true` does not match `'true'`, silently |
| **Multi-values** → canonical JSON, stable order | One engine's `?=` finds what the other misses |

Two divergences no normalization can fix, so they are resolved **in favor of the stricter engine**
(PostgreSQL), with SQLite constrained to match:

- **`LIKE` case sensitivity.** SQLite is ASCII case-insensitive, PostgreSQL is case-sensitive.
  Cratebase enforces **case-insensitive** semantics for the `~` operator: PostgreSQL gets `ILIKE`,
  SQLite gets `LIKE`.
- **String sorting.** SQLite sorts binary, PostgreSQL sorts by locale. `text` columns are created
  `COLLATE "C"` on PostgreSQL to match binary ordering, unless a collection explicitly asks
  otherwise.

---

## 6. The conformance suite

Engine portability is *the* silent failure this project must not ship. It therefore has its own
test, and that test is the central deliverable: `Cratebase.Testing` exposes a single suite,
parameterized by dialect, run against SQLite **and** PostgreSQL (Testcontainers) on every CI run.

```csharp
public class SqliteConformance   : CratebaseDialectConformanceSuite<SqliteFixture> { }
public class PostgresConformance : CratebaseDialectConformanceSuite<PostgresFixture> { }
```

Coverage: every field type round-trip, every DSL operator on every type, date macros across
timezone/year boundaries, sort stability and `NULL` ordering, access rules (including the hostile
case: deleting a row outside scope must return 404 and **destroy nothing**), DDL operations
(including SQLite table rebuilds and unique-index enforcement), and a full switch-over test — a
schema and dataset written on SQLite, migrated by `migrate-provider`, with every prior assertion
replayed against the resulting PostgreSQL database.

### What execution actually found

The end-to-end suite (`tests/smoke.ps1`) replayed against PostgreSQL 18 (`tests/postgres.ps1`)
passes on both engines — but not on the first try. Four portability defects surfaced, all from the
same family: code that bypassed `ToStorage` (a date or JSON value written as text, accepted by
SQLite, rejected by PostgreSQL with a type mismatch), plus an ORM `IN @list` expansion that worked
on one driver and not the other. None came from the dialects themselves — all came from code that
routed around the normalization layer built specifically to prevent this. The fix generalized §5's
rule: **the parameter's type is also a dialect detail**, and it cannot be inferred from the value
alone, hence `ISqlDialect.BindParameter`, which casts the placeholder explicitly rather than
trusting the driver.

---

## 7. The request pipeline — order is the security model

```
1.  Authentication          → principal, or anonymous
2.  Permission (RBAC)       → is the endpoint open to this role?             403 otherwise
3.  Collection resolution   → 404 if unknown
4.  Access rule (ABAC)      → compiled to a SQL predicate
5.  User filter             → compiled to a SQL predicate, identifiers ALLOW-LISTED
6.  Composition              → predicate = rule AND filter          ⚠️ never the reverse
7.  Bounds                  → perPage capped, expand depth capped, sort allow-listed
8.  Execution                → a single parameterized query
9.  Projection (fields)      → after the database, never to widen it
10. Expand                   → each relation re-checks the viewRule of ITS OWN collection
```

Four sealed invariants, each with its own hostile test: an identifier absent from the collection's
schema is a 400, never an interpolation; the rule can only ever be *added* (the compiler's API has
no way to produce an `OR` between rule and filter — a `sealed` method, not a convention); writes
put the rule in the `UPDATE`/`DELETE`'s `WHERE` clause and check the affected row count (zero rows
→ 404); `createRule` evaluates against the proposed record after defaults and hooks run, never
against the raw request body.

---

## 8. Deployment: one container, then several

```
┌─ single container (start) ──────────────────────┐
│  ASP.NET Core 10 (Kestrel)                       │
│   ├── React-TS SPA (MapStaticAssets + fallback)  │
│   ├── /api/collections/*   CRUD engine           │
│   ├── /api/files/*         local disk            │
│   ├── /api/realtime        SSE, in-memory bus    │
│   └── /api/admin/*         console               │
│  volume: /data → cratebase.db, storage/, backups │
└───────────────────────────────────────────────────┘

                     ↓ scaling up: configuration only

┌─ N containers ──┐   ┌ PostgreSQL ┐   ┌ MinIO / S3 ┐
│  same image     │──▶│  + NOTIFY  │   │            │
└─────────────────┘   └────────────┘   └────────────┘
```

The SPA is **served by ASP.NET Core**, not by nginx — that's what keeps the "single container"
promise. Runtime configuration is exposed through a server-generated `/config.js` endpoint.

### Planned: logical export and engine/storage migration

Physical backup (zipping the SQLite file, or `VACUUM INTO` vs. `pg_dump`) is **rejected, not
postponed**: it would necessarily be engine-specific, which is exactly what R1 forbids. What is
planned instead is a **logical export** — a `.crate` ZIP archive (JSON Lines records, collection
definitions, files) that goes through the collection model and knows no engine, produced inside a
single read transaction with cursor-based pagination (never `OFFSET`, to stay correct against a
live table). It is not a replacement for an operator's own database backups; it answers a different
question — getting data out and putting it back somewhere else, including the other engine.

Moving an already-populated instance between engines or storage backends is the same shape of
problem: a single process that holds both a source and a target connection, copies collection by
collection with a resumable cursor, verifies row counts and a sampled diff before declaring success,
and never writes to the source. The tool reports the configuration variable to set — it does not
flip it itself.

---

## 9. Catalogue of silent failures

Every row justifies code and a test, not a documentation rule. Rows marked ⚠️ were **actually
encountered** during development, not merely anticipated.

| Failure | What you'd see | What actually happens |
| --- | --- | --- |
| Fields matched by name instead of their identifier | nothing | ⚠️ *encountered* — a rename becomes `DROP` + `ADD`, **the column comes back empty** |
| Kestrel bound to `[::]` in a container without dual-stack | healthy container, normal logs | ⚠️ *encountered* — nothing listens on IPv4, so the published port never resolves |
| `/dev/tcp` in a `HEALTHCHECK` | container marked "unhealthy" | ⚠️ *encountered* — the image only has `dash`, which lacks that redirection |
| Project missing from the Dockerfile's `COPY *.csproj` list | late, opaque error | `project.assets.json not found`, several steps past the real cause |
| Array written into a field presumed scalar | nothing | ⚠️ *encountered* — the text conversion literally stores `System.String[]` |
| Write bypassing `ToStorage` | nothing on SQLite | ⚠️ *encountered ×3* — text date into a `timestamptz`, text JSON into a `jsonb`: accepted by SQLite, rejected by PostgreSQL on switch-over |
| ORM auto-expansion of `IN @list` | nothing on SQLite | ⚠️ *encountered* — not expanded depending on the driver: the array is sent as a single parameter and the query is rejected |
| Suppressed last superuser | 204, all fine | the instance becomes unadministrable: system collections are locked, and bootstrap only recreates an account if configuration carries one |
| Password change that revokes nothing | password changes, user assumes they're safe | ⚠️ *encountered* — only the signing key rotated, and nothing consulted it when resolving a token: a stolen session stayed open |
| Empty field sent for an unrelated field | "address is required" on a password-change form | ⚠️ *encountered* — a shared form always sent the address, empty when it wasn't being asked for; the engine reads that as "clear the address" |
| Settings `PATCH` deserialized as a full object | the screen shows what was sent | a client sending only the name would silently reset retention and IP collection to defaults; fixed with an optional-properties payload merged onto the existing settings |
| Non-normalized SQLite date | nothing | range filters break after switching engines |
| Boolean stored as `'true'` | nothing | `WHERE active = 1` finds nothing |
| `LIKE` case-sensitive on PostgreSQL only | nothing in dev | search silently stops working in production |
| Sort without an allow-list | nothing | ordering leaks values that shouldn't be readable |
| Unbounded `perPage` | slowness | denial of service from a single request |
| SQLite table rebuild dropping an index | nothing | uniqueness lost, silent duplicates |
| In-memory realtime bus, 2 instances | nothing | half the clients receive nothing |
| Realtime rule evaluated only at subscribe time | nothing | updates keep arriving after access is revoked |
| Presigned URL signed for the wrong host | links rejected | browser and API don't see the object store at the same address |

---

## 10. Milestones

Each milestone is shippable and tested. Order is driven by risk: the most uncertain work first.

| # | Milestone | Content | Exit criterion |
| :-: | --- | --- | --- |
| **0** | **The chain** | Solution, `Directory.Packages.props`, SPA served by ASP.NET Core, SQLite, single-container Dockerfile, CI | `docker run` serves a page |
| **1** | **Types + dialect** | `Core`, `Data`, `SqliteDialect`, system migrator, **and `PostgresDialect` from the start** | conformance suite green on both engines |
| **2** | **Dynamic schema** | `Schema`: collections, fields, indexes, DDL, SQLite rebuilds, migration snapshots | creating a collection via the API creates the table |
| **3** | **Filter DSL** | `Expressions`: lexer, parser, AST; AST→SQL compiler in `Data` | `?filter=` works identically on both engines |
| **4** | **CRUD** | `Records`: list/view/create/update/delete, sort, expand, fields, batch, hooks | PocketBase-compatible API on `base` collections |
| **5** | **Auth + Authz** | `Auth` (accounts, tokens, OAuth2, TOTP), RBAC + ABAC wired to the pipeline (§7) | rules and permissions enforced, hostile tests green |
| **6** | **Files** | Local `Storage`, upload, thumbnails, protected files + tokens | `file` fields operational |
| **7** | **Console** | `@cratebase/admin`: collections, records, users, roles, settings, logs | full administration without SQL |
| **8** | **Realtime** | SSE `Realtime` + `InMemory`, rules re-evaluated at broadcast | PocketBase-compatible subscriptions |
| **9** | **Library** | Public hooks, `MapCratebase()`, NuGet packages, `dotnet new cratebase`, `@cratebase/client` | a third-party project embeds it |
| **10** | **Scale** | `Data.Postgres` in production, `Storage.S3`, `migrate-provider` | switch-over proven by the §6 test |

**Milestone 1 writes both dialects at once.** Counter-intuitive — PostgreSQL isn't used in
production until milestone 10 — but it's the only way to stop SQLite assumptions from leaking into
everything downstream. A dialect written as an afterthought discovers too late that ten decisions
need undoing. That is the price of the portability promise, and it is paid once, up front, or paid
many times over.

---

## 11. Out of scope

- **Raw SQL in `view` collections** — contrary to R1 and R5 (§2).
- **GraphQL** — the filter DSL covers the need; two query languages means two authorization
  surfaces.
- **Multi-tenancy** — no dedicated mechanism. A collection with an `organization` relation and an
  access rule already does the job; revisit if the need becomes structural.
- **Edge functions** — moot: this is all of ASP.NET Core.
