namespace SteamSwitchboard.Services;

public static class SteamNavigationPolicy
{
    public const int MaximumEmbeddedUriCharacters = 8_192;
    public const int MaximumExternalUriCharacters = 2_048;

    private const string SteamCommunityHost = "steamcommunity.com";

    private static readonly string[] AllowedDocumentPathPrefixes =
    [
        "/chat",
        "/login"
    ];

    public static bool IsAllowedSteamNavigation(string? rawUri) =>
        IsAllowedEmbeddedDocument(rawUri);

    public static bool IsAllowedEmbeddedDocument(string? rawUri)
    {
        if (IsBootstrapDocument(rawUri))
        {
            return true;
        }

        if (!TryCreateSafeHttpsUri(
                rawUri,
                MaximumEmbeddedUriCharacters,
                out var uri)
            || !string.Equals(
                uri.Host,
                SteamCommunityHost,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AllowedDocumentPathPrefixes.Any(prefix =>
            string.Equals(uri.AbsolutePath, prefix, StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith(
                $"{prefix}/",
                StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsTrustedSteamOrigin(string? rawUri) =>
        TryCreateSafeHttpsUri(
            rawUri,
            MaximumEmbeddedUriCharacters,
            out var uri)
        && string.Equals(
            uri.Host,
            SteamCommunityHost,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsBootstrapDocument(string? rawUri) =>
        string.Equals(rawUri, "about:blank", StringComparison.OrdinalIgnoreCase);

    public static bool IsLoginDocument(Uri? uri) =>
        uri is not null
        && IsAllowedEmbeddedDocument(uri.AbsoluteUri)
        && (string.Equals(uri.AbsolutePath, "/login", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/login/", StringComparison.OrdinalIgnoreCase));

    public static bool CanRequestMicrophone(
        string? requestingUri,
        bool isUserInitiated,
        bool isWorkspaceVisible) =>
        isUserInitiated
        && isWorkspaceVisible
        && IsTrustedSteamOrigin(requestingUri);

    public static bool ShouldPromptForExternalLink(
        string? rawUri,
        bool isUserInitiated) =>
        isUserInitiated && GetSafeExternalUri(rawUri) is not null;

    public static Uri? GetSafeExternalUri(string? rawUri) =>
        TryCreateSafeHttpsUri(
            rawUri,
            MaximumExternalUriCharacters,
            out var uri)
            ? uri
            : null;

    private static bool TryCreateSafeHttpsUri(
        string? rawUri,
        int maximumCharacters,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(rawUri)
            || rawUri.Length > maximumCharacters
            || SafeText.ContainsUnsafeIdentityCharacters(rawUri)
            || !Uri.TryCreate(rawUri, UriKind.Absolute, out var parsed)
            || !string.Equals(
                parsed.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || !parsed.IsDefaultPort
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || string.IsNullOrWhiteSpace(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
