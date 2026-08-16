# Cratebase

[Français](./README.md) · **English**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![React](https://img.shields.io/badge/React-19-61DAFB?logo=react&logoColor=white)
![SQLite](https://img.shields.io/badge/SQLite-embedded-003B57?logo=sqlite&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-supported-4169E1?logo=postgresql&logoColor=white)
![Status](https://img.shields.io/badge/status-active%20development-orange)

An open source application backend built on **ASP.NET Core 10**, shipped as a single container:
runtime-defined collections, full authentication, files, realtime and a React admin console —
usable as a standalone server or as a library inside an existing application.

## Why Cratebase

Cratebase borrows the model of [PocketBase](https://pocketbase.io), a well-loved "all-in-one"
backend for getting started fast. But PocketBase is inseparable from SQLite: the day your
application outgrows what a single file can take, there is no migration — there is a rewrite.

**Cratebase exists so that day costs nothing.** SQLite to start, PostgreSQL at scale, local disk
or S3 for files — the same application code runs on both, proven by a test suite executed against
every combination. The full architecture and PocketBase feature mapping live in
[`CONCEPTION.md`](./CONCEPTION.md) (French).

> [!WARNING]
> **Under active development.** Published packages (`dotnet new cratebase`, `@cratebase/client`)
> are not available yet. Physical backups from the console are not planned (they would be
> engine-specific); a logical export is planned — see [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md)
> and [`docs/MIGRATION.md`](./docs/MIGRATION.md) (both in French). Everything else described below
> exists and works.

## Contents

- [Features](#features)
- [Getting started](#getting-started)
- [The admin console](#the-admin-console)
- [Using Cratebase as a library](#using-cratebase-as-a-library)
- [Configuration](#configuration)
- [Docker container](#docker-container)
- [Tests](#tests)
- [Repository layout](#repository-layout)

## Features

- **Dynamic collections** — defined at runtime, automatic CRUD and per-collection access rules,
  no generated code.
- **SQLite or PostgreSQL**, chosen with a single connection string.
- **Authentication** — local accounts, revocable tokens, roles and per-record grants, OAuth2
  (Google, Facebook, Microsoft, GitHub), TOTP two-factor.
- **Files** — local disk or S3 (MinIO, R2, B2), generated thumbnails, protected files.
- **Realtime** — SSE streams per collection, access rules re-evaluated at every broadcast.
- **React admin console** — collections, files, logs, instance settings.
- **REST API** usable from any client.
- **Library or server** — added to an existing ASP.NET Core application, or run standalone.

## Getting started

```bash
dotnet run --project src/Cratebase.App --urls http://localhost:8090
```

Both the API and the console are served on http://localhost:8090. To work on the console with hot
reload, in a second terminal:

```bash
cd web/admin && npm install && npm run dev
```

The first superuser is created at startup **only if the database has none**:

```bash
Cratebase__Superuser__Email=admin@example.com \
Cratebase__Superuser__Password=a-strong-password \
dotnet run --project src/Cratebase.App
```

In development, `appsettings.Development.json` already sets one up (`admin@cratebase.local`).

> [!CAUTION]
> Never put a password in a versioned file in production: always use environment variables.

## The admin console

Four destinations, in a sidebar that collapses to an icon rail:

- **Collections** — records, accounts, schema and access rules for each collection.
- **Files** — an inventory of the store: what is stored, which record references it, and what no
  longer serves anything. Orphaned objects can be isolated and deleted.
- **Logs** — served requests, access denials and administration events, with filters and a
  clickable histogram to zoom into a time slice.
- **Administration** — instance overview, settings, storage, superusers, identity providers.

**Administration → Storage** exercises the file store's configuration (write, read back, presigned
URL, delete) without ever writing it: anything carrying a secret stays in host configuration.

Two guarantees enforced by the engine, not the interface:

- the **last superuser** cannot be deleted;
- **changing a password closes every open session** for that account, including a thief's.

## Using Cratebase as a library

Cratebase can be added to an existing ASP.NET Core application without giving up its own
endpoints:

```csharp
builder.AddCratebase(o => o.UseSqlite("Data Source=./data/cratebase.db"));

var app = builder.Build();

app.UseCratebaseAuthentication();    // resolves the token — before any endpoint
app.MapCratebase();                  // /api/collections, /api/me, /api/health, the console
app.MapGet("/api/report", ...);      // your endpoints, same ICurrentUser, same database

await app.Services.InitializeCratebaseAsync();
await app.RunAsync();
```

To hook into writes, a single extension point:

```csharp
builder.Services.AddSingleton<IRecordMutationHook, MyHook>();
```

The order is fixed: **validation → hooks → create rule**. A hook therefore sees data that is
already valid, and the access rule sees the record exactly as it will be written.

## Configuration

Everything goes through ASP.NET Core configuration, so through environment variables in
production (double underscore for nesting, e.g. `Cratebase__Superuser__Email`).

| Key | Effect |
| --- | --- |
| `ConnectionStrings:Postgres` | Switches to PostgreSQL. Without it, SQLite. |
| `Cratebase:DataDirectory` | Data root: database and files. |
| `Cratebase:Superuser:Email` / `Password` | First superuser, if the database has none. |
| `Cratebase:S3:Bucket` / `AccessKey` / `SecretKey` / `Endpoint` | Switches to S3. Without them, local disk. |
| `Cratebase:S3:PublicEndpoint` | Host as seen by the browser, when it differs from the API's. |
| `Cratebase:OAuth2:{google\|facebook\|microsoft\|github}:ClientId` / `ClientSecret` | Enables an external provider. |

The rest (instance name, public URL, request logging, retention) is set from the console under
**Administration → Settings** and lives in the database: editable at runtime, no redeploy needed.

> [!WARNING]
> `PublicEndpoint` is not a convenience. A presigned URL is signed *for a given host*: if the API
> signs for `http://minio:9000` while the browser calls `https://files.example.com`, **every link
> is rejected**. It is the most expensive trap in object storage.

## Docker container

```bash
docker compose up --build
```

One image, one volume. Moving to PostgreSQL means uncommenting the service in `compose.yaml` and
setting `ConnectionStrings__Postgres` — the image does not change.

## Tests

```bash
dotnet test Cratebase.slnx
```

191 unit tests: dialects, filter language, schema planner, logs, settings.

```bash
pwsh tests/smoke.ps1
```

145 assertions against a running instance, covering the happy path **and** the hostile ones:
injection, off-schema field, unauthorised sort, locked rule, privilege escalation at sign-up,
replay of a TOTP challenge, directory traversal.

```bash
pwsh tests/postgres.ps1   # same suite, replayed against PostgreSQL 18
pwsh tests/minio.ps1      # same suite, replayed against MinIO
```

Docker is required for these last two scripts.

## Repository layout

```text
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
web/admin/                   React-TS admin console
tests/                       unit tests, smoke.ps1, postgres.ps1, minio.ps1
CONCEPTION.md                design notes and PocketBase feature mapping (French)
```

## Further reading

- [`CONCEPTION.md`](./CONCEPTION.md) — detailed architecture, trade-offs and the catalogue of
  silent failure modes avoided (French).
- [`docs/SAUVEGARDE.md`](./docs/SAUVEGARDE.md) — logical export (planned, French).
- [`docs/MIGRATION.md`](./docs/MIGRATION.md) — moving an instance that already holds data (French).
