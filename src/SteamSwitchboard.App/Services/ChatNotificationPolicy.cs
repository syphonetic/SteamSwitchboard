using SteamSwitchboard.Models;

namespace SteamSwitchboard.Services;

public static class ChatNotificationPolicy
{
    public const int MaximumTitleLength = 80;
    public const int MaximumPreviewLength = 240;
    public const int MaximumRawTitleLength = 4_096;
    public const int MaximumRawPreviewLength = 16_384;
    public const int MaximumReplacementTagLength = 256;

    public static bool HasAcceptableRawSize(
        string? title,
        string? body,
        string? replacementTag) =>
        (title?.Length ?? 0) <= MaximumRawTitleLength
        && (body?.Length ?? 0) <= MaximumRawPreviewLength
        && (replacementTag?.Length ?? 0) <= MaximumReplacementTagLength;

    public static string? NormalizeReplacementTag(string? replacementTag)
    {
        if (string.IsNullOrWhiteSpace(replacementTag)
            || replacementTag.Length > MaximumReplacementTagLength
            || SafeText.ContainsUnsafeIdentityCharacters(replacementTag))
        {
            return null;
        }

        return replacementTag;
    }

    public static bool TryCreate(
        string? senderOrigin,
        string? title,
        string? body,
        out ChatNotificationPayload payload)
    {
        payload = null!;
        if (!SteamNavigationPolicy.IsTrustedSteamOrigin(senderOrigin))
        {
            return false;
        }

        payload = new ChatNotificationPayload(
            SafeText.SanitizeDisplayText(
                title,
                "Steam Chat",
                MaximumTitleLength),
            SafeText.SanitizeDisplayText(
                body,
                "New Steam Chat message",
                MaximumPreviewLength),
            DateTimeOffset.UtcNow);
        return true;
    }
}
