namespace OpusMac.Core.Search;

public sealed class ListaryException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record ListaryServiceStatus(bool IsAvailable, string Message, string? ErrorCode = null);

/// <summary>Use a page's NextRequest to preserve its time, endpoint, and catalog snapshot.</summary>
public sealed record ListarySearchRequest(
    string Query,
    long Offset = 0,
    int Limit = 100,
    string SortField = "relevance",
    string SortDirection = "descending",
    ulong? ExpectedCatalogGeneration = null,
    long? NowUnixMilliseconds = null,
    int? TimezoneOffsetMinutes = null,
    string? ExpectedEndpointIdentity = null,
    IReadOnlyList<string>? Categories = null,
    string? ModifiedTime = null);

/// <summary>
/// Entry is an indexed metadata projection, with no synchronous filesystem probes.
/// RevalidateAsync returns current native metadata before the caller performs an action.
/// EndpointIdentity is a digest; it never contains the authentication token.
/// </summary>
public sealed record ListarySearchResult(
    FileEntry Entry,
    uint ItemId,
    Guid RootId,
    string? StableFileId,
    string IndexedKind,
    string EndpointIdentity,
    long IndexedSize,
    long IndexedModifiedUnixMilliseconds);

public sealed record ListarySearchPage(
    IReadOnlyList<ListarySearchResult> Results,
    long TotalMatches,
    long Offset,
    int Limit,
    bool HasMore,
    ulong CatalogGeneration,
    ListarySearchRequest Request,
    string EndpointIdentity,
    int RejectedResults)
{
    public ListarySearchRequest? NextRequest => HasMore
        ? Request with
        {
            Offset = checked(Offset + Limit),
            ExpectedCatalogGeneration = CatalogGeneration,
            ExpectedEndpointIdentity = EndpointIdentity
        }
        : null;
}
