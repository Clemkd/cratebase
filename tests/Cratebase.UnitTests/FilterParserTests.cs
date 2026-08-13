using Cratebase.Expressions;
using Shouldly;

namespace Cratebase.UnitTests;

public class FilterParserTests
{
    [Fact]
    public void Une_expression_vide_ne_contraint_rien()
    {
        // Distinction structurante, reprise de PocketBase : une règle vide autorise, une règle
        // absente (null) verrouille. Les confondre ouvrirait toutes les collections.
        FilterParser.Parse(null).ShouldBeNull();
        FilterParser.Parse("").ShouldBeNull();
        FilterParser.Parse("   ").ShouldBeNull();
    }

    [Fact]
    public void Une_comparaison_simple_produit_un_noeud_de_comparaison()
    {
        var node = FilterParser.Parse("title = 'bonjour'").ShouldBeOfType<ComparisonNode>();

        node.Operator.ShouldBe(ComparisonOperator.Equal);
        node.AnyOf.ShouldBeFalse();
        node.Left.ShouldBeOfType<PathNode>().Segments.ShouldBe(["title"]);
        node.Right.ShouldBeOfType<LiteralNode>().Value.ShouldBe("bonjour");
    }

    [Theory]
    [InlineData("a = 1", ComparisonOperator.Equal)]
    [InlineData("a != 1", ComparisonOperator.NotEqual)]
    [InlineData("a > 1", ComparisonOperator.GreaterThan)]
    [InlineData("a >= 1", ComparisonOperator.GreaterThanOrEqual)]
    [InlineData("a < 1", ComparisonOperator.LessThan)]
    [InlineData("a <= 1", ComparisonOperator.LessThanOrEqual)]
    [InlineData("a ~ 1", ComparisonOperator.Like)]
    [InlineData("a !~ 1", ComparisonOperator.NotLike)]
    public void Tous_les_operateurs_sont_reconnus(string expression, ComparisonOperator expected) =>
        FilterParser.Parse(expression)
            .ShouldBeOfType<ComparisonNode>()
            .Operator.ShouldBe(expected);

    [Theory]
    [InlineData("a ?= 1", ComparisonOperator.Equal)]
    [InlineData("a ?!= 1", ComparisonOperator.NotEqual)]
    [InlineData("a ?> 1", ComparisonOperator.GreaterThan)]
    [InlineData("a ?>= 1", ComparisonOperator.GreaterThanOrEqual)]
    [InlineData("a ?< 1", ComparisonOperator.LessThan)]
    [InlineData("a ?<= 1", ComparisonOperator.LessThanOrEqual)]
    [InlineData("a ?~ 1", ComparisonOperator.Like)]
    [InlineData("a ?!~ 1", ComparisonOperator.NotLike)]
    public void Le_prefixe_interrogatif_bascule_en_au_moins_un(string expression, ComparisonOperator expected)
    {
        var node = FilterParser.Parse(expression).ShouldBeOfType<ComparisonNode>();

        node.Operator.ShouldBe(expected);
        node.AnyOf.ShouldBeTrue();
    }

    [Fact]
    public void La_conjonction_lie_plus_fort_que_la_disjonction()
    {
        // « a = 1 || b = 2 && c = 3 » doit se lire « a = 1 || (b = 2 && c = 3) ».
        // Se tromper ici élargit silencieusement toute règle d'accès qui mélange les deux.
        var root = FilterParser.Parse("a = 1 || b = 2 && c = 3").ShouldBeOfType<LogicalNode>();

        root.Operator.ShouldBe(LogicalOperator.Or);
        root.Left.ShouldBeOfType<ComparisonNode>();
        root.Right.ShouldBeOfType<LogicalNode>().Operator.ShouldBe(LogicalOperator.And);
    }

    [Fact]
    public void Les_parentheses_reprennent_la_main_sur_la_precedence()
    {
        var root = FilterParser.Parse("(a = 1 || b = 2) && c = 3").ShouldBeOfType<LogicalNode>();

        root.Operator.ShouldBe(LogicalOperator.And);
        root.Left.ShouldBeOfType<LogicalNode>().Operator.ShouldBe(LogicalOperator.Or);
    }

    [Fact]
    public void Un_chemin_traverse_les_relations()
    {
        var node = FilterParser.Parse("author.profile.city = 'Lyon'").ShouldBeOfType<ComparisonNode>();

        node.Left.ShouldBeOfType<PathNode>().Segments.ShouldBe(["author", "profile", "city"]);
    }

    [Fact]
    public void Les_meta_champs_de_requete_sont_reconnus()
    {
        var node = FilterParser.Parse("owner = @request.auth.id").ShouldBeOfType<ComparisonNode>();

        var path = node.Right.ShouldBeOfType<PathNode>();
        path.IsRequestPath.ShouldBeTrue();
        path.Segments.ShouldBe(["@request", "auth", "id"]);
    }

    [Fact]
    public void Une_jointure_de_collection_est_reconnue()
    {
        var node = FilterParser.Parse("@collection.memberships.user = 'x'").ShouldBeOfType<ComparisonNode>();

        node.Left.ShouldBeOfType<PathNode>().IsCollectionPath.ShouldBeTrue();
    }

    [Fact]
    public void Une_macro_est_distinguee_dun_champ()
    {
        var node = FilterParser.Parse("published < @now").ShouldBeOfType<ComparisonNode>();

        var macro = node.Right.ShouldBeOfType<PathNode>();
        macro.IsMacro.ShouldBeTrue();
        macro.IsRequestPath.ShouldBeFalse();

        node.Left.ShouldBeOfType<PathNode>().IsMacro.ShouldBeFalse();
    }

    [Theory]
    [InlineData("a:isset = true", PathModifier.IsSet)]
    [InlineData("a:length > 2", PathModifier.Length)]
    [InlineData("a:each = 'x'", PathModifier.Each)]
    [InlineData("a:lower = 'x'", PathModifier.Lower)]
    [InlineData("a:changed = true", PathModifier.Changed)]
    public void Les_modificateurs_de_chemin_sont_reconnus(string expression, PathModifier expected) =>
        FilterParser.Parse(expression)
            .ShouldBeOfType<ComparisonNode>()
            .Left.ShouldBeOfType<PathNode>()
            .Modifier.ShouldBe(expected);

    [Fact]
    public void Un_modificateur_inconnu_est_refuse() =>
        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse("a:sqlinject = 1"));

    [Fact]
    public void Les_litteraux_sont_types()
    {
        Value("a = 'texte'").ShouldBe("texte");
        Value("a = \"texte\"").ShouldBe("texte");
        Value("a = 42").ShouldBe(42d);
        Value("a = -3.5").ShouldBe(-3.5d);
        Value("a = true").ShouldBe(true);
        Value("a = false").ShouldBe(false);
        Value("a = null").ShouldBeNull();

        static object? Value(string expression) => FilterParser.Parse(expression)
            .ShouldBeOfType<ComparisonNode>()
            .Right.ShouldBeOfType<LiteralNode>()
            .Value;
    }

    [Fact]
    public void Les_echappements_de_chaine_sont_resolus() =>
        FilterParser.Parse(@"a = 'l\'apostrophe'")
            .ShouldBeOfType<ComparisonNode>()
            .Right.ShouldBeOfType<LiteralNode>()
            .Value.ShouldBe("l'apostrophe");

    [Fact]
    public void Les_commentaires_de_fin_de_ligne_sont_ignores()
    {
        var node = FilterParser.Parse("""
            // seul le propriétaire
            owner = @request.auth.id
            """);

        node.ShouldBeOfType<ComparisonNode>();
    }

    [Fact]
    public void Un_appel_de_fonction_est_analyse()
    {
        var node = FilterParser.Parse("geoDistance(lon, lat, 4.83, 45.76) < 10")
            .ShouldBeOfType<ComparisonNode>();

        var call = node.Left.ShouldBeOfType<FunctionNode>();
        call.Name.ShouldBe("geoDistance");
        call.Arguments.Count.ShouldBe(4);
    }

    // ── Ce qui doit échouer ────────────────────────────────────────────────────────────────────
    // Chacun de ces cas est une tentative d'atteindre le SQL. Le parseur doit refuser, et non
    // produire un arbre que le compilateur aval interpréterait.

    [Theory]
    [InlineData("a = 1; DROP TABLE posts")]
    [InlineData("a = 1 -- commentaire SQL")]
    [InlineData("a = 1 /* bloc */")]
    [InlineData("a = 1 UNION SELECT 1")]
    [InlineData("a = 'x' OR 1=1")]
    [InlineData("a")]
    [InlineData("= 1")]
    [InlineData("a =")]
    [InlineData("(a = 1")]
    [InlineData("a = 1)")]
    [InlineData("a & b")]
    [InlineData("a | b")]
    [InlineData("a ? 1")]
    [InlineData("a = 'non terminée")]
    [InlineData("a..b = 1")]
    public void Les_expressions_hostiles_ou_malformees_sont_refusees(string expression) =>
        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse(expression));

    [Fact]
    public void Une_expression_trop_longue_est_refusee()
    {
        var expression = new string('a', FilterParser.MaxLength + 1);

        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse(expression));
    }

    [Fact]
    public void Une_expression_trop_imbriquee_est_refusee()
    {
        // Sans borne, cette entrée fait déborder la pile : un déni de service en une chaîne.
        var depth = FilterParser.MaxDepth + 5;
        var expression = new string('(', depth) + "a = 1" + new string(')', depth);

        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse(expression));
    }

    [Fact]
    public void La_position_de_lerreur_est_rapportee()
    {
        var error = Should.Throw<FilterSyntaxException>(() => FilterParser.Parse("title = 'ok' && "));

        error.Position.ShouldBeGreaterThan(0);
    }
}
