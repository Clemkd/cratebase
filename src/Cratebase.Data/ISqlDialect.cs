using System.Data.Common;
using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Translation of Cratebase's logical model to a given SQL engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only place in the code where engine-specific SQL is allowed to exist.</b> Rule
/// R1 of the design document: a <c>grep</c> for <c>SELECT</c>, <c>json_extract</c>, or
/// <c>strftime</c> outside the <c>Cratebase.Data.*</c> packages must come back empty.
/// </para>
/// <para>
/// Any method added here must be covered by the conformance suite, which runs the same assertions
/// against every implementation. A method whose two dialects don't return the same result is a
/// portability bug, not an acceptable difference.
/// </para>
/// </remarks>
public interface ISqlDialect
{
    /// <summary>Dialect name, for logs and error messages.</summary>
    string Name { get; }

    /// <summary>Escapes an identifier (table, column, alias).</summary>
    string QuoteIdentifier(string name);

    /// <summary>Column type corresponding to a logical field.</summary>
    string ColumnType(FieldType type, bool multiple);

    /// <summary>
    /// Converts a value from the logical model to its storage representation.
    /// </summary>
    /// <remarks>
    /// Mandatory normalization point: booleans to 0/1 on SQLite, dates to canonical form,
    /// multi-values to JSON with stable order. Without it, the same data produces different filter
    /// results depending on the engine.
    /// </remarks>
    object? ToStorage(FieldType type, bool multiple, object? value);

    /// <summary>Converts a storage value to the logical model.</summary>
    object? FromStorage(FieldType type, bool multiple, object? value);

    /// <summary>
    /// Adapts a parameter's placeholder to the target column's type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the placeholder unchanged in most cases. PostgreSQL, however, refuses to write a
    /// string into a <c>jsonb</c> column — <i>column is of type jsonb but expression is of type
    /// text</i> — and requires an explicit cast.
    /// </para>
    /// <para>
    /// The cast goes through SQL rather than through the driver, deliberately: parameters are
    /// carried by anonymous objects whose properties are typed <c>object</c>, and drivers' typing
    /// mechanisms resolve against the <i>declared</i> type. They therefore never see the actual
    /// value. SQL does not make that mistake.
    /// </para>
    /// </remarks>
    string BindParameter(FieldType type, bool multiple, string placeholder);

    /// <summary>
    /// Renders a "contains" comparison, <b>always case-insensitive</b>.
    /// </summary>
    /// <remarks>
    /// SQLite is ASCII case-insensitive, PostgreSQL is case-sensitive. Cratebase aligns on
    /// case-insensitive because that's the behavior observed in development: the opposite would
    /// make search stop working the moment the app reaches production, without any error.
    /// </remarks>
    string LikeExpression(string valueExpression, string patternPlaceholder, bool negated);

    /// <summary>Renders a lowercase conversion.</summary>
    string Lower(string expression);

    /// <summary>Renders the element count of a multi-valued value.</summary>
    string ArrayLength(string expression);

    /// <summary>
    /// Renders a test: "at least one element of the multi-valued value satisfies the comparison".
    /// </summary>
    /// <param name="expression">Expression of the multi-valued column.</param>
    /// <param name="comparison">
    /// Comparison template where <c>{0}</c> stands for the current element. Example: <c>{0} = @p3</c>.
    /// </param>
    string AnyElementMatches(string expression, string comparison);

    /// <summary>
    /// Renders a test: "every element of the multi-valued value satisfies the comparison".
    /// </summary>
    string AllElementsMatch(string expression, string comparison);

    /// <summary>Renders a pagination clause.</summary>
    string LimitOffset(int limit, int offset);

    /// <summary>Renders the position of <c>NULL</c> in a sort, to align it across engines.</summary>
    string OrderByNullsLast(string expression, bool descending);

    /// <summary>Opens a connection.</summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Translates a provider exception into a business exception, when it matches a known
    /// constraint violation.
    /// </summary>
    /// <returns>
    /// The corresponding business exception, or <see langword="null"/> if the exception is not
    /// recognized and should propagate unchanged.
    /// </returns>
    CratebaseException? TranslateException(DbException exception);

    /// <summary>
    /// Statements to run when opening each connection (WAL mode, foreign keys…).
    /// </summary>
    IReadOnlyList<string> ConnectionInitializationStatements { get; }

    /// <summary>
    /// Scalar query returning the database size, in bytes.
    /// </summary>
    /// <remarks>
    /// Carried by the dialect because it exists nowhere else: SQLite derives it from its pages,
    /// PostgreSQL has a dedicated function, and both forms belong to the engine. Measuring it from
    /// the server by reading a file would work for SQLite and only for SQLite — the operations
    /// screen would then go silent right at switch-over, exactly when data volume starts to matter.
    /// </remarks>
    string DatabaseSizeQuery { get; }
}
