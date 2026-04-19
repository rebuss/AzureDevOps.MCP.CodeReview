using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Services.Pagination;

namespace REBUSS.Pure.Tests.Services.Pagination;

public class PageReferenceCodecTests
{
    private readonly PageReferenceCodec _codec = new(NullLogger<PageReferenceCodec>.Instance);

    // --- Round-trip encode→decode ---

    [Fact]
    public void Encode_ThenDecode_RoundTrip_IdenticalData()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        var data = new PageReferenceData("get_pr_files", requestParams, 89600, 2, "abc123def456");

        var encoded = _codec.Encode(data);
        var decoded = _codec.TryDecode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(data.ToolName, decoded.ToolName);
        Assert.Equal(data.SafeBudgetTokens, decoded.SafeBudgetTokens);
        Assert.Equal(data.PageNumber, decoded.PageNumber);
        Assert.Equal(data.DataFingerprint, decoded.DataFingerprint);
        // Verify request params preserved (Q16)
        Assert.Equal(42, decoded.RequestParams.GetProperty("prNumber").GetInt32());
    }

    // --- PR tool with fingerprint ---

    [Fact]
    public void Encode_PrToolWithFingerprint_PreservesAllFields()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":99}").RootElement;
        var data = new PageReferenceData("get_pr_files", requestParams, 50000, 3, "sha123");

        var encoded = _codec.Encode(data);
        var decoded = _codec.TryDecode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal("get_pr_files", decoded.ToolName);
        Assert.Equal(99, decoded.RequestParams.GetProperty("prNumber").GetInt32());
        Assert.Equal(50000, decoded.SafeBudgetTokens);
        Assert.Equal(3, decoded.PageNumber);
        Assert.Equal("sha123", decoded.DataFingerprint);
    }

    // --- Local tool with null fingerprint ---

    [Fact]
    public void Encode_LocalToolNullFingerprint_PreservesNull()
    {
        var requestParams = JsonDocument.Parse("{\"scope\":\"staged\"}").RootElement;
        var data = new PageReferenceData("get_local_files", requestParams, 30000, 1, null);

        var encoded = _codec.Encode(data);
        var decoded = _codec.TryDecode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal("get_local_files", decoded.ToolName);
        Assert.Equal("staged", decoded.RequestParams.GetProperty("scope").GetString());
        Assert.Null(decoded.DataFingerprint);
    }

    // --- Actual request params preserved (not hashed, Q16) ---

    [Fact]
    public void Encode_ActualRequestParamsPreserved_NotHashed()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        var data = new PageReferenceData("get_pr_files", requestParams, 89600, 1, "abc");

        var encoded = _codec.Encode(data);
        var decoded = _codec.TryDecode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(JsonValueKind.Object, decoded.RequestParams.ValueKind);
        Assert.True(decoded.RequestParams.TryGetProperty("prNumber", out var prNum));
        Assert.Equal(42, prNum.GetInt32());
    }

    // --- Malformed Base64 → null ---

    [Fact]
    public void TryDecode_MalformedBase64_ReturnsNull()
    {
        var result = _codec.TryDecode("not-valid-base64!!!");
        Assert.Null(result);
    }

    // --- Valid Base64 invalid JSON → null ---

    [Fact]
    public void TryDecode_ValidBase64InvalidJson_ReturnsNull()
    {
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not json"));
        var result = _codec.TryDecode(base64);
        Assert.Null(result);
    }

    // --- Valid JSON missing fields → null ---

    [Fact]
    public void TryDecode_ValidJsonMissingFields_ReturnsNull()
    {
        var json = "{\"t\":\"test\"}"; // Missing b, p, r
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        var result = _codec.TryDecode(base64);
        Assert.Null(result);
    }

    // --- Empty string → null ---

    [Fact]
    public void TryDecode_EmptyString_ReturnsNull()
    {
        Assert.Null(_codec.TryDecode(""));
        Assert.Null(_codec.TryDecode("   "));
    }

    // --- Generic error safety (Q23) ---

    [Fact]
    public void TryDecode_NeverThrows_ForAnyMalformedInput()
    {
        // Should never throw, only return null
        Assert.Null(_codec.TryDecode(""));
        Assert.Null(_codec.TryDecode("a"));
        Assert.Null(_codec.TryDecode("==="));
        Assert.Null(_codec.TryDecode("eyJ0IjoiIn0")); // {"t":""}
        Assert.Null(_codec.TryDecode(new string('A', 10000))); // Very long string
        Assert.Null(_codec.TryDecode("null"));
        Assert.Null(_codec.TryDecode("undefined"));
    }

    // --- Encode produces non-empty opaque string ---

    [Fact]
    public void Encode_ProducesNonEmptyString()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":1}").RootElement;
        var data = new PageReferenceData("get_pr_files", requestParams, 1000, 1, null);

        var encoded = _codec.Encode(data);

        Assert.False(string.IsNullOrEmpty(encoded));
        Assert.DoesNotContain("=", encoded); // Base64url: no padding
        Assert.DoesNotContain("+", encoded); // Base64url: no +
        Assert.DoesNotContain("/", encoded); // Base64url: no /
    }

    // --- Different page numbers produce different tokens ---

    [Fact]
    public void Encode_DifferentPageNumbers_DifferentTokens()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        var data1 = new PageReferenceData("get_pr_files", requestParams, 89600, 1, "abc");
        var data2 = new PageReferenceData("get_pr_files", requestParams, 89600, 2, "abc");

        Assert.NotEqual(_codec.Encode(data1), _codec.Encode(data2));
    }
}
