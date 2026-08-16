using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Data.Postgres;
using Cratebase.Data.Sqlite;
using Cratebase.Expressions;
using Shouldly;

namespace Cratebase.UnitTests;

public class FilterCompilerTests
{
    private static readonly RecordId AuthId = RecordId.Parse("01922f8e-0000-7000-8000-000000000001");

    private static FilterCompiler Compiler(
        ISqlDialect? dialect = null,
        FilterRequestContext? request = null) =>
        new(dialect ?? SqliteDialect.Instance,
            new FakeResolver(),
            request ?? FilterRequestContext.None,
            FixedClock.Default);

    private static SqlPredicate Compile(
        string expression,
        ISqlDialect? dialect = null,
        FilterRequestContext? request = null) =>
        Compiler(dialect, request).Compile(expression, "t");

    // ── Injection boundary ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("unknown = 1")]
    [InlineData("title.sub = 1")]
    [InlineData("password = 'x'")]
    public void A_field_outside_the_schema_never_becomes_sql(string expression)
    {
        var error = Should.Throw<FilterSyntaxException>(() => Compile(expression));

        error.StatusCode.ShouldBe(400);
    }

    [Fact]
    public void Every_value_becomes_a_parameter()
    {
        var predicate = Compile(@"title = 'Robert\'); DROP TABLE posts;--'");

        // The hostile payload is in the parameters, not in the SQL text.
        predicate.Sql.ShouldBe("\"t\".\"title\" = @p0");
        predicate.Parameters["p0"].ShouldBe("Robert'); DROP TABLE posts;--");
    }

    [Fact]
    public void The_sql_escaping_idiom_does_not_exist_in_this_language()
    {
        // Doubling a SQL quote isn't an escape here: "'a''b'" reads as the string "a" followed by
        // stray tokens, and the expression is rejected. The most common injection payload
        // therefore never even makes it past parsing.
        Should.Throw<FilterSyntaxException>(() => Compile("title = 'a''); DROP TABLE posts;--'"));
    }

    [Fact]
    public void Identifiers_are_escaped_and_qualified() =>
        Compile("title = 'x'").Sql.ShouldBe("\"t\".\"title\" = @p0");

    // ── Composition: a rule can only narrow ────────────────────────────────────────────────────

    [Fact]
    public void A_rule_and_a_filter_compose_as_a_conjunction()
    {
        var compiler = Compiler();
        var rule = compiler.Compile("owner = 'u1'", "t", "r");
        var filter = compiler.Compile("title ~ 'news'", "t", "f");

        var combined = rule.And(filter);

        combined.Sql.ShouldBe("(\"t\".\"owner\" = @r0) AND (\"t\".\"title\" LIKE @f0 ESCAPE '\\')");
        combined.Parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void Composing_two_predicates_with_the_same_prefix_is_a_programming_error()
    {
        var compiler = Compiler();
        var a = compiler.Compile("owner = 'u1'", "t");
        var b = compiler.Compile("title = 'x'", "t");

        // The guard: without it, the merge would silently overwrite a parameter and the rule
        // would end up applying with the filter's value.
        Should.Throw<InvalidOperationException>(() => a.And(b));
    }

    [Fact]
    public void An_unconstrained_predicate_disappears_in_composition()
    {
        var compiler = Compiler();
        var rule = compiler.Compile((string?)null, "t", "r");
        var filter = compiler.Compile("title = 'x'", "t", "f");

        rule.IsUnconstrained.ShouldBeTrue();
        rule.And(filter).Sql.ShouldBe("\"t\".\"title\" = @f0");
    }

    [Fact]
    public void The_denial_predicate_admits_no_row() =>
        SqlPredicate.Denied.Sql.ShouldBe("1 = 0");

    // ── Fallback at compile time ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_authentication_idiom_falls_back_based_on_the_caller()
    {
        var anonymous = Compile("@request.auth.id != ''");
        anonymous.Sql.ShouldBe("1 = 0");

        var authenticated = Compile(
            "@request.auth.id != ''",
            request: new FilterRequestContext { Auth = new FakeUser(AuthId) });

        authenticated.Sql.ShouldBe("1 = 1");
    }

    [Fact]
    public void A_field_of_the_auth_record_is_resolved_to_a_parameter()
    {
        var predicate = Compile(
            "owner = @request.auth.id",
            request: new FilterRequestContext { Auth = new FakeUser(AuthId) });

        predicate.Sql.ShouldBe("\"t\".\"owner\" = @p0");
        predicate.Parameters["p0"].ShouldBe(AuthId.ToString());
    }

    [Fact]
    public void A_free_field_of_the_auth_record_is_reachable()
    {
        var predicate = Compile(
            "@request.auth.role = 'editor'",
            request: new FilterRequestContext { Auth = new FakeUser(AuthId) });

        predicate.Sql.ShouldBe("1 = 1");
    }

    [Fact]
    public void The_request_verb_and_context_are_resolved()
    {
        var request = new FilterRequestContext { Method = "POST", Context = "oauth2" };

        Compile("@request.method = 'POST'", request: request).Sql.ShouldBe("1 = 1");
        Compile("@request.context = 'default'", request: request).Sql.ShouldBe("1 = 0");
    }

    [Fact]
    public void The_isset_modifier_reflects_what_was_submitted()
    {
        var request = new FilterRequestContext
        {
            Body = new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "x" },
        };

        Compile("@request.body.title:isset = true", request: request).Sql.ShouldBe("1 = 1");
        Compile("@request.body.views:isset = true", request: request).Sql.ShouldBe("1 = 0");
    }

    [Fact]
    public void The_changed_modifier_compares_against_the_previous_state()
    {
        var request = new FilterRequestContext
        {
            Body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "new",
                ["views"] = 3d,
            },
            Original = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = "old",
                ["views"] = 3d,
            },
        };

        Compile("@request.body.title:changed = true", request: request).Sql.ShouldBe("1 = 1");
        Compile("@request.body.views:changed = true", request: request).Sql.ShouldBe("1 = 0");
    }

    // ── Date macros ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_macro_becomes_a_parameter_not_an_engine_function()
    {
        var predicate = Compile("releasedAt < @now");

        // The point that sets Cratebase apart from PocketBase: no strftime() in the produced SQL.
        predicate.Sql.ShouldBe("\"t\".\"released_at\" < @p0");
        predicate.Parameters["p0"].ShouldBe("2026-08-13T14:05:09.123Z");
    }

    [Theory]
    [InlineData("@todayStart", "2026-08-13T00:00:00.000Z")]
    [InlineData("@todayEnd", "2026-08-13T23:59:59.999Z")]
    [InlineData("@monthStart", "2026-08-01T00:00:00.000Z")]
    [InlineData("@monthEnd", "2026-08-31T23:59:59.999Z")]
    [InlineData("@yearStart", "2026-01-01T00:00:00.000Z")]
    [InlineData("@yearEnd", "2026-12-31T23:59:59.999Z")]
    [InlineData("@yesterday", "2026-08-12T14:05:09.123Z")]
    [InlineData("@tomorrow", "2026-08-14T14:05:09.123Z")]
    public void The_time_bounds_are_exact(string macro, string expected) =>
        Compile($"releasedAt < {macro}").Parameters["p0"].ShouldBe(expected);

    [Fact]
    public void An_unknown_macro_is_rejected() =>
        Should.Throw<FilterSyntaxException>(() => Compile("releasedAt < @never"));

    // ── Per-type normalization ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_boolean_is_normalized_to_0_1_on_sqlite() =>
        Compile("published = true").Parameters["p0"].ShouldBe(1L);

    [Fact]
    public void A_boolean_stays_boolean_on_postgresql() =>
        Compile("published = true", PostgresDialect.Instance).Parameters["p0"].ShouldBe(true);

    [Fact]
    public void A_literal_date_is_brought_back_to_canonical_form() =>
        Compile("releasedAt > '2026-08-13'").Parameters["p0"].ShouldBe("2026-08-13T00:00:00.000Z");

    [Fact]
    public void A_literal_date_becomes_an_instant_on_postgresql() =>
        Compile("releasedAt > '2026-08-13'", PostgresDialect.Instance)
            .Parameters["p0"]
            .ShouldBe(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));

    // ── "Contains" operator ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_operand_is_wrapped_in_wildcards()
    {
        var predicate = Compile("title ~ 'news'");

        predicate.Sql.ShouldBe("\"t\".\"title\" LIKE @p0 ESCAPE '\\'");
        predicate.Parameters["p0"].ShouldBe("%news%");
    }

    [Fact]
    public void Wildcards_written_by_the_user_are_escaped() =>
        Compile("title ~ '100%_x'").Parameters["p0"].ShouldBe(@"%100\%\_x%");

    [Fact]
    public void Postgresql_uses_ilike_to_align_with_sqlite() =>
        Compile("title ~ 'news'", PostgresDialect.Instance)
            .Sql.ShouldBe("\"t\".\"title\" ILIKE @p0 ESCAPE '\\'");

    [Fact]
    public void The_negation_of_contains_is_rendered() =>
        Compile("title !~ 'news'").Sql.ShouldStartWith("NOT (");

    [Fact]
    public void Contains_requires_a_value_on_the_right() =>
        Should.Throw<FilterSyntaxException>(() => Compile("title ~ owner"));

    // ── Multi-valued fields ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void By_default_a_multivalued_field_requires_every_element_to_match() =>
        Compile("tags = 'news'").Sql.ShouldStartWith("NOT EXISTS (SELECT 1 FROM json_each(");

    [Fact]
    public void The_question_mark_prefix_switches_to_at_least_one() =>
        Compile("tags ?= 'news'").Sql.ShouldStartWith("EXISTS (SELECT 1 FROM json_each(");

    [Fact]
    public void Postgresql_unnests_the_json_array_the_same_way() =>
        Compile("tags ?= 'news'", PostgresDialect.Instance)
            .Sql.ShouldStartWith("EXISTS (SELECT 1 FROM jsonb_array_elements_text(");

    [Fact]
    public void The_length_modifier_counts_elements()
    {
        var predicate = Compile("tags:length > 2");

        predicate.Sql.ShouldBe("coalesce(json_array_length(\"t\".\"tags\"), 0) > @p0");
        predicate.Parameters["p0"].ShouldBe(2d);
    }

    // ── Miscellaneous ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Comparison_to_null_becomes_is_null()
    {
        Compile("title = null").Sql.ShouldBe("\"t\".\"title\" IS NULL");
        Compile("title != null").Sql.ShouldBe("\"t\".\"title\" IS NOT NULL");
    }

    [Fact]
    public void A_value_on_the_left_flips_the_operator() =>
        Compile("100 < views").Sql.ShouldBe("\"t\".\"views\" > @p0");

    [Fact]
    public void The_lower_modifier_compares_lowercase()
    {
        var predicate = Compile("title:lower = 'News'");

        predicate.Sql.ShouldBe("lower(\"t\".\"title\") = @p0");
        predicate.Parameters["p0"].ShouldBe("news");
    }

    [Fact]
    public void Logical_connectors_are_parenthesized() =>
        Compile("title = 'a' && (views > 1 || published = true)")
            .Sql.ShouldBe(
                "(\"t\".\"title\" = @p0 AND (\"t\".\"views\" > @p1 OR \"t\".\"published\" = @p2))");

    [Fact]
    public void Two_fields_are_compared_against_each_other() =>
        Compile("title = owner").Sql.ShouldBe("\"t\".\"title\" = \"t\".\"owner\"");

    [Fact]
    public void Collection_joins_are_explicitly_rejected()
    {
        var error = Should.Throw<FilterSyntaxException>(
            () => Compile("@collection.members.user = 'x'"));

        error.Message.ShouldContain("@collection");
    }

    [Fact]
    public void An_unsupported_function_is_rejected_clearly()
    {
        var error = Should.Throw<FilterSyntaxException>(() => Compile("geoDistance(1,2,3,4) < 10"));

        error.Message.ShouldContain("geoDistance");
    }
}
