using System.Globalization;
using System.Text.RegularExpressions;
using Cratebase.Core;
using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Contrôle les valeurs d'un enregistrement contre le schéma de sa collection.
/// </summary>
/// <remarks>
/// La validation s'applique <b>y compris au superadmin</b>. Contourner les règles d'accès est une
/// décision d'autorisation ; contourner la validation produirait des lignes que le moteur ne sait
/// plus relire.
/// </remarks>
public static partial class RecordValidator
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape { get; }

    /// <summary>
    /// Valide et normalise les valeurs soumises.
    /// </summary>
    /// <param name="collection">Collection cible.</param>
    /// <param name="data">Valeurs soumises, modifiées en place par la normalisation.</param>
    /// <param name="isCreate">Création (les champs absents prennent leur valeur nulle) ou modification.</param>
    public static void Validate(CollectionDefinition collection, RecordData data, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(data);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var field in collection.Fields)
        {
            if (field.IsSystem && field.Name is not (SystemFields.Email or SystemFields.EmailVisibility))
            {
                // id, created, updated, password, tokenKey, verified : renseignés par le moteur,
                // jamais par le client. Les accepter permettrait d'usurper une identité en
                // POSTant « verified: true ».
                data.Remove(field.Name);
                continue;
            }

            if (!data.Contains(field.Name))
            {
                if (isCreate)
                {
                    data[field.Name] = field.Type.ZeroValue(field.Multiple);
                }
                else
                {
                    continue;
                }
            }

            var messages = new List<string>();
            data[field.Name] = Normalize(field, data[field.Name], messages);

            if (field.Required && IsEmpty(data[field.Name]))
            {
                messages.Add("Ce champ est obligatoire.");
            }

            if (messages.Count > 0)
            {
                errors[field.Name] = [.. messages];
            }
        }

        // Un champ soumis qui n'existe pas dans le schéma est refusé plutôt qu'ignoré : l'ignorer
        // ferait croire à l'appelant que sa donnée a été enregistrée. Les champs d'entrée pure
        // (confirmation de mot de passe) font exception et sont simplement retirés — les crochets
        // les lisent dans le corps brut.
        foreach (var submitted in data.Keys.ToList())
        {
            if (SystemFields.InputOnlyFields.Contains(submitted))
            {
                data.Remove(submitted);
                continue;
            }

            if (collection.Field(submitted) is null)
            {
                errors[submitted] = ["Ce champ n'existe pas dans cette collection."];
            }
        }

        if (errors.Count > 0)
        {
            throw new CratebaseValidationException(errors);
        }
    }

    private static object? Normalize(FieldDefinition field, object? value, List<string> messages)
    {
        if (field.Multiple)
        {
            var items = AsList(value);

            if (items.Count > field.MaxSelect)
            {
                messages.Add($"Ce champ n'admet pas plus de {field.MaxSelect} valeurs.");
            }

            foreach (var item in items)
            {
                ValidateScalar(field, item, messages);
            }

            return items;
        }

        // ⚠️ Un tableau soumis à un champ simple ne doit jamais atteindre le stockage : la
        // conversion en texte le rendrait « System.String[] », c'est-à-dire une donnée détruite
        // sans le moindre message. Refus explicite.
        if (value is not string and System.Collections.IEnumerable)
        {
            messages.Add("Ce champ n'accepte qu'une seule valeur.");
            return string.Empty;
        }

        return ValidateScalar(field, value, messages);
    }

    private static object? ValidateScalar(FieldDefinition field, object? value, List<string> messages)
    {
        var options = field.Options;

        switch (field.Type)
        {
            case FieldType.Bool:
                return value is bool flag ? flag : AsBoolean(value);

            case FieldType.Number:
            {
                var number = AsNumber(value, messages);

                if (options.IntegerOnly && number % 1 != 0)
                {
                    messages.Add("Ce champ n'admet que des entiers.");
                }

                if (options.Min is { } min && number < min)
                {
                    messages.Add($"La valeur doit être supérieure ou égale à {min}.");
                }

                if (options.Max is { } max && number > max)
                {
                    messages.Add($"La valeur doit être inférieure ou égale à {max}.");
                }

                return number;
            }

            case FieldType.Email:
            {
                var text = AsText(value);

                if (text.Length > 0 && !EmailShape.IsMatch(text))
                {
                    messages.Add("Adresse de courriel invalide.");
                }

                return text;
            }

            case FieldType.Url:
            {
                var text = AsText(value);

                if (text.Length > 0 && !Uri.TryCreate(text, UriKind.Absolute, out _))
                {
                    messages.Add("URL absolue attendue.");
                }

                return text;
            }

            case FieldType.Date or FieldType.AutoDate:
            {
                var text = AsText(value);

                if (text.Length == 0)
                {
                    return string.Empty;
                }

                if (!Timestamp.TryNormalize(text, out var canonical))
                {
                    messages.Add("Date invalide.");
                    return text;
                }

                return canonical;
            }

            case FieldType.Select:
            {
                var text = AsText(value);

                if (text.Length > 0 && !options.Values.Contains(text, StringComparer.Ordinal))
                {
                    messages.Add($"« {text} » n'est pas une valeur admise.");
                }

                return text;
            }

            case FieldType.Relation:
            {
                var text = AsText(value);

                if (text.Length > 0 && !RecordId.TryParse(text, out _))
                {
                    messages.Add($"« {text} » n'est pas un identifiant d'enregistrement valide.");
                }

                return text;
            }

            case FieldType.Json:
                return value;

            case FieldType.GeoPoint:
                return value is GeoPoint point ? point : new GeoPoint(0, 0);

            default:
            {
                var text = AsText(value);

                if (options.Min is { } min && text.Length < min)
                {
                    messages.Add($"Ce champ doit compter au moins {min} caractères.");
                }

                if (options.Max is { } max && text.Length > max)
                {
                    messages.Add($"Ce champ ne peut dépasser {max} caractères.");
                }

                if (options.Pattern is { Length: > 0 } pattern && text.Length > 0)
                {
                    // Délai borné : un motif fourni par un administrateur peut être catastrophique
                    // en retour sur trace, et bloquerait alors le serveur sur chaque écriture.
                    try
                    {
                        if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                        {
                            messages.Add("La valeur ne respecte pas le motif attendu.");
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        messages.Add("La vérification du motif a dépassé le délai imparti.");
                    }
                    catch (ArgumentException)
                    {
                        messages.Add("Le motif déclaré sur ce champ est invalide.");
                    }
                }

                return text;
            }
        }
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string text => text.Length == 0,
        IReadOnlyCollection<string> items => items.Count == 0,
        _ => false,
    };

    private static List<string> AsList(object? value) => value switch
    {
        null => [],
        string text => text.Length == 0 ? [] : [text],
        IEnumerable<string> items => [.. items],
        System.Collections.IEnumerable items => [.. items.Cast<object?>().Select(AsText)],
        _ => [AsText(value)],
    };

    private static bool AsBoolean(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        double number => number != 0,
        string text => text is not ("" or "0" or "false"),
        _ => true,
    };

    private static double AsNumber(object? value, List<string> messages)
    {
        switch (value)
        {
            case null:
                return 0d;
            case double number:
                return number;
            case string text when text.Length == 0:
                return 0d;
            case string text when double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                messages.Add("Nombre attendu.");
                return 0d;
        }
    }

    private static string AsText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
