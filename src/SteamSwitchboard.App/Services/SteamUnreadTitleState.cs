namespace SteamSwitchboard.Services;

internal static class SteamUnreadTitleState
{
    private const int MaximumTrackedUnreadCount = 100;
    private const int MaximumPrefixDigits = 10;

    internal static int ResolveUnreadCount(
        string? documentTitle,
        int currentUnreadCount,
        bool isWorkspaceVisible)
    {
        if (isWorkspaceVisible)
        {
            return 0;
        }

        var normalizedCurrent = Math.Max(0, currentUnreadCount);
        return TryReadUnreadCount(documentTitle, out var titleUnreadCount)
            ? titleUnreadCount
            : normalizedCurrent;
    }

    private static bool TryReadUnreadCount(
        string? documentTitle,
        out int unreadCount)
    {
        unreadCount = 0;
        if (string.IsNullOrEmpty(documentTitle)
            || documentTitle.Length < 3
            || documentTitle[0] != '(')
        {
            return false;
        }

        var searchLength = Math.Min(
            MaximumPrefixDigits + 1,
            documentTitle.Length - 1);
        var closingParenthesis = documentTitle
            .AsSpan(1, searchLength)
            .IndexOf(')');
        if (closingParenthesis <= 0)
        {
            return false;
        }

        var value = 0;
        foreach (var character in documentTitle.AsSpan(1, closingParenthesis))
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            if (value < MaximumTrackedUnreadCount)
            {
                value = Math.Min(
                    MaximumTrackedUnreadCount,
                    (value * 10) + (character - '0'));
            }
        }

        unreadCount = value;
        return true;
    }
}
