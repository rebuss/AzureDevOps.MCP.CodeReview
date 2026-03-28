using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.Pagination;

namespace REBUSS.Pure.Services.Pagination;

/// <summary>
/// Base64url JSON codec for page reference tokens.
/// Encodes PageReferenceData to compact JSON with short keys, then Base64url-encodes (no padding).
/// TryDecode returns null on any error — never throws, never exposes internals (FR-011, Q23).
/// Round-trip guarantee: TryDecode(Encode(data)) == data.
/// </summary>
public sealed class PageReferenceCodec : IPageReferenceCodec
{
    private readonly ILogger<PageReferenceCodec> _logger;

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null // Use explicit property names
    };

    public PageReferenceCodec(ILogger<PageReferenceCodec> logger)
    {
        _logger = logger;
    }

    public string Encode(PageReferenceData data)
    {
        var compact = new CompactPageReference
        {
            T = data.ToolName,
            R = data.RequestParams,
            B = data.SafeBudgetTokens,
            P = data.PageNumber,
            F = data.DataFingerprint
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(compact, CompactOptions);
        return Base64UrlEncode(json);
    }

    public PageReferenceData? TryDecode(string pageReference)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pageReference))
                return null;

            var bytes = Base64UrlDecode(pageReference);
            if (bytes == null)
                return null;

            var compact = JsonSerializer.Deserialize<CompactPageReference>(bytes, CompactOptions);
            if (compact == null)
                return null;

            // Validate required fields
            if (string.IsNullOrEmpty(compact.T) || compact.B <= 0 || compact.P <= 0)
                return null;

            // Validate RequestParams is present (not undefined)
            if (compact.R.ValueKind == JsonValueKind.Undefined)
                return null;

            return new PageReferenceData(
                compact.T,
                compact.R,
                compact.B,
                compact.P,
                compact.F);
        }
        catch (Exception ex)
        {
            // Per Q23/FR-011: never throw, never expose field-level detail beyond DEBUG
            _logger.LogDebug(ex, "[PageReferenceCodec] Failed to decode page reference");
            return null;
        }
    }

    // --- Base64url helpers (RFC 4648 §5, no padding) ---

    private static string Base64UrlEncode(byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        return base64
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[]? Base64UrlDecode(string base64Url)
    {
        try
        {
            var base64 = base64Url
                .Replace('-', '+')
                .Replace('_', '/');

            // Add padding
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
            }

            return Convert.FromBase64String(base64);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compact JSON representation with short keys to minimize token cost.
    /// </summary>
    private sealed class CompactPageReference
    {
        [JsonPropertyName("t")]
        public string T { get; set; } = string.Empty;

        [JsonPropertyName("r")]
        public JsonElement R { get; set; }

        [JsonPropertyName("b")]
        public int B { get; set; }

        [JsonPropertyName("p")]
        public int P { get; set; }

        [JsonPropertyName("f")]
        public string? F { get; set; }
    }
}
