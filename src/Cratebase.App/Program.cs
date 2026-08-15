using Cratebase.App;
using Cratebase.Auth;
using Cratebase.Server;
using Cratebase.Storage.S3;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = builder.Configuration["Cratebase:DataDirectory"] ?? "./data";

builder.AddCratebase(options =>
{
    options.DataDirectory = dataDirectory;

    // Le seul endroit du produit où un moteur est nommé. Basculer sur PostgreSQL est
    // exactement ce changement-ci, et rien d'autre.
    var postgres = builder.Configuration.GetConnectionString("Postgres");

    if (!string.IsNullOrWhiteSpace(postgres))
    {
        options.UsePostgres(postgres);
    }
    else
    {
        options.UseSqlite(
            builder.Configuration.GetConnectionString("Sqlite")
            ?? $"Data Source={Path.Combine(dataDirectory, "cratebase.db")}");
    }

    // Même logique pour les fichiers : disque local par défaut, S3 dès que la configuration en
    // décrit un. Aucun autre changement n'est nécessaire.
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
        });
    }
    else
    {
        options.UseLocalFiles(Path.Combine(dataDirectory, "storage"));
    }

    // Fournisseurs externes : Cratebase__OAuth2__Google__ClientId, etc. Un fournisseur dont les
    // identifiants manquent reste simplement absent de la liste des méthodes d'authentification.
    foreach (var provider in OAuth2Presets.All.Keys)
    {
        var section = builder.Configuration.GetSection($"Cratebase:OAuth2:{provider}");

        options.AddOAuth2(provider, section["ClientId"] ?? string.Empty, section["ClientSecret"] ?? string.Empty);
    }
});

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

// Avant tout endpoint : sans elle, chaque requête est anonyme.
app.UseCratebaseAuthentication();

// Après elle : le journal nomme l'auteur de chaque requête.
app.UseCratebaseRequestLog();

app.MapCratebase();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// La SPA est servie par ASP.NET Core, et non par nginx : c'est ce qui tient la promesse
// « un seul conteneur ». MapStaticAssets apporte l'empreinte de contenu et la compression
// au moment de la publication.
app.UseDefaultFiles();
app.MapStaticAssets();
app.MapFallbackToFile("index.html");

await app.Services.InitializeCratebaseAsync();
await app.Services.BootstrapSuperuserAsync(app.Configuration, app.Logger);

await app.RunAsync();

/// <summary>Point d'entrée, exposé pour les tests d'intégration.</summary>
public partial class Program;
