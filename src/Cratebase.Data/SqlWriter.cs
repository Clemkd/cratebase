using System.Text;

namespace Cratebase.Data;

/// <summary>
/// Accumule un fragment SQL et ses paramètres.
/// </summary>
/// <remarks>
/// <b>Toute valeur passe par <see cref="AddParameter"/>.</b> C'est la frontière d'injection du
/// moteur : aucune valeur d'utilisateur n'est concaténée dans le texte SQL, jamais, sous aucun
/// prétexte. Les seuls fragments littéraux admis sont les identifiants issus du schéma, déjà
/// résolus et échappés par le dialecte.
/// </remarks>
public sealed class SqlWriter(ISqlDialect dialect, string parameterPrefix = "p")
{
    private readonly StringBuilder _sql = new();
    private readonly Dictionary<string, object?> _parameters = [];
    private readonly string _parameterPrefix = parameterPrefix;
    private int _next;

    /// <summary>Dialecte cible.</summary>
    public ISqlDialect Dialect { get; } = dialect;

    /// <summary>Paramètres accumulés, indexés par nom.</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>Texte SQL accumulé.</summary>
    public override string ToString() => _sql.ToString();

    /// <summary>Ajoute du texte SQL brut. Réservé aux fragments construits par le moteur.</summary>
    public SqlWriter Raw(string sql)
    {
        _sql.Append(sql);
        return this;
    }

    /// <summary>Ajoute un identifiant, échappé par le dialecte.</summary>
    public SqlWriter Identifier(string name)
    {
        _sql.Append(Dialect.QuoteIdentifier(name));
        return this;
    }

    /// <summary>Ajoute une référence de colonne qualifiée par son alias de table.</summary>
    public SqlWriter Column(string tableAlias, string columnName)
    {
        _sql.Append(Dialect.QuoteIdentifier(tableAlias))
            .Append('.')
            .Append(Dialect.QuoteIdentifier(columnName));

        return this;
    }

    /// <summary>
    /// Enregistre une valeur et écrit son emplacement réservé.
    /// </summary>
    public SqlWriter Parameter(object? value)
    {
        _sql.Append(AddParameter(value));
        return this;
    }

    /// <summary>
    /// Enregistre une valeur et renvoie son emplacement réservé, sans l'écrire.
    /// </summary>
    public string AddParameter(object? value)
    {
        var name = $"{_parameterPrefix}{_next++}";
        _parameters[name] = value;

        return "@" + name;
    }

    /// <summary>Produit le fragment terminé.</summary>
    public SqlFragment Build() => new(_sql.ToString(), _parameters);
}

/// <summary>
/// Fragment SQL et ses paramètres.
/// </summary>
/// <param name="Sql">Texte SQL.</param>
/// <param name="Parameters">Valeurs, indexées par nom de paramètre sans le préfixe.</param>
public sealed record SqlFragment(string Sql, IReadOnlyDictionary<string, object?> Parameters)
{
    /// <summary>Fragment vide.</summary>
    public static readonly SqlFragment Empty = new(string.Empty, new Dictionary<string, object?>());

    /// <summary>Le fragment est-il vide ?</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Sql);
}
