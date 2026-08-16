using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Expressions;

namespace Cratebase.Schema;

/// <summary>
/// Checks that a collection definition is consistent before touching the database.
/// </summary>
public sealed class CollectionValidator(ISqlDialect dialect, IClock clock)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Validates a definition.
    /// </summary>
    /// <param name="collection">Definition to validate.</param>
    /// <param name="knownCollections">Names of existing collections, to check relations.</param>
    /// <exception cref="CratebaseValidationException">The definition is inconsistent.</exception>
    public void Validate(CollectionDefinition collection, IReadOnlyCollection<string> knownCollections)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(knownCollections);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        ValidateName(collection, errors);
        ValidateFields(collection, knownCollections, errors);
        ValidateIndexes(collection, errors);
        ValidateRules(collection, errors);

        if (errors.Count > 0)
        {
            throw new CratebaseValidationException(errors);
        }
    }

    private static void ValidateName(
        CollectionDefinition collection,
        Dictionary<string, string[]> errors)
    {
        try
        {
            Identifier.Validate(collection.Name, "collection");
        }
        catch (CratebaseBadRequestException error)
        {
            errors["name"] = [error.Message];
            return;
        }

        if (!collection.IsSystem && Identifier.IsSystemName(collection.Name))
        {
            errors["name"] = ["The \"_\" prefix is reserved for engine collections."];
        }
    }

    private static void ValidateFields(
        CollectionDefinition collection,
        IReadOnlyCollection<string> knownCollections,
        Dictionary<string, string[]> errors)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<RecordId>();

        foreach (var field in collection.Fields)
        {
            var key = $"fields.{field.Name}";
            var messages = new List<string>();

            try
            {
                Identifier.Validate(field.Name, "field");
            }
            catch (CratebaseBadRequestException error)
            {
                messages.Add(error.Message);
            }

            if (!seenNames.Add(field.Name))
            {
                // Case-insensitive comparison: SQLite and PostgreSQL don't treat identifiers the
                // same way, so "Title" and "title" must never coexist — the collection would
                // become unportable.
                messages.Add("A field with this name already exists (case does not distinguish).");
            }

            if (!seenIds.Add(field.Id))
            {
                messages.Add("Two fields share the same identifier.");
            }

            if (field.Type is FieldType.Relation)
            {
                var target = field.Options.TargetCollection;

                if (string.IsNullOrWhiteSpace(target))
                {
                    messages.Add("A relation field must designate a target collection.");
                }
                else if (!knownCollections.Contains(target) && !string.Equals(target, collection.Name, StringComparison.Ordinal))
                {
                    messages.Add($"Target collection \"{target}\" does not exist.");
                }
            }

            if (field.Type is FieldType.Select && field.Options.Values.Count == 0)
            {
                messages.Add("A select field must declare at least one admitted value.");
            }

            if (field.MaxSelect < 1)
            {
                messages.Add("The maximum number of values must be at least 1.");
            }

            if (messages.Count > 0)
            {
                errors[key] = [.. messages];
            }
        }

        var required = collection.Kind is CollectionKind.Auth
            ? SystemFields.ForAuth()
            : SystemFields.ForBase();

        foreach (var expected in required)
        {
            if (collection.Field(expected.Name) is null)
            {
                errors[$"fields.{expected.Name}"] = ["This system field is required."];
            }
        }
    }

    private static void ValidateIndexes(
        CollectionDefinition collection,
        Dictionary<string, string[]> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in collection.Indexes)
        {
            var key = $"indexes.{index.Name}";
            var messages = new List<string>();

            try
            {
                Identifier.Validate(index.Name, "index");
            }
            catch (CratebaseBadRequestException error)
            {
                messages.Add(error.Message);
            }

            if (!seen.Add(index.Name))
            {
                messages.Add("An index with this name already exists.");
            }

            if (index.Fields.Count == 0)
            {
                messages.Add("An index must cover at least one field.");
            }

            foreach (var name in index.Fields)
            {
                if (collection.Field(name) is null)
                {
                    messages.Add($"Field \"{name}\" does not exist in this collection.");
                }
            }

            if (messages.Count > 0)
            {
                errors[key] = [.. messages];
            }
        }
    }

    private void ValidateRules(CollectionDefinition collection, Dictionary<string, string[]> errors)
    {
        // ⚠️ Rules are compiled here, at save time, not on the first request. A rule referencing a
        // nonexistent field would otherwise produce a 400 error on every API call — that is, a
        // broken collection discovered in production by users rather than by the administrator who
        // just edited it.
        var compiler = new FilterCompiler(
            _dialect,
            new CollectionFieldResolver(collection),
            FilterRequestContext.None,
            _clock);

        foreach (var action in Enum.GetValues<CollectionAction>())
        {
            var rule = collection.Rules.For(action);

            if (rule is null)
            {
                continue;
            }

            try
            {
                compiler.Compile(rule, "t");
            }
            catch (FilterSyntaxException error)
            {
                errors[$"rules.{char.ToLowerInvariant(action.ToString()[0])}{action.ToString()[1..]}Rule"] =
                    [error.Message];
            }
        }
    }
}
