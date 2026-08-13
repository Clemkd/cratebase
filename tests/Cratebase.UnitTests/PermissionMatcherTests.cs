using Cratebase.Core;
using Shouldly;

namespace Cratebase.UnitTests;

/// <summary>
/// Ces cas sont la source de vérité partagée avec le jumeau TypeScript de <c>@cratebase/client</c>.
/// Une divergence entre les deux fait qu'un bouton s'affiche alors que l'API refusera, ou l'inverse.
/// </summary>
public class PermissionMatcherTests
{
    [Theory]
    // Égalité stricte
    [InlineData("posts.write", "posts.write", true)]
    [InlineData("posts.write", "posts.read", false)]
    [InlineData("posts.write", "Posts.Write", false)]

    // Joker d'un seul niveau
    [InlineData("posts.*", "posts.write", true)]
    [InlineData("posts.*", "posts.read", true)]
    [InlineData("posts.*", "posts.comments.moderate", false)]
    [InlineData("posts.*", "posts", false)]
    [InlineData("posts.*", "postsx.write", false)]
    [InlineData("*", "posts", false)]

    // Le joker n'est reconnu qu'en position terminale
    [InlineData("*.write", "posts.write", false)]
    [InlineData("posts.*.write", "posts.a.write", false)]

    // Cas dégénérés
    [InlineData("", "posts.write", false)]
    [InlineData(null, "posts.write", false)]
    public void Le_joker_ne_couvre_quun_seul_niveau(string? held, string required, bool expected) =>
        PermissionMatcher.Covers(held, required).ShouldBe(expected);

    [Fact]
    public void Un_ensemble_est_satisfait_des_quune_permission_couvre()
    {
        string[] held = ["users.read", "posts.*"];

        PermissionMatcher.IsSatisfied(held, "posts.write").ShouldBeTrue();
        PermissionMatcher.IsSatisfied(held, "users.read").ShouldBeTrue();
        PermissionMatcher.IsSatisfied(held, "users.write").ShouldBeFalse();
        PermissionMatcher.IsSatisfied([], "posts.write").ShouldBeFalse();
    }

    [Fact]
    public void Une_permission_exigee_vide_est_une_erreur_de_programmation() =>
        Should.Throw<ArgumentException>(() => PermissionMatcher.Covers("posts.*", ""));
}
