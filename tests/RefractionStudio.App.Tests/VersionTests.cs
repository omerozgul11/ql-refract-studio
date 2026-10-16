using RefractionStudio.Infrastructure;

namespace RefractionStudio.App.Tests;

public sealed class VersionTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("1.2.0", "1.2.0-beta.10")]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.2")]
    [InlineData("1.2.0-beta", "1.2.0-alpha.5")]
    [InlineData("1.2.0-beta.2.1", "1.2.0-beta.2")]
    [InlineData("1.2.0-rc.1", "1.2.0-beta.11")]
    [InlineData("1.2.0-alpha.beta", "1.2.0-alpha.1")]
    public void Semantic_version_precedence(string newer, string older)
    {
        Assert.True(UpdateService.IsNewer(newer, older));
        Assert.False(UpdateService.IsNewer(older, newer));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2.0+build.5", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    public void Equal_versions_are_not_newer(string a, string b)
    {
        Assert.Equal(0, UpdateService.CompareVersions(a, b));
        Assert.False(UpdateService.IsNewer(a, b));
    }
}
