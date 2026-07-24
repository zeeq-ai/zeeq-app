namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Unit tests for user-entered alias normalization.
/// </summary>
public sealed class UserAliasNormalizerTests
{
    [Test]
    public async Task ToWrites_WithMoreThanThreeEmailAliases_ThrowsArgumentException()
    {
        void Act() =>
            UserAliasNormalizer.ToWrites(
                ["one@example.com", "two@example.com", "three@example.com", "four@example.com"],
                null
            );

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task ToWrites_WithMoreThanThreeGitHubAliases_ThrowsArgumentException()
    {
        void Act() => UserAliasNormalizer.ToWrites(null, ["one", "two", "three", "four"]);

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task ToWrites_WithOversizedEmailAlias_ThrowsArgumentException()
    {
        var domain = "@example.com";
        var localPart = new string('a', UserAliasNormalizer.MaxAliasLength - domain.Length + 1);

        void Act() => UserAliasNormalizer.ToWrites([localPart + domain], null);

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task ToWrites_WithNullAlias_ThrowsArgumentException()
    {
        void Act() => UserAliasNormalizer.ToWrites([null], [null]);

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task ToWrites_WithGitHubAlias_PreservesDisplayAndStripsAtForNormalizedValue()
    {
        var aliases = UserAliasNormalizer.ToWrites(null, [" @CharlieDigital "]);

        await Assert.That(aliases).HasSingleItem();
        await Assert.That(aliases[0].DisplayValue).IsEqualTo("CharlieDigital");
        await Assert.That(aliases[0].NormalizedValue).IsEqualTo("charliedigital");
    }
}
