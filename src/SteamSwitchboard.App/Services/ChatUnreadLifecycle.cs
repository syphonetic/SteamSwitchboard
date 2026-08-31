namespace SteamSwitchboard.Services;

internal readonly record struct ChatNotificationUnreadDecision(
    int UnreadCount,
    bool ShouldMarkRead)
{
    internal bool ShouldShowWindowsNotification => !ShouldMarkRead;
}

internal static class ChatUnreadLifecycle
{
    internal static ChatNotificationUnreadDecision ResolveAfterNotification(
        int currentUnreadCount,
        bool isSelectedConversation,
        bool isWorkspaceActuallyVisible)
    {
        var shouldMarkRead = isSelectedConversation
            && isWorkspaceActuallyVisible;
        return new ChatNotificationUnreadDecision(
            shouldMarkRead ? 0 : Math.Max(1, currentUnreadCount),
            shouldMarkRead);
    }
}
