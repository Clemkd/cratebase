using Cratebase.App;
using Cratebase.Auth;
using Cratebase.Server;
using Cratebase.Storage.S3;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = builder.Configuration["Cratebase:DataDirectory"] ?? "./data";

builder.AddCratebase(options =>
{
    options.DataDirectory = dataDirectory;

    // The only place in the product where an engine is named. Switching to PostgreSQL is exactly
    // this change, and nothing else.
    var postgres = builder.Configuration.GetConnectionString("Postgres");

    if (!string.IsNullOrWhiteSpace(postgres))
    {
        options.UsePostgres(postgres);

        // PostgreSQL doesn't return its volume's remaining space, and a managed instance often
        // exposes none at all. The dashboard's gauge therefore only exists if the operator declares
        // it.
        options.DatabaseCapacityBytes =
            builder.Configuration.GetValue<long>("Cratebase:Postgres:CapacityBytes");
    }
    else
    {
        options.UseSqlite(
            builder.Configuration.GetConnectionString("Sqlite")
            ?? $"Data Source={Path.Combine(dataDirectory, "cratebase.db")}");
    }

    // Same logic for files: local disk by default, S3 as soon as configuration describes one. No
    // other change is needed.
    var s3 = builder.Configuration.GetSection("Cratebase:S3");

    if (s3.Exists() && !string.IsNullOrWhiteSpace(s3["Bucket"]))
    {
        options.UseS3(new S3StorageOptions
        {
            Bucket = s3["Bucket"]!,
            AccessKey = s3["AccessKey"] ?? string.Empty,
            SecretKey = s3["SecretKey"] ?? string.Empty,
            Endpoint = s3["Endpoint"] ?? string.Empty,
            PublicEndpoint = s3["PublicEndpoint"],
            Region = s3["Region"] ?? "us-east-1",
            ForcePathStyle = s3.GetValue("ForcePathStyle", true),
            // No limit can be read from a bucket: this one is either declared, or absent.
            CapacityBytes = s3.GetValue<long>("CapacityBytes"),
        });
    }
    else
    {
        options.UseLocalFiles(Path.Combine(dataDirectory, "storage"));
    }

    // External providers: Cratebase__OAuth2__Google__ClientId, etc. A provider whose credentials
    // are missing simply stays absent from the list of authentication methods.
    foreach (var provider in OAuth2Presets.All.Keys)
    {
        var section = builder.Configuration.GetSection($"Cratebase:OAuth2:{provider}");

        options.AddOAuth2(provider, section["ClientId"] ?? string.Empty, section["ClientSecret"] ?? string.Empty);
    }
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

// Before any endpoint: without it, every request is anonymous.
app.UseCratebaseAuthentication();

// After it: the log names the author of every request.
app.UseCratebaseRequestLog();

app.MapCratebase();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// The SPA is served by ASP.NET Core, not by nginx: that's what keeps the "single container"
// promise. MapStaticAssets brings content hashing and compression at publish time.
app.UseDefaultFiles();
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

await app.Services.InitializeCratebaseAsync();
await app.Services.BootstrapSuperuserAsync(app.Configuration, app.Logger);

await app.RunAsync();

/// <summary>Entry point, exposed for integration tests.</summary>
public partial class Program;
