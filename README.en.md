# Cratebase

[Français](./README.md) · **English**

Cratebase is an open source application backend built on ASP.NET Core 10 and shipped as **a single
container**. It includes:

- **collections defined at runtime**, with automatic CRUD and per-collection access rules;
- an **embedded SQLite database — or PostgreSQL**, chosen by configuration;
- **authentication**: local accounts, revocable tokens, roles and per-record grants, OAuth2 (Google,
  Facebook, Microsoft, GitHub), TOTP two-factor;
- **files** on local disk — or S3 —, thumbnails and protected files included;
- a React **admin console**: collections, records, logs, settings;
- a **REST API** usable from any client.

> Design notes, the full PocketBase feature mapping and the catalogue of silent failure modes live
> in [`CONCEPTION.md`](./CONCEPTION.md) (French).

> ⚠️ **Under active development.** Realtime (SSE) and published packages (`dotnet new cratebase`,
> `@cratebase/client`) are not there yet. **Physical** backups from the console are ruled out rather
> than postponed: they would necessarily be engine-specific (`VACUUM INTO` on one side, `pg_dump` on
> the other). A **logical export** is planned — [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md), and
> [`docs/MIGRATION.md`](./docs/MIGRATION.md) for moving an instance that already holds data (both in
> French). Everything else listed above exists.

## Why not PocketBase?

[PocketBase](https://pocketbase.io) is excellent, and Cratebase borrows its model wholesale. The
difference fits in one sentence: **Cratebase migrates, PocketBase gets replaced.**

PocketBase is inseparable from SQLite. The day your application outgrows what a single file can
take — several writers, several instances, a managed database — there is no migration: there is a
rewrite, and the clients you already shipped pay for it.

Cratebase is written so that day costs nothing:

| Axis | Start | At scale | What you change |
| --- | --- | --- | --- |
| Database | SQLite (file) | PostgreSQL | one connection string |
| Files | local disk | S3, MinIO, R2, B2 | four variables |
| Instances | one | N behind a load balancer | nothing |

The same end-to-end suite runs on SQLite and on PostgreSQL, on local disk and on S3.

In exchange, PocketBase has what Cratebase does not: realtime, backups from the console, a single
binary to download and a published SDK ecosystem.

## Getting started

```bash
dotnet run --project src/Cratebase.App --urls http://localhost:8090
```

Both the API and the console are on http://localhost:8090. To work on the console with hot reload,
in a second terminal:

```bash
cd web/admin && npm install && npm run dev
```

The first superuser is created at startup **only if the database has none**, from configuration:

```bash
Cratebase__Superuser__Email=admin@example.com Cratebase__Superuser__Password=a-strong-password dotnet run --project src/Cratebase.App
```

In development, `appsettings.Development.json` already sets one up (`admin@cratebase.local`).
**Never put a password in a versioned file in production**: use environment variables.

## The console

Three destinations in a sidebar that collapses to an icon rail:

- **Collections** — records, accounts, schema and access rules for each collection.
- **Logs** — served requests, access denials and administration events. Filters sit under the header
  of the column they restrict; clicking a histogram bar opens that time slice and redraws it one
  step finer.
- **Administration** — instance overview, settings, superusers, identity providers.

Two guarantees enforced by the engine rather than the interface: **the last superuser cannot be
deleted** — without them nobody can administer the instance any more — and **changing a password
closes that account's open sessions**, including a thief's.

## From your own application

Cratebase is also a library: add it to an existing ASP.NET Core application and keep writing your
own endpoints.

```csharp
builder.AddCratebase(o => o.UseSqlite("Data Source=./data/cratebase.db"));

var app = builder.Build();

app.UseCratebaseAuthentication();    // resolves the token — before any endpoint
app.MapCratebase();                  // /api/collections, /api/me, /api/health, the console
app.MapGet("/api/report", ...);      // your endpoints, same ICurrentUser, same database

await app.Services.InitializeCratebaseAsync();
await app.RunAsync();
```

To hook into writes, implement `IRecordMutationHook` and register it:

```csharp
builder.Services.AddSingleton<IRecordMutationHook, MyHook>();
```

The order is fixed: **validation, then hooks, then the create rule**. A hook therefore sees data
that is already valid, and the access rule sees the record exactly as it will be written.

## Configuration

Everything goes through ASP.NET Core configuration, so through environment variables in production
(`Cratebase__Superuser__Email` — double underscore for nesting).

| Key | Effect |
| --- | --- |
| `ConnectionStrings:Postgres` | Switches to PostgreSQL. Without it, SQLite. |
| `Cratebase:DataDirectory` | Data root: database and files. |
| `Cratebase:Superuser:Email` / `Password` | First superuser, if the database has none. |
| `Cratebase:S3:Bucket` / `AccessKey` / `SecretKey` / `Endpoint` | Switches to S3. Without them, local disk. |
| `Cratebase:S3:PublicEndpoint` | Host as seen by the **browser**, when it differs from the one the API sees. |
| `Cratebase:OAuth2:{google\|facebook\|microsoft\|github}:ClientId` / `ClientSecret` | Enables an external provider. |

The rest — instance name, public URL, request logging, retention — is set from the console
(**Administration → Settings**) and lives in the database: anything carrying a secret or naming an
engine stays in host configuration, the rest is editable at runtime.

> ⚠️ `PublicEndpoint` is not a convenience. A presigned URL is signed *for a given host*: if the API
> signs for `http://minio:9000` while the browser calls `https://files.example.com`, **every link is
> rejected**. It is the most expensive trap in object storage.

## Container

```bash
docker compose up --build
```

One image, one volume. Moving to PostgreSQL means uncommenting the service in `compose.yaml` and
setting `ConnectionStrings__Postgres` — the image does not change.

## Verifying

```bash
dotnet test Cratebase.slnx
```

186 unit tests: dialects, filter language, schema planner, logs, settings.

```bash
pwsh tests/smoke.ps1
```

110 assertions against a running instance. They cover the happy path **and** the hostile ones:
injection, off-schema field, unauthorised sort, locked rule, privilege escalation at sign-up, replay
of a two-factor challenge, directory traversal on files.

```bash
pwsh tests/postgres.ps1
pwsh tests/minio.ps1
```

The same suite, replayed against PostgreSQL 18 and then against MinIO, each started in a container.
Docker required.

<details>
<summary><b>Repository layout</b></summary>

```
src/
  Cratebase.Core            abstractions — no external dependency
  Cratebase.Expressions     filter language — produces a tree, never SQL
  Cratebase.Data{,.Sqlite,.Postgres}
                            SQL dialect, compilation, transactions
  Cratebase.Schema          dynamic collections, DDL, migrations
  Cratebase.Records         CRUD engine, rule enforcement, mutation hooks
  Cratebase.Auth            accounts, revocable tokens, RBAC, OAuth2, TOTP
  Cratebase.Admin           request log, instance settings
  Cratebase.Storage{,.S3}   IObjectStore, local disk, SkiaSharp thumbnails
  Cratebase.Server          DI wiring and endpoints — AddCratebase() / MapCratebase()
  Cratebase.App             the runnable application
web/admin/                  React-TS admin console
tests/                      unit tests, smoke.ps1, postgres.ps1, minio.ps1
CONCEPTION.md               the plan (French)
```

</details>
