using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Expressions;

namespace Cratebase.Schema;

/// <summary>
/// Contrôle qu'une définition de collection est cohérente avant de toucher la base.
/// </summary>
public sealed class CollectionValidator(ISqlDialect dialect, IClock clock)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Valide une définition.
    /// </summary>
    /// <param name="collection">Définition à valider.</param>
    /// <param name="knownCollections">Noms des collections existantes, pour vérifier les relations.</param>
    /// <exception cref="CratebaseValidationException">La définition est incohérente.</exception>
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
            errors["name"] = ["Le préfixe « _ » est réservé aux collections du moteur."];
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
                Identifier.Validate(field.Name, "champ");
            }
            catch (CratebaseBadRequestException error)
            {
                messages.Add(error.Message);
            }

            if (!seenNames.Add(field.Name))
            {
                // Comparaison insensible à la casse : SQLite et PostgreSQL ne traitent pas les
                // identifiants de la même façon, donc « Titre » et « titre » ne doivent jamais
                // coexister — la collection deviendrait intransportable.
                messages.Add("Un champ de ce nom existe déjà (la casse ne distingue pas).");
            }

            if (!seenIds.Add(field.Id))
            {
                messages.Add("Deux champs partagent le même identifiant.");
            }

            if (field.Type is FieldType.Relation)
            {
                var target = field.Options.TargetCollection;

                if (string.IsNullOrWhiteSpace(target))
                {
                    messages.Add("Un champ de relation doit désigner une collection cible.");
                }
                else if (!knownCollections.Contains(target) && !string.Equals(target, collection.Name, StringComparison.Ordinal))
                {
                    messages.Add($"La collection cible « {target} » n'existe pas.");
                }
            }

            if (field.Type is FieldType.Select && field.Options.Values.Count == 0)
            {
                messages.Add("Un champ de sélection doit déclarer au moins une valeur admise.");
            }

            if (field.MaxSelect < 1)
            {
                messages.Add("Le nombre maximal de valeurs doit valoir au moins 1.");
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
                errors[$"fields.{expected.Name}"] = ["Ce champ système est obligatoire."];
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
                messages.Add("Un index de ce nom existe déjà.");
            }

            if (index.Fields.Count == 0)
            {
                messages.Add("Un index doit porter sur au moins un champ.");
            }

            foreach (var name in index.Fields)
            {
                if (collection.Field(name) is null)
                {
                    messages.Add($"Le champ « {name} » n'existe pas dans cette collection.");
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
        // ⚠️ Les règles sont compilées ici, à l'enregistrement, et non à la première requête.
        // Une règle qui référence un champ inexistant produirait sinon une erreur 400 sur chaque
        // appel de l'API — c'est-à-dire une collection cassée, découverte en production par les
        // utilisateurs plutôt que par l'administrateur qui vient de la modifier.
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
