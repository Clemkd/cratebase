using System.Text.Json;
using Cratebase.Core;
using Cratebase.Records;
using Cratebase.Schema;
using Cratebase.Storage;
using Microsoft.AspNetCore.Http;

namespace Cratebase.Server;

/// <summary>Fichiers importés au cours d'une écriture, pour pouvoir les défaire.</summary>
public sealed class UploadedFiles
{
    private readonly List<string> _keys = [];

    /// <summary>Enregistre une clé écrite.</summary>
    public void Track(string key) => _keys.Add(key);

    /// <summary>
    /// Supprime les objets écrits.
    /// </summary>
    /// <remarks>
    /// Appelé quand l'écriture en base échoue après l'import. Sans ce rattrapage, une règle de
    /// création qui refuse laisserait les octets sur le disque sans aucune ligne pour les
    /// désigner : on les facture, on les sauvegarde, et plus rien ne sait qu'ils existent.
    /// </remarks>
    public async Task RollbackAsync(IObjectStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        foreach (var key in _keys)
        {
            try
            {
                await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Un échec de nettoyage ne doit pas masquer l'erreur d'origine, qui est la vraie
                // information à remonter à l'appelant.
            }
        }

        _keys.Clear();
    }
}

/// <summary>
/// Lecture d'un corps de requête, en JSON ou en multipart.
/// </summary>
public static class FileUploads
{
    /// <summary>Le corps est-il un formulaire multipart ?</summary>
    public static bool IsMultipart(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.HasFormContentType
            && request.ContentType?.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Lit un corps multipart : champs ordinaires, puis fichiers importés dans le magasin.
    /// </summary>
    public static async Task<(RecordData Data, UploadedFiles Files)> ReadMultipartAsync(
        HttpRequest request,
        CollectionDefinition collection,
        RecordId recordId,
        IObjectStore store,
        IReadOnlyDictionary<string, object?>? original,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(store);

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var data = new RecordData();
        var uploaded = new UploadedFiles();

        foreach (var entry in form)
        {
            // Les champs de fichier arrivent par form.Files ; ici on ne traite que le reste.
            var name = entry.Key;
            var field = collection.Field(TrimModifier(name));

            if (field?.Type is FieldType.File)
            {
                continue;
            }

            data[name] = field?.Multiple == true
                ? entry.Value.Select(value => value ?? string.Empty).ToArray()
                : ParseScalar(entry.Value.ToString());
        }

        foreach (var field in collection.Fields.Where(f => f.Type is FieldType.File))
        {
            var files = form.Files.GetFiles(field.Name).ToList();
            files.AddRange(form.Files.GetFiles(field.Name + "+"));

            var append = form.Files.GetFiles(field.Name + "+").Count > 0;
            var removals = form[field.Name + "-"].Select(value => value ?? string.Empty).ToList();

            if (files.Count == 0 && removals.Count == 0 && !form.ContainsKey(field.Name))
            {
                continue;
            }

            var existing = append || removals.Count > 0
                ? ExistingFiles(original, field.Name)
                : [];

            var kept = existing.Where(name => !removals.Contains(name, StringComparer.Ordinal)).ToList();

            foreach (var file in files)
            {
                Validate(field, file);

                var fileName = ObjectKey.NewFileName(file.FileName);
                var key = ObjectKey.For(collection.Name, recordId.ToString(), fileName);

                await using var content = file.OpenReadStream();

                await store.PutAsync(
                        key,
                        content,
                        file.ContentType ?? ObjectKey.ContentTypeOf(fileName),
                        cancellationToken)
                    .ConfigureAwait(false);

                uploaded.Track(key);
                kept.Add(fileName);
            }

            if (kept.Count > field.MaxSelect)
            {
                throw CratebaseValidationException.ForField(
                    field.Name, $"Ce champ n'admet pas plus de {field.MaxSelect} fichiers.");
            }

            data[field.Name] = field.Multiple ? kept : kept.FirstOrDefault() ?? string.Empty;
        }

        return (data, uploaded);
    }

    private static void Validate(FieldDefinition field, IFormFile file)
    {
        var limit = field.Options.MaxFileSize ?? 5 * 1024 * 1024;

        if (file.Length > limit)
        {
            throw CratebaseValidationException.ForField(
                field.Name, $"Le fichier dépasse la taille autorisée ({limit} octets).");
        }

        if (field.Options.MimeTypes.Count == 0)
        {
            return;
        }

        // On se fie au type déclaré ET à l'extension : le premier est fourni par le client, donc
        // falsifiable, mais le refuser seul sur l'extension bloquerait des envois légitimes.
        var declared = file.ContentType ?? string.Empty;
        var guessed = ObjectKey.ContentTypeOf(file.FileName);

        if (!field.Options.MimeTypes.Contains(declared, StringComparer.OrdinalIgnoreCase)
            && !field.Options.MimeTypes.Contains(guessed, StringComparer.OrdinalIgnoreCase))
        {
            throw CratebaseValidationException.ForField(
                field.Name, $"Type de fichier non autorisé : « {declared} ».");
        }
    }

    private static List<string> ExistingFiles(
        IReadOnlyDictionary<string, object?>? original,
        string fieldName) => original?.GetValueOrDefault(fieldName) switch
        {
            IEnumerable<string> many => [.. many],
            string single when single.Length > 0 => [single],
            _ => [],
        };

    private static string TrimModifier(string name) =>
        name.Length > 1 && name[^1] is '+' or '-' ? name[..^1] : name;

    private static object? ParseScalar(string raw) => raw switch
    {
        "true" => true,
        "false" => false,
        _ when double.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        _ => raw,
    };

    /// <summary>Lit un corps JSON.</summary>
    public static RecordData ReadJson(JsonElement body)
    {
        var data = new RecordData();

        if (body.ValueKind is not JsonValueKind.Object)
        {
            return data;
        }

        foreach (var property in body.EnumerateObject())
        {
            data[property.Name] = RecordData.FromJson(property.Value);
        }

        return data;
    }
}
