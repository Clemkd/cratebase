using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Contrôle exécutable de la règle R1 : aucun SQL de dialecte hors des paquets
/// <c>Cratebase.Data*</c>.
/// </summary>
/// <remarks>
/// <para>
/// Le document de conception adosse R1 à un <c>grep</c>. Un <c>grep</c> ne tourne pas tout seul et
/// personne ne le lance avant de pousser : la règle ne tient que si elle est vérifiée par un test.
/// C'est celui-ci. Ce qu'il protège n'est pas une convention de style, c'est la promesse du §1 —
/// sortir SQLite pour PostgreSQL sans réécrire le code applicatif. Un seul <c>json_extract</c> dans
/// le moteur suffit à la retirer.
/// </para>
/// <para>
/// <b>Les commentaires sont exclus du balayage.</b> La distinction est essentielle ici : la base
/// documente ses pièges de portabilité en nommant les fonctions interdites — <c>RecordId</c>
/// explique qu'aucun identifiant ne dépend de <c>last_insert_rowid</c>, <c>FilterAst</c> explique
/// pourquoi le langage de filtre n'expose pas <c>strftime()</c>. Échouer sur cette prose
/// pousserait à la supprimer, donc à perdre l'explication de la règle pour satisfaire son contrôle.
/// </para>
/// </remarks>
public class ArchitectureTests
{
    /// <summary>Les paquets où le SQL propre à un moteur a le droit d'exister.</summary>
    private static readonly string[] DialectPackages =
    [
        "Cratebase.Data",
        "Cratebase.Data.Sqlite",
        "Cratebase.Data.Postgres",
    ];

    /// <summary>
    /// Marqueurs de SQL propre à un moteur.
    /// </summary>
    /// <remarks>
    /// <para>
    /// La casse fait partie du motif pour les mots-clés nus, et ce n'est pas une négligence.
    /// <c>Identifier.Reserved</c> conserve <c>returning</c>, <c>analyze</c>, <c>collate</c> ou
    /// <c>similar</c> en minuscules, comme <b>données</b> : c'est la liste blanche du DDL, pas du
    /// SQL. Les chercher sans égard à la casse ferait échouer le test sur le fichier même qui
    /// protège les identifiants, et la seule issue serait de l'exempter — donc d'ouvrir un trou
    /// dans le contrôle là où il compte le plus. Le SQL de cette base écrit les mots-clés en
    /// capitales et les fonctions en minuscules ; les motifs suivent cette forme.
    /// </para>
    /// <para>
    /// Les marqueurs dont la forme ne peut pas entrer en collision — noms à souligné, expressions
    /// de plusieurs mots — restent insensibles à la casse : aucun identifiant C# ne ressemble à
    /// <c>json_extract</c> ni à <c>ON CONFLICT</c>.
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
        Insensitive("unixepoch", @"\bunixepoch\b"),
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
        // « jsonb » et « setval » nus restent sensibles à la casse : « jsonBody » et « setVal »
        // sont des noms C# plausibles, et le transtypage « global::… » n'est pas du SQL.
        Sensitive("jsonb", @"\bjsonb\b"),
        Sensitive("nextval", @"\b(?:nextval|currval|setval)\b"),
        Sensitive(
            "transtypage ::",
            @"::(?:text|json|jsonb|integer|int|int4|int8|bigint|boolean|bool|numeric|real|uuid"
            + @"|timestamptz|timestamp|date|double\s+precision)\b"),
        Sensitive("ILIKE", @"\bILIKE\b"),
        Sensitive("RETURNING", @"\bRETURNING\b"),
        Sensitive("SERIAL", @"\b(?:BIG|SMALL)?SERIAL\b"),
        Sensitive("EXCLUDED.", @"\bEXCLUDED\s*\."),
        Sensitive("LISTEN/NOTIFY", @"\b(?:UNLISTEN|LISTEN|NOTIFY)\b"),

        // ── Écrit des deux côtés, mais pas de la même façon ─────────────────────────────────────
        Insensitive("ON CONFLICT", @"\bon\s+conflict\b"),
    ];

    [Fact]
    public void Aucun_sql_de_dialecte_hors_des_paquets_data()
    {
        var files = SourceFiles();

        // Sans ce garde-fou, une racine mal localisée ferait passer le test sur zéro fichier.
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
    public void Le_balayage_reconnait_les_marqueurs_des_dialectes()
    {
        // Contrôle positif. Le test précédent réussit quand il ne trouve rien : il faut donc
        // prouver séparément que le balayage trouve ce qui existe. Les dialectes, eux, sont
        // censés être remplis de marqueurs — c'est leur raison d'être.
        var detected = SourceFiles()
            .Where(file => IsDialectPackage(file.RelativePath))
            .SelectMany(file => Scan(file.RelativePath, File.ReadAllText(file.FullPath)))
            .Select(violation => violation.Marker)
            .ToHashSet(StringComparer.Ordinal);

        detected.ShouldContain("PRAGMA");
        detected.ShouldContain("json_each");
        detected.ShouldContain("ILIKE");
        detected.ShouldContain("jsonb");
        detected.ShouldContain("transtypage ::");
    }

    [Fact]
    public void Les_sorties_de_compilation_sont_hors_du_balayage()
    {
        IsBuildOutput("Cratebase.Data/obj/Debug/net10.0/Data.GlobalUsings.g.cs").ShouldBeTrue();
        IsBuildOutput("Cratebase.Data/bin/Debug/net10.0/Copie.cs").ShouldBeTrue();
        IsBuildOutput("Cratebase.Data/ISqlDialect.cs").ShouldBeFalse();

        // Segment entier, pas sous-chaîne : « Binder.cs » n'est pas une sortie de compilation.
        IsBuildOutput("Cratebase.Storage/Binder.cs").ShouldBeFalse();

        var segments = SourceFiles()
            .SelectMany(file => file.RelativePath.Split('/'))
            .ToHashSet(StringComparer.Ordinal);

        segments.ShouldNotContain("bin");
        segments.ShouldNotContain("obj");
    }

    [Fact]
    public void Un_marqueur_cite_dans_un_commentaire_nest_pas_du_sql()
    {
        const string source = """
            // strftime() n'existe pas sur PostgreSQL.
            /// <remarks>Aucun <c>last_insert_rowid</c> : c'est la règle R4.</remarks>
            /* json_each est du SQLite pur. */
            var reference = "https://exemple.test/r1" + " ON CONFLICT ";
            """;

        var found = Scan("Cratebase.Exemple/Exemple.cs", source);

        // Seule la chaîne compte. Et le « // » de l'URL n'ouvre pas un commentaire : sinon le
        // marqueur qui la suit sur la même ligne disparaîtrait du balayage.
        found.Select(violation => violation.Marker).ShouldBe(["ON CONFLICT"]);

        // Le retrait des commentaires conserve les longueurs : les lignes signalées sont donc
        // celles du fichier, pas celles d'un texte réduit.
        found[0].Line.ShouldBe(4);
    }

    // ── Balayage ───────────────────────────────────────────────────────────────────────────────

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
    /// Rend le source privé de ses commentaires, <b>à longueur inchangée</b>.
    /// </summary>
    /// <remarks>
    /// Les caractères de commentaire deviennent des espaces, les fins de ligne sont conservées : un
    /// décalage dans le texte rendu désigne donc la même ligne que dans le fichier d'origine. Les
    /// littéraux sont recopiés tels quels — verbatim et bruts compris, ces derniers portant tout le
    /// SQL de cette base — sans quoi le « // » d'une URL ferait disparaître la fin de sa ligne.
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

    /// <summary>Recopie un littéral brut, clôture comprise, et rend la position d'après.</summary>
    /// <remarks>
    /// La barrière peut compter plus de trois guillemets, et le contenu peut en contenir moins :
    /// c'est ce qui permet à <c>$"""… {dialect.QuoteIdentifier("id")} …"""</c> de rester un seul
    /// littéral, donc au SQL qu'il porte d'être vu comme du SQL.
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

    /// <summary>Recopie un littéral verbatim, où le guillemet se double pour s'échapper.</summary>
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

    /// <summary>Recopie un littéral de chaîne ou de caractère à échappement par barre oblique.</summary>
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
    /// Localise la racine du dépôt en remontant depuis le répertoire d'exécution.
    /// </summary>
    /// <remarks>
    /// Aucun chemin en dur : le test doit rester valable depuis un clone, un conteneur de build ou
    /// un arbre de travail détaché, où la racine ne porte pas le même nom.
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
            $"Racine du dépôt introuvable : aucun « Cratebase.slnx » au-dessus de « {AppContext.BaseDirectory} ».");
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

    // ── Rapport ────────────────────────────────────────────────────────────────────────────────

    private static string Report(List<Violation> violations)
    {
        var builder = new StringBuilder()
            .AppendLine("Règle R1 — du SQL propre à un moteur apparaît hors de Cratebase.Data* :")
            .AppendLine();

        foreach (var violation in violations
            .OrderBy(violation => violation.RelativePath, StringComparer.Ordinal)
            .ThenBy(violation => violation.Line))
        {
            builder
                .AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  src/{violation.RelativePath}:{violation.Line} — marqueur « {violation.Marker} »")
                .AppendLine(CultureInfo.InvariantCulture, $"      {violation.Text}")
                .AppendLine();
        }

        return builder
            .AppendLine("Le moteur de requêtes ne produit pas de chaîne SQL : il produit un arbre, que le")
            .AppendLine("dialecte compile. Ce qui manque va donc dans ISqlDialect, rendu par chaque dialecte —")
            .AppendLine("la frontière ne se déplace pas.")
            .ToString();
    }

    // ── Types internes ─────────────────────────────────────────────────────────────────────────

    private sealed record SourceFile(string RelativePath, string FullPath);

    private sealed record Violation(string RelativePath, int Line, string Marker, string Text);

    private sealed record Marker(string Label, Regex Pattern);

    private static Marker Sensitive(string label, string pattern) =>
        new(label, new Regex(pattern, RegexOptions.CultureInvariant));

    private static Marker Insensitive(string label, string pattern) =>
        new(label, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
}
