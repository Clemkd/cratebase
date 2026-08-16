using Cratebase.Core;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// These cases are the source of truth shared with the TypeScript twin in
/// <c>@cratebase/client</c>. A divergence between the two means a button shows up when the API
/// will refuse, or the reverse.
/// </summary>
public class PermissionMatcherTests
{
    [Theory]
    // Strict equality
    [InlineData("posts.write", "posts.write", true)]
    [InlineData("posts.write", "posts.read", false)]
    [InlineData("posts.write", "Posts.Write", false)]

    // Single-level wildcard
    [InlineData("posts.*", "posts.write", true)]
    [InlineData("posts.*", "posts.read", true)]
    [InlineData("posts.*", "posts.comments.moderate", false)]
    [InlineData("posts.*", "posts", false)]
    [InlineData("posts.*", "postsx.write", false)]
    [InlineData("*", "posts", false)]

    // The wildcard is only recognized in terminal position
    [InlineData("*.write", "posts.write", false)]
    [InlineData("posts.*.write", "posts.a.write", false)]

    // Degenerate cases
    [InlineData("", "posts.write", false)]
    [InlineData(null, "posts.write", false)]
    public void The_wildcard_covers_only_one_level(string? held, string required, bool expected) =>
        PermissionMatcher.Covers(held, required).ShouldBe(expected);

    [Fact]
    public void A_set_is_satisfied_as_soon_as_one_permission_covers()
    {
        string[] held = ["users.read", "posts.*"];

        PermissionMatcher.IsSatisfied(held, "posts.write").ShouldBeTrue();
        PermissionMatcher.IsSatisfied(held, "users.read").ShouldBeTrue();
        PermissionMatcher.IsSatisfied(held, "users.write").ShouldBeFalse();
        PermissionMatcher.IsSatisfied([], "posts.write").ShouldBeFalse();
    }

    [Fact]
    public void An_empty_required_permission_is_a_programming_error() =>
        Should.Throw<ArgumentException>(() => PermissionMatcher.Covers("posts.*", ""));
}
