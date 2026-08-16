using System.Diagnostics;
using Cratebase.Auth;
using Shouldly;

namespace Cratebase.UnitTests;

public class PasswordHasherTests
{
    [Fact]
    public void A_password_verifies_against_its_hash()
    {
        var hash = PasswordHasher.Hash("solid-password");

        PasswordHasher.Verify("solid-password", hash).ShouldBeTrue();
        PasswordHasher.Verify("solid-passworD", hash).ShouldBeFalse();
        PasswordHasher.Verify("", hash).ShouldBeFalse();
        PasswordHasher.Verify(null, hash).ShouldBeFalse();
    }

    [Fact]
    public void Two_hashes_of_the_same_password_differ()
    {
        // Random salt: without it, two accounts with the same password would be recognizable by
        // simply inspecting the table.
        var first = PasswordHasher.Hash("solid-password");
        var second = PasswordHasher.Hash("solid-password");

        first.ShouldNotBe(second);
        PasswordHasher.Verify("solid-password", first).ShouldBeTrue();
        PasswordHasher.Verify("solid-password", second).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !")]
    [InlineData("YWJj")]
    public void An_unreadable_hash_never_verifies(string? hash) =>
        PasswordHasher.Verify("solid-password", hash).ShouldBeFalse();

    [Fact]
    public void The_cost_is_stored_with_the_hash()
    {
        // That's what allows raising the cost tomorrow without making yesterday's passwords
        // unverifiable.
        PasswordHasher.NeedsRehash(PasswordHasher.Hash("solid-password")).ShouldBeFalse();
        PasswordHasher.NeedsRehash(null).ShouldBeTrue();
        PasswordHasher.NeedsRehash("YWJj").ShouldBeTrue();
    }

    [Fact]
    public void The_derivation_cost_is_perceptible()
    {
        // An instant hash is a hash that's too weak. We don't measure a target value — that would
        // be unstable in continuous integration — only that a cost exists.
        var stopwatch = Stopwatch.StartNew();
        PasswordHasher.Hash("solid-password");
        stopwatch.Stop();

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThan(5);
    }
}
