using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class ChatNotificationPolicyTests
{
    [TestMethod]
    public void TryCreate_AcceptsExactSteamOriginAndSanitizesUntrustedText()
    {
        var accepted = ChatNotificationPolicy.TryCreate(
            "https://steamcommunity.com/",
            "Alice\r\nspoof",
            $"hello\u202Eworld{new string('x', 300)}",
            out var notification);

        Assert.IsTrue(accepted);
        Assert.IsFalse(notification.SteamTitle.Contains('\r'));
        Assert.IsFalse(notification.SteamTitle.Contains('\n'));
        Assert.IsFalse(notification.Preview.Contains('\u202E'));
        Assert.IsLessThanOrEqualTo(
            ChatNotificationPolicy.MaximumTitleLength,
            notification.SteamTitle.Length);
        Assert.IsLessThanOrEqualTo(
            ChatNotificationPolicy.MaximumPreviewLength,
            notification.Preview.Length);
    }

    [TestMethod]
    [DataRow("https://steamcommunity.com.example/")]
    [DataRow("https://evil.example/")]
    [DataRow("http://steamcommunity.com/")]
    [DataRow("https://steamcommunity.com:444/")]
    [DataRow("javascript:alert(1)")]
    [DataRow(null)]
    public void TryCreate_RejectsUntrustedNotificationOrigins(string? origin)
    {
        var accepted = ChatNotificationPolicy.TryCreate(
            origin,
            "Alice",
            "Hello",
            out _);

        Assert.IsFalse(accepted);
    }

    [TestMethod]
    public void TryCreate_UsesSafeFallbacksForBlankContent()
    {
        var accepted = ChatNotificationPolicy.TryCreate(
            "https://steamcommunity.com/chat/",
            " ",
            null,
            out var notification);

        Assert.IsTrue(accepted);
        Assert.AreEqual("Steam Chat", notification.SteamTitle);
        Assert.AreEqual("New Steam Chat message", notification.Preview);
    }

    [TestMethod]
    public void RawSizePolicy_RejectsOversizedContentBeforeSanitization()
    {
        Assert.IsFalse(ChatNotificationPolicy.HasAcceptableRawSize(
            new string('t', ChatNotificationPolicy.MaximumRawTitleLength + 1),
            "body",
            null));
        Assert.IsFalse(ChatNotificationPolicy.HasAcceptableRawSize(
            "title",
            new string('b', ChatNotificationPolicy.MaximumRawPreviewLength + 1),
            null));
        Assert.IsNull(ChatNotificationPolicy.NormalizeReplacementTag(
            new string('x', ChatNotificationPolicy.MaximumReplacementTagLength + 1)));
    }
}
