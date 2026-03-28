using REBUSS.Pure.Core.Models.Pagination;

namespace REBUSS.Pure.Core;

/// <summary>
/// Encodes/decodes page reference tokens.
/// Page references are opaque Base64url-encoded JSON strings (FR-010).
/// </summary>
public interface IPageReferenceCodec
{
    /// <summary>
    /// Produces an opaque page reference token from the given data.
    /// </summary>
    string Encode(PageReferenceData data);

    /// <summary>
    /// Attempts to decode a page reference token.
    /// Returns null for malformed/invalid tokens (FR-011).
    /// Never throws — returns null on any error.
    /// </summary>
    PageReferenceData? TryDecode(string pageReference);
}
