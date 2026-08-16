using Cratebase.Expressions;
using Shouldly;

namespace Cratebase.UnitTests;

public class FilterParserTests
{
    [Fact]
    public void An_empty_expression_constrains_nothing()
    {
        // A structuring distinction, borrowed from PocketBase: an empty rule allows, an absent
        // (null) rule locks. Confusing the two would open every collection.
        FilterParser.Parse(null).ShouldBeNull();
        FilterParser.Parse("").ShouldBeNull();
        FilterParser.Parse("   ").ShouldBeNull();
    }

    [Fact]
    public void A_simple_comparison_produces_a_comparison_node()
    {
        var node = FilterParser.Parse("title = 'hello'").ShouldBeOfType<ComparisonNode>();

        node.Operator.ShouldBe(ComparisonOperator.Equal);
        node.AnyOf.ShouldBeFalse();
        node.Left.ShouldBeOfType<PathNode>().Segments.ShouldBe(["title"]);
        node.Right.ShouldBeOfType<LiteralNode>().Value.ShouldBe("hello");
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
    public void All_operators_are_recognized(string expression, ComparisonOperator expected) =>
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
    public void The_question_mark_prefix_switches_to_any_of(string expression, ComparisonOperator expected)
    {
        var node = FilterParser.Parse(expression).ShouldBeOfType<ComparisonNode>();

        node.Operator.ShouldBe(expected);
        node.AnyOf.ShouldBeTrue();
    }

    [Fact]
    public void Conjunction_binds_tighter_than_disjunction()
    {
        // "a = 1 || b = 2 && c = 3" must read as "a = 1 || (b = 2 && c = 3)".
        // Getting this wrong silently widens any access rule that mixes the two.
        var root = FilterParser.Parse("a = 1 || b = 2 && c = 3").ShouldBeOfType<LogicalNode>();

        root.Operator.ShouldBe(LogicalOperator.Or);
        root.Left.ShouldBeOfType<ComparisonNode>();
        root.Right.ShouldBeOfType<LogicalNode>().Operator.ShouldBe(LogicalOperator.And);
    }

    [Fact]
    public void Parentheses_take_back_control_of_precedence()
    {
        var root = FilterParser.Parse("(a = 1 || b = 2) && c = 3").ShouldBeOfType<LogicalNode>();

        root.Operator.ShouldBe(LogicalOperator.And);
        root.Left.ShouldBeOfType<LogicalNode>().Operator.ShouldBe(LogicalOperator.Or);
    }

    [Fact]
    public void A_path_traverses_relations()
    {
        var node = FilterParser.Parse("author.profile.city = 'Lyon'").ShouldBeOfType<ComparisonNode>();

        node.Left.ShouldBeOfType<PathNode>().Segments.ShouldBe(["author", "profile", "city"]);
    }

    [Fact]
    public void Request_meta_fields_are_recognized()
    {
        var node = FilterParser.Parse("owner = @request.auth.id").ShouldBeOfType<ComparisonNode>();

        var path = node.Right.ShouldBeOfType<PathNode>();
        path.IsRequestPath.ShouldBeTrue();
        path.Segments.ShouldBe(["@request", "auth", "id"]);
    }

    [Fact]
    public void A_collection_join_is_recognized()
    {
        var node = FilterParser.Parse("@collection.memberships.user = 'x'").ShouldBeOfType<ComparisonNode>();

        node.Left.ShouldBeOfType<PathNode>().IsCollectionPath.ShouldBeTrue();
    }

    [Fact]
    public void A_macro_is_distinguished_from_a_field()
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
    public void Path_modifiers_are_recognized(string expression, PathModifier expected) =>
        FilterParser.Parse(expression)
            .ShouldBeOfType<ComparisonNode>()
            .Left.ShouldBeOfType<PathNode>()
            .Modifier.ShouldBe(expected);

    [Fact]
    public void An_unknown_modifier_is_rejected() =>
        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse("a:sqlinject = 1"));

    [Fact]
    public void Literals_are_typed()
    {
        Value("a = 'text'").ShouldBe("text");
        Value("a = \"text\"").ShouldBe("text");
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
    public void String_escapes_are_resolved() =>
        FilterParser.Parse(@"a = 'the\'apostrophe'")
            .ShouldBeOfType<ComparisonNode>()
            .Right.ShouldBeOfType<LiteralNode>()
            .Value.ShouldBe("the'apostrophe");

    [Fact]
    public void End_of_line_comments_are_ignored()
    {
        var node = FilterParser.Parse("""
            // owner only
            owner = @request.auth.id
            """);

        node.ShouldBeOfType<ComparisonNode>();
    }

    [Fact]
    public void A_function_call_is_parsed()
    {
        var node = FilterParser.Parse("geoDistance(lon, lat, 4.83, 45.76) < 10")
            .ShouldBeOfType<ComparisonNode>();

        var call = node.Left.ShouldBeOfType<FunctionNode>();
        call.Name.ShouldBe("geoDistance");
        call.Arguments.Count.ShouldBe(4);
    }

    // ── What must fail ────────────────────────────────────────────────────────────────────────
    // Each of these cases is an attempt to reach SQL. The parser must refuse, not produce a tree
    // the downstream compiler would interpret.

    [Theory]
    [InlineData("a = 1; DROP TABLE posts")]
    [InlineData("a = 1 -- SQL comment")]
    [InlineData("a = 1 /* block */")]
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
    [InlineData("a = 'unterminated")]
    [InlineData("a..b = 1")]
    public void Hostile_or_malformed_expressions_are_rejected(string expression) =>
        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse(expression));

    [Fact]
    public void An_overly_long_expression_is_rejected()
    {
        var expression = new string('a', FilterParser.MaxLength + 1);

        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse(expression));
    }

    [Fact]
    public void An_overly_nested_expression_is_rejected()
    {
        // Without a bound, this input overflows the stack: a denial of service in a single string.
        var depth = FilterParser.MaxDepth + 5;
        var expression = new string('(', depth) + "a = 1" + new string(')', depth);

        Should.Throw<FilterSyntaxException>(() => FilterParser.Parse(expression));
    }

    [Fact]
    public void The_error_position_is_reported()
    {
        var error = Should.Throw<FilterSyntaxException>(() => FilterParser.Parse("title = 'ok' && "));

        error.Position.ShouldBeGreaterThan(0);
    }
}
