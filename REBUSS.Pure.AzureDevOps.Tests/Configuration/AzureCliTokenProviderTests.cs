using REBUSS.Pure.AzureDevOps.Configuration;

namespace REBUSS.Pure.AzureDevOps.Tests.Configuration;

public class AzureCliTokenProviderTests
{
    [Fact]
    public void ParseTokenResponse_ReturnsToken_WhenValidJson()
    {
        var json = """
            {
              "accessToken": "eyJ0eXAiOiJKV1Q...",
              "expiresOn": "2025-01-15 14:30:00.000000",
              "tokenType": "Bearer"
            }
            """;

        var result = AzureCliTokenProvider.ParseTokenResponse(json);

        Assert.NotNull(result);
        Assert.Equal("eyJ0eXAiOiJKV1Q...", result.AccessToken);
        Assert.True(result.ExpiresOn > DateTime.MinValue);
    }

    [Fact]
    public void ParseTokenResponse_ReturnsNull_WhenNoAccessTokenProperty()
    {
        var json = """{ "tokenType": "Bearer" }""";

        var result = AzureCliTokenProvider.ParseTokenResponse(json);

        Assert.Null(result);
    }

    [Fact]
    public void ParseTokenResponse_ReturnsNull_WhenAccessTokenIsEmpty()
    {
        var json = """{ "accessToken": "", "expiresOn": "2025-01-15 14:30:00" }""";

        var result = AzureCliTokenProvider.ParseTokenResponse(json);

        Assert.Null(result);
    }

    [Fact]
    public void ParseTokenResponse_ReturnsToken_WhenExpiresOnMissing()
    {
        var json = """{ "accessToken": "some-token" }""";

        var result = AzureCliTokenProvider.ParseTokenResponse(json);

        Assert.NotNull(result);
        Assert.Equal("some-token", result.AccessToken);
        // Should default to approximately 1 hour from now
        Assert.True(result.ExpiresOn > DateTime.UtcNow.AddMinutes(50));
    }

    [Fact]
    public void ParseTokenResponse_HandlesIso8601ExpiresOn()
    {
        var expiresOn = DateTime.UtcNow.AddHours(1);
        var json = $"{{\"accessToken\":\"test-token\",\"expiresOn\":\"{expiresOn:yyyy-MM-ddTHH:mm:ssZ}\"}}";

        var result = AzureCliTokenProvider.ParseTokenResponse(json);

        Assert.NotNull(result);
        Assert.Equal("test-token", result.AccessToken);
    }

    [Fact]
    public void AzureDevOpsResourceId_IsCorrect()
    {
        Assert.Equal("499b84ac-1321-427f-aa17-267ca6975798", AzureCliTokenProvider.AzureDevOpsResourceId);
    }
}
