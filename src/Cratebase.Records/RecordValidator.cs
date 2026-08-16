using System.Globalization;
using System.Text.RegularExpressions;
using Cratebase.Core;
using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Checks a record's values against its collection's schema.
/// </summary>
/// <remarks>
/// Validation applies <b>even to superusers</b>. Bypassing access rules is an authorization
/// decision; bypassing validation would produce rows the engine can no longer read back.
/// </remarks>
public static partial class RecordValidator
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape { get; }

    /// <summary>
    /// Validates and normalizes submitted values.
    /// </summary>
    /// <param name="collection">Target collection.</param>
    /// <param name="data">Submitted values, modified in place by normalization.</param>
    /// <param name="isCreate">Creation (absent fields take their zero value) or modification.</param>
    public static void Validate(CollectionDefinition collection, RecordData data, bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(data);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var field in collection.Fields)
        {
            if (field.IsSystem && field.Name is not (SystemFields.Email or SystemFields.EmailVisibility))
            {
                // id, created, updated, password, tokenKey, verified: set by the engine, never by
                // the client. Accepting them would let identity be stolen by POSTing
                // "verified: true".
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
                messages.Add("This field is required.");
            }

            if (messages.Count > 0)
            {
                errors[field.Name] = [.. messages];
            }
        }

        // A submitted field absent from the schema is refused rather than ignored: ignoring it
        // would lead the caller to believe their data was saved. Input-only fields (password
        // confirmation) are the exception and are simply stripped — hooks read them from the raw
        // body.
        foreach (var submitted in data.Keys.ToList())
        {
            if (SystemFields.InputOnlyFields.Contains(submitted))
            {
                data.Remove(submitted);
                continue;
            }

            if (collection.Field(submitted) is null)
            {
                errors[submitted] = ["This field does not exist in this collection."];
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
                messages.Add($"This field does not admit more than {field.MaxSelect} values.");
            }

            foreach (var item in items)
            {
                ValidateScalar(field, item, messages);
            }

            return items;
        }

        // ⚠️ An array submitted to a scalar field must never reach storage: converting it to text
        // would render it "System.String[]", i.e. destroyed data with no error at all. Explicit
        // refusal.
        if (value is not string and System.Collections.IEnumerable)
        {
            messages.Add("This field only accepts a single value.");
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
                    messages.Add("This field only admits integers.");
                }

                if (options.Min is { } min && number < min)
                {
                    messages.Add($"The value must be greater than or equal to {min}.");
                }

                if (options.Max is { } max && number > max)
                {
                    messages.Add($"The value must be less than or equal to {max}.");
                }

                return number;
            }

            case FieldType.Email:
            {
                var text = AsText(value);

                if (text.Length > 0 && !EmailShape.IsMatch(text))
                {
                    messages.Add("Invalid email address.");
                }

                return text;
            }

            case FieldType.Url:
            {
                var text = AsText(value);

                if (text.Length > 0 && !Uri.TryCreate(text, UriKind.Absolute, out _))
                {
                    messages.Add("Absolute URL expected.");
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
                    messages.Add("Invalid date.");
                    return text;
                }

                return canonical;
            }

            case FieldType.Select:
            {
                var text = AsText(value);

                if (text.Length > 0 && !options.Values.Contains(text, StringComparer.Ordinal))
                {
                    messages.Add($"\"{text}\" is not an admitted value.");
                }

                return text;
            }

            case FieldType.Relation:
            {
                var text = AsText(value);

                if (text.Length > 0 && !RecordId.TryParse(text, out _))
                {
                    messages.Add($"\"{text}\" is not a valid record identifier.");
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
                    messages.Add($"This field must be at least {min} characters long.");
                }

                if (options.Max is { } max && text.Length > max)
                {
                    messages.Add($"This field cannot exceed {max} characters.");
                }

                if (options.Pattern is { Length: > 0 } pattern && text.Length > 0)
                {
                    // Bounded timeout: a pattern supplied by an administrator can backtrack
                    // catastrophically, which would then block the server on every write.
                    try
                    {
                        if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                        {
                            messages.Add("The value does not match the expected pattern.");
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        messages.Add("Pattern matching exceeded its time limit.");
                    }
                    catch (ArgumentException)
                    {
                        messages.Add("The pattern declared on this field is invalid.");
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
                messages.Add("Number expected.");
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
