using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.GitHub.Tests.Configuration;

public class GitHubCliTokenProviderTests
{
    [Fact]
    public void ParseTokenResponse_ReturnsToken_WhenValidPlainText()
    {
        var result = GitHubCliTokenProvider.ParseTokenResponse("ghp_abc123XYZ789");

        Assert.NotNull(result);
        Assert.Equal("ghp_abc123XYZ789", result.AccessToken);
        Assert.True(result.ExpiresOn > DateTime.UtcNow.AddHours(23));
        Assert.True(result.ExpiresOn < DateTime.UtcNow.AddHours(25));
    }

    [Fact]
    public void ParseTokenResponse_TrimsWhitespace()
    {
        var result = GitHubCliTokenProvider.ParseTokenResponse("  ghp_abc123XYZ789  \n");

        Assert.NotNull(result);
        Assert.Equal("ghp_abc123XYZ789", result.AccessToken);
    }

    [Fact]
    public void ParseTokenResponse_ReturnsNull_WhenEmpty()
    {
        var result = GitHubCliTokenProvider.ParseTokenResponse(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void ParseTokenResponse_ReturnsNull_WhenNull()
    {
        var result = GitHubCliTokenProvider.ParseTokenResponse(null);

        Assert.Null(result);
    }

    [Fact]
    public void ParseTokenResponse_ReturnsNull_WhenWhitespace()
    {
        var result = GitHubCliTokenProvider.ParseTokenResponse("   \n  ");

        Assert.Null(result);
    }

    [Fact]
    public void DefaultTokenLifetime_Is24Hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), GitHubCliTokenProvider.DefaultTokenLifetime);
    }
}
