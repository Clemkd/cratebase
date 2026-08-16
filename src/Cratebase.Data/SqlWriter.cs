using System.Text;

namespace Cratebase.Data;

/// <summary>
/// Accumulates a SQL fragment and its parameters.
/// </summary>
/// <remarks>
/// <b>Every value goes through <see cref="AddParameter"/>.</b> This is the engine's injection
/// boundary: no user value is ever concatenated into SQL text, under any circumstance. The only
/// literal fragments admitted are identifiers coming from the schema, already resolved and escaped
/// by the dialect.
/// </remarks>
public sealed class SqlWriter(ISqlDialect dialect, string parameterPrefix = "p")
{
    private readonly StringBuilder _sql = new();
    private readonly Dictionary<string, object?> _parameters = [];
    private readonly string _parameterPrefix = parameterPrefix;
    private int _next;

    /// <summary>Target dialect.</summary>
    public ISqlDialect Dialect { get; } = dialect;

    /// <summary>Accumulated parameters, indexed by name.</summary>
    public IReadOnlyDictionary<string, object?> Parameters => _parameters;

    /// <summary>Accumulated SQL text.</summary>
    public override string ToString() => _sql.ToString();

    /// <summary>Appends raw SQL text. Reserved for fragments built by the engine.</summary>
    public SqlWriter Raw(string sql)
    {
        _sql.Append(sql);
        return this;
    }

    /// <summary>Appends an identifier, escaped by the dialect.</summary>
    public SqlWriter Identifier(string name)
    {
        _sql.Append(Dialect.QuoteIdentifier(name));
        return this;
    }

    /// <summary>Appends a column reference qualified by its table alias.</summary>
    public SqlWriter Column(string tableAlias, string columnName)
    {
        _sql.Append(Dialect.QuoteIdentifier(tableAlias))
            .Append('.')
            .Append(Dialect.QuoteIdentifier(columnName));

        return this;
    }

    /// <summary>
    /// Registers a value and writes its placeholder.
    /// </summary>
    public SqlWriter Parameter(object? value)
    {
        _sql.Append(AddParameter(value));
        return this;
    }

    /// <summary>
    /// Registers a value and returns its placeholder, without writing it.
    /// </summary>
    public string AddParameter(object? value)
    {
        var name = $"{_parameterPrefix}{_next++}";
        _parameters[name] = value;

        return "@" + name;
    }

    /// <summary>Produces the finished fragment.</summary>
    public SqlFragment Build() => new(_sql.ToString(), _parameters);
}

/// <summary>
/// SQL fragment and its parameters.
/// </summary>
/// <param name="Sql">SQL text.</param>
/// <param name="Parameters">Values, indexed by parameter name without the prefix.</param>
public sealed record SqlFragment(string Sql, IReadOnlyDictionary<string, object?> Parameters)
{
    /// <summary>Empty fragment.</summary>
    public static readonly SqlFragment Empty = new(string.Empty, new Dictionary<string, object?>());

    /// <summary>Is the fragment empty?</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Sql);
}
