using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Executable check for rule R1: no dialect-specific SQL outside the <c>Cratebase.Data*</c>
/// packages.
/// </summary>
/// <remarks>
/// <para>
/// The design document backs R1 with a <c>grep</c>. A <c>grep</c> doesn't run itself and nobody
/// runs it before pushing: the rule only holds if it's checked by a test. This is that test. What
/// it protects isn't a style convention, it's the promise of §1 — swapping SQLite for PostgreSQL
/// without rewriting application code. A single <c>json_extract</c> in the engine is enough to
/// take that promise away.
/// </para>
/// <para>
/// <b>Comments are excluded from the scan.</b> The distinction matters here: the codebase
/// documents its portability pitfalls by naming the forbidden functions — <c>RecordId</c>
/// explains that no identifier depends on <c>last_insert_rowid</c>, <c>FilterAst</c> explains why
/// the filter language doesn't expose <c>strftime()</c>. Failing on that prose would push people
/// to delete it, and so lose the explanation of the rule in order to satisfy its own check.
/// </para>
/// </remarks>
public class ArchitectureTests
{
    /// <summary>The packages where engine-specific SQL is allowed to exist.</summary>
    private static readonly string[] DialectPackages =
    [
        "Cratebase.Data",
        "Cratebase.Data.Sqlite",
        "Cratebase.Data.Postgres",
    ];

    /// <summary>
    /// Markers for engine-specific SQL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Case is part of the pattern for bare keywords, and that's not an oversight.
    /// <c>Identifier.Reserved</c> keeps <c>returning</c>, <c>analyze</c>, <c>collate</c> or
    /// <c>similar</c> lowercase, like <b>data</b>: that's the DDL allow-list, not SQL. Matching
    /// them case-insensitively would fail the test on the very file that protects identifiers, and
    /// the only way out would be to exempt it — opening a hole in the check exactly where it
    /// matters most. The SQL in this codebase writes keywords in uppercase and functions in
    /// lowercase; the patterns follow that convention.
    /// </para>
    /// <para>
    /// Markers whose shape can't collide — underscored names, multi-word expressions — stay
    /// case-insensitive: no C# identifier looks like <c>json_extract</c> or <c>ON CONFLICT</c>.
    /// </para>
    /// </remarks>
    private static readonly Marker[] Markers =
    [
        // ── SQLite ─────────────────────────────────────────────────────────────────────────────
        Insensitive("json_extract", @"\bjson_extract\b"),
        Insensitive("json_each", @"\bjson_each\b"),
        Insensitive("json_array_length", @"\bjson_array_length\b"),
        Insensitive("json_*", @"\bjson_(?:set|insert|remove|patch|type|valid|quote)\b"),
        Insensitive("strftime", @"\bstrftime\b"),
        Insensitive("julianday", @"\bjulianday\b"),
        // Bare "unixepoch" stays case-sensitive, for the same reason as "jsonb" further down:
        // `DateTimeOffset.UnixEpoch` is a standard library property, and the canonical timestamp
        // relies on it. The SQLite function, meanwhile, is written lowercase like the rest of this
        // codebase's SQL.
        Sensitive("unixepoch", @"\bunixepoch\b"),
        Insensitive("last_insert_rowid", @"\blast_insert_rowid\b"),
        Insensitive("sqlite_*", @"\bsqlite_\w+"),
        Insensitive("WITHOUT ROWID", @"\bwithout\s+rowid\b"),
        Insensitive("COLLATE NOCASE", @"\bcollate\s+nocase\b"),
        Insensitive("EXPLAIN QUERY PLAN", @"\bexplain\s+query\s+plan\b"),
        Sensitive("PRAGMA", @"\bPRAGMA\b"),
        Sensitive("AUTOINCREMENT", @"\bAUTOINCREMENT\b"),
        Sensitive("VACUUM", @"\bVACUUM\b"),
        Sensitive("GLOB", @"\bGLOB\b"),
        Sensitive("ATTACH", @"\b(?:ATTACH|DETACH)\b"),
        Sensitive("REINDEX", @"\bREINDEX\b"),
        Sensitive("ANALYZE", @"\bANALYZE\b"),

        // ── PostgreSQL ─────────────────────────────────────────────────────────────────────────
        Insensitive("jsonb_*", @"\bjsonb_\w+"),
        Insensitive("date_trunc", @"\bdate_trunc\b"),
        Insensitive("to_timestamp", @"\bto_timestamp\b"),
        Insensitive("generate_series", @"\bgenerate_series\b"),
        Insensitive("regexp_*", @"\bregexp_\w+"),
        Insensitive("pg_*", @"\bpg_\w+"),
        Insensitive("citext", @"\bcitext\b"),
        Insensitive("DISTINCT ON", @"\bdistinct\s+on\b"),
        Insensitive("SIMILAR TO", @"\bsimilar\s+to\b"),
        // Bare "jsonb" and "setval" stay case-sensitive: "jsonBody" and "setVal" are plausible C#
        // names, and the "global::…" cast isn't SQL.
        Sensitive("jsonb", @"\bjsonb\b"),
        Sensitive("nextval", @"\b(?:nextval|currval|setval)\b"),
        Sensitive(
            "cast ::",
            @"::(?:text|json|jsonb|integer|int|int4|int8|bigint|boolean|bool|numeric|real|uuid"
            + @"|timestamptz|timestamp|date|double\s+precision)\b"),
        Sensitive("ILIKE", @"\bILIKE\b"),
        Sensitive("RETURNING", @"\bRETURNING\b"),
        Sensitive("SERIAL", @"\b(?:BIG|SMALL)?SERIAL\b"),
        Sensitive("EXCLUDED.", @"\bEXCLUDED\s*\."),
        Sensitive("LISTEN/NOTIFY", @"\b(?:UNLISTEN|LISTEN|NOTIFY)\b"),

        // ── Written on both sides, but not the same way ─────────────────────────────────────────
        Insensitive("ON CONFLICT", @"\bon\s+conflict\b"),
    ];

    [Fact]
    public void No_dialect_sql_exists_outside_the_data_packages()
    {
        var files = SourceFiles();

        // Without this guard, a mislocated root would make the test pass on zero files.
        files.ShouldNotBeEmpty();

        var violations = files
            .Where(file => !IsDialectPackage(file.RelativePath))
            .SelectMany(file => Scan(file.RelativePath, File.ReadAllText(file.FullPath)))
            .ToList();

        if (violations.Count > 0)
        {
            Assert.Fail(Report(violations));
        }
    }

    [Fact]
    public void The_scan_recognizes_dialect_markers()
    {
        // Positive control. The previous test passes when it finds nothing: so it must be proven
        // separately that the scan finds what does exist. The dialects, by design, are supposed
        // to be full of markers — that's their reason for being.
        var detected = SourceFiles()
            .Where(file => IsDialectPackage(file.RelativePath))
            .SelectMany(file => Scan(file.RelativePath, File.ReadAllText(file.FullPath)))
            .Select(violation => violation.Marker)
            .ToHashSet(StringComparer.Ordinal);

        detected.ShouldContain("PRAGMA");
        detected.ShouldContain("json_each");
        detected.ShouldContain("ILIKE");
        detected.ShouldContain("jsonb");
        detected.ShouldContain("cast ::");
    }

    [Fact]
    public void Build_outputs_are_excluded_from_the_scan()
    {
        IsBuildOutput("Cratebase.Data/obj/Debug/net10.0/Data.GlobalUsings.g.cs").ShouldBeTrue();
        IsBuildOutput("Cratebase.Data/bin/Debug/net10.0/Copy.cs").ShouldBeTrue();
        IsBuildOutput("Cratebase.Data/ISqlDialect.cs").ShouldBeFalse();

        // Whole segment, not substring: "Binder.cs" is not a build output.
        IsBuildOutput("Cratebase.Storage/Binder.cs").ShouldBeFalse();

        var segments = SourceFiles()
            .SelectMany(file => file.RelativePath.Split('/'))
            .ToHashSet(StringComparer.Ordinal);

        segments.ShouldNotContain("bin");
        segments.ShouldNotContain("obj");
    }

    [Fact]
    public void A_marker_quoted_in_a_comment_is_not_sql()
    {
        const string source = """
            // strftime() doesn't exist on PostgreSQL.
            /// <remarks>No <c>last_insert_rowid</c>: that's rule R4.</remarks>
            /* json_each is pure SQLite. */
            var reference = "https://example.test/r1" + " ON CONFLICT ";
            """;

        var found = Scan("Cratebase.Example/Example.cs", source);

        // Only the string counts. And the URL's "//" doesn't open a comment: otherwise the marker
        // that follows it on the same line would vanish from the scan.
        found.Select(violation => violation.Marker).ShouldBe(["ON CONFLICT"]);

        // Stripping comments preserves length: the reported lines are therefore those of the
        // original file, not of a shrunk text.
        found[0].Line.ShouldBe(4);
    }

    // ── Scanning ───────────────────────────────────────────────────────────────────────────────

    private static List<Violation> Scan(string relativePath, string source)
    {
        var code = WithoutComments(source);
        var lines = source.Split('\n');
        var violations = new List<Violation>();

        foreach (var marker in Markers)
        {
            foreach (Match match in marker.Pattern.Matches(code))
            {
                var line = LineNumberAt(code, match.Index);
                var text = line <= lines.Length ? lines[line - 1].Trim() : string.Empty;

                violations.Add(new Violation(relativePath, line, marker.Label, Shorten(text)));
            }
        }

        violations.Sort(static (left, right) => left.Line.CompareTo(right.Line));

        return violations;
    }

    /// <summary>
    /// Returns the source stripped of its comments, <b>at unchanged length</b>.
    /// </summary>
    /// <remarks>
    /// Comment characters become spaces, line endings are preserved: an offset in the rendered
    /// text therefore points at the same line as in the original file. Literals are copied
    /// verbatim — raw and verbatim strings included, since the latter carry all the SQL in this
    /// codebase — otherwise a URL's "//" would erase the rest of its line.
    /// </remarks>
    private static string WithoutComments(string source)
    {
        var code = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                for (; index < source.Length && source[index] != '\n'; index++)
                {
                    code.Append(' ');
                }
            }
            else if (current == '/' && next == '*')
            {
                var close = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                var stop = close < 0 ? source.Length : close + 2;

                for (; index < stop; index++)
                {
                    code.Append(source[index] == '\n' ? '\n' : ' ');
                }
            }
            else if (current == '"' && next == '"' && index + 2 < source.Length && source[index + 2] == '"')
            {
                index = CopyRawString(source, index, code);
            }
            else if (current == '@' && next == '"')
            {
                index = CopyVerbatimString(source, index, code);
            }
            else if (current is '"' or '\'')
            {
                index = CopyQuoted(source, index, current, code);
            }
            else
            {
                code.Append(current);
                index++;
            }
        }

        return code.ToString();
    }

    /// <summary>Copies a raw string literal, closing fence included, and returns the position after it.</summary>
    /// <remarks>
    /// The fence can be more than three quotes, and the content can contain fewer: that's what
    /// lets <c>$"""… {dialect.QuoteIdentifier("id")} …"""</c> stay a single literal, and so lets
    /// the SQL it carries be seen as SQL.
    /// </remarks>
    private static int CopyRawString(string source, int start, StringBuilder code)
    {
        var fence = 0;
        while (start + fence < source.Length && source[start + fence] == '"')
        {
            fence++;
        }

        code.Append(source, start, fence);
        var index = start + fence;

        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                code.Append(source[index]);
                index++;
                continue;
            }

            var run = 0;
            while (index + run < source.Length && source[index + run] == '"')
            {
                run++;
            }

            code.Append(source, index, run);
            index += run;

            if (run >= fence)
            {
                break;
            }
        }

        return index;
    }

    /// <summary>Copies a verbatim string literal, where the quote is doubled to escape itself.</summary>
    private static int CopyVerbatimString(string source, int start, StringBuilder code)
    {
        code.Append(source, start, 2);
        var index = start + 2;

        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                code.Append(source[index]);
                index++;
                continue;
            }

            if (index + 1 < source.Length && source[index + 1] == '"')
            {
                code.Append(source, index, 2);
                index += 2;
                continue;
            }

            code.Append('"');
            index++;
            break;
        }

        return index;
    }

    /// <summary>Copies a string or character literal that uses backslash escaping.</summary>
    private static int CopyQuoted(string source, int start, char delimiter, StringBuilder code)
    {
        code.Append(delimiter);
        var index = start + 1;

        while (index < source.Length && source[index] != delimiter && source[index] != '\n')
        {
            if (source[index] == '\\' && index + 1 < source.Length)
            {
                code.Append(source, index, 2);
                index += 2;
                continue;
            }

            code.Append(source[index]);
            index++;
        }

        if (index < source.Length && source[index] == delimiter)
        {
            code.Append(delimiter);
            index++;
        }

        return index;
    }

    private static int LineNumberAt(string text, int index)
    {
        var line = 1;

        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string Shorten(string text) =>
        text.Length <= 120 ? text : string.Concat(text.AsSpan(0, 119), "…");

    // ── Sources ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Locates the repository root by walking up from the execution directory.
    /// </summary>
    /// <remarks>
    /// No hardcoded path: the test must stay valid from a clone, a build container, or a detached
    /// worktree, where the root doesn't carry the same name.
    /// </remarks>
    private static string RepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "Cratebase.slnx")))
            {
                return candidate.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root not found: no \"Cratebase.slnx\" above \"{AppContext.BaseDirectory}\".");
    }

    private static List<SourceFile> SourceFiles()
    {
        var sources = Path.Combine(RepositoryRoot(), "src");
        var files = new List<SourceFile>();

        foreach (var path in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sources, path).Replace('\\', '/');

            if (!IsBuildOutput(relative))
            {
                files.Add(new SourceFile(relative, path));
            }
        }

        files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        return files;
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split('/').Any(static segment => segment is "bin" or "obj");

    private static bool IsDialectPackage(string relativePath)
    {
        var separator = relativePath.IndexOf('/');
        var package = separator < 0 ? relativePath : relativePath[..separator];

        return DialectPackages.Contains(package, StringComparer.Ordinal);
    }

    // ── Report ─────────────────────────────────────────────────────────────────────────────────

    private static string Report(List<Violation> violations)
    {
        var builder = new StringBuilder()
            .AppendLine("Rule R1 — engine-specific SQL appears outside Cratebase.Data*:")
            .AppendLine();

        foreach (var violation in violations
            .OrderBy(violation => violation.RelativePath, StringComparer.Ordinal)
            .ThenBy(violation => violation.Line))
        {
            builder
                .AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  src/{violation.RelativePath}:{violation.Line} — marker \"{violation.Marker}\"")
                .AppendLine(CultureInfo.InvariantCulture, $"      {violation.Text}")
                .AppendLine();
        }

        return builder
            .AppendLine("The query engine doesn't produce a SQL string: it produces a tree, which the")
            .AppendLine("dialect compiles. Whatever's missing belongs in ISqlDialect, implemented by each")
            .AppendLine("dialect — the boundary doesn't move.")
            .ToString();
    }

    // ── Internal types ─────────────────────────────────────────────────────────────────────────

    private sealed record SourceFile(string RelativePath, string FullPath);

    private sealed record Violation(string RelativePath, int Line, string Marker, string Text);

    private sealed record Marker(string Label, Regex Pattern);

    private static Marker Sensitive(string label, string pattern) =>
        new(label, new Regex(pattern, RegexOptions.CultureInvariant));

    private static Marker Insensitive(string label, string pattern) =>
        new(label, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
}
