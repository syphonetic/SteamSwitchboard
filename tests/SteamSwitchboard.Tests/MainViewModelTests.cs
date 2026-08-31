using SteamSwitchboard.Models;
using SteamSwitchboard.Services;
using SteamSwitchboard.ViewModels;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    [TestMethod]
    public void NewSettings_DefaultToKeepingEveryChatConnected()
    {
        var settings = new AppSettings();

        Assert.IsTrue(settings.KeepAllChatsLive);
        Assert.IsTrue(settings.EnableWindowsNotifications);
        Assert.IsFalse(settings.ShowNotificationPreviews);
    }

    [TestMethod]
    public async Task Initialize_NotifiesExistingBindingsAboutPersistedConversationSettings()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        await new StateStore(statePath).SaveAsync(new PersistedState
        {
            Settings = new AppSettings
            {
                KeepAllChatsLive = false,
                EnableWindowsNotifications = false,
                ShowNotificationPreviews = true
            }
        });

        await WpfTestHost.RunAsync(async () =>
        {
            var viewModel = new MainViewModel(
                new StateStore(statePath),
                new SteamInstallationService(),
                new SteamLibraryService());
            var changedProperties = new HashSet<string?>(StringComparer.Ordinal);
            viewModel.PropertyChanged += (_, args) =>
                changedProperties.Add(args.PropertyName);

            await viewModel.InitializeAsync();

            Assert.IsFalse(viewModel.KeepAllChatsLive);
            Assert.IsFalse(viewModel.EnableWindowsNotifications);
            Assert.IsTrue(viewModel.ShowNotificationPreviews);
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    nameof(MainViewModel.KeepAllChatsLive),
                    nameof(MainViewModel.EnableWindowsNotifications),
                    nameof(MainViewModel.ShowNotificationPreviews)
                },
                changedProperties.ToArray());
        });
    }

    [TestMethod]
    public void ReadyChatStatus_ReportsLoadedWorkspaceWithoutClaimingIdentityVerification()
    {
        var account = new AccountViewModel(new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "main"
        })
        {
            ConnectionState = ChatConnectionState.Ready
        };

        Assert.AreEqual("Steam Chat workspace open", account.StatusText);
        Assert.IsFalse(
            account.StatusText.Contains("verify", StringComparison.OrdinalIgnoreCase));

        account.IsSleeping = true;
        Assert.AreEqual("Sleeping — notifications paused", account.StatusText);
    }

    [TestMethod]
    public async Task RenameAccount_UpdatesAndPersistsOnlyTheProfileNickname()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var store = new StateStore(statePath);
        var viewModel = new MainViewModel(
            store,
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Old label",
            SteamLoginName = "stable_login"
        });

        await viewModel.RenameAccountAsync(account, "  New label  ");
        var persisted = await store.LoadAsync();

        Assert.AreEqual("New label", account.DisplayName);
        Assert.AreEqual("New label", viewModel.SelectedProfileDisplayName);
        Assert.AreEqual("New label", persisted.Accounts.Single().DisplayName);
        Assert.AreEqual("stable_login", persisted.Accounts.Single().SteamLoginName);
        Assert.AreEqual(
            "New label — Steam login: stable_login",
            account.LaunchIdentityLabel);
        StringAssert.Contains(account.StatusText, "Steam");
    }

    [TestMethod]
    public async Task RenameAccount_RollsBackWhenPersistenceFails()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var viewModel = new MainViewModel(
            new StateStore(statePath),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Keep this label",
            SteamLoginName = "keep_label"
        });

        await using var lockStream = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => viewModel.RenameAccountAsync(account, "Must roll back"));

        Assert.AreEqual("Keep this label", account.DisplayName);
    }

    [TestMethod]
    public async Task RelinkSteamLogin_UpdatesPersistenceAndNotificationIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var store = new StateStore(statePath);
        var viewModel = new MainViewModel(
            store,
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Main profile",
            SteamLoginName = "wrong_login"
        });
        var notification = viewModel.AddNotification(
            account,
            new ChatNotificationPayload(
                "Alice",
                "hello",
                DateTimeOffset.UtcNow));

        await viewModel.RelinkSteamLoginAsync(account, "correct_login");
        var persisted = await store.LoadAsync();

        Assert.AreEqual("correct_login", account.SteamLoginName);
        Assert.AreEqual(
            "correct_login",
            viewModel.SelectedProfileSteamLoginName);
        Assert.AreEqual("correct_login", persisted.Accounts.Single().SteamLoginName);
        Assert.AreEqual("correct_login", notification.AccountLoginName);
        Assert.AreEqual("Main profile", account.DisplayName);
    }

    [TestMethod]
    public async Task RelinkSteamLogin_RejectsLoginLinkedToAnotherProfile()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        var first = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "First",
            SteamLoginName = "first_login"
        });
        _ = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Second",
            SteamLoginName = "second_login"
        });

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => viewModel.RelinkSteamLoginAsync(first, "second_login"));

        Assert.AreEqual("first_login", first.SteamLoginName);
    }

    [TestMethod]
    public async Task PlayAccountChoice_PersistsIndependentlyFromChatSelection()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var store = new StateStore(statePath);
        var viewModel = new MainViewModel(
            store,
            new SteamInstallationService(),
            new SteamLibraryService());
        var first = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Chat account",
            SteamLoginName = "chat_account"
        });
        var second = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Play account",
            SteamLoginName = "play_account"
        });
        viewModel.SelectedAccount = first;
        viewModel.SelectedPlayAccount = second;
        await viewModel.SaveAsync();

        var persisted = await store.LoadAsync();

        Assert.AreEqual(first.Id, persisted.LastSelectedAccountId);
        Assert.AreEqual(second.Id, persisted.LastPlayAccountId);
    }

    [TestMethod]
    public async Task AddingAnotherAccount_PreservesTheExistingPlayAccountChoice()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        var first = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Keep for games",
            SteamLoginName = "keep_for_games"
        });

        _ = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "New chat",
            SteamLoginName = "new_chat"
        });

        Assert.AreSame(first, viewModel.SelectedPlayAccount);
    }

    [TestMethod]
    public async Task LaunchCheck_DisablesAndRestoresEveryLibraryLaunchAction()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        _ = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Launch profile",
            SteamLoginName = "launch_profile"
        });

        Assert.IsTrue(viewModel.CanLaunchGames);
        Assert.IsTrue(viewModel.TryBeginLaunchCheck());
        Assert.IsFalse(viewModel.CanLaunchGames);
        Assert.IsFalse(viewModel.TryBeginLaunchCheck());

        viewModel.EndLaunchCheck();

        Assert.IsTrue(viewModel.CanLaunchGames);
    }

    [TestMethod]
    public async Task RemovingThePlayAccount_RequiresAnExplicitNewChoice()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var viewModel = new MainViewModel(
            new StateStore(statePath),
            new SteamInstallationService(),
            new SteamLibraryService());
        var first = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Remove me",
            SteamLoginName = "remove_me"
        });
        _ = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Remain",
            SteamLoginName = "remain"
        });
        viewModel.SelectedPlayAccount = first;

        await viewModel.RemoveAccountAsync(first);

        Assert.IsNull(viewModel.SelectedPlayAccount);
        Assert.AreEqual(
            "Choose the Steam account required for launches",
            viewModel.SelectedPlayAccountStatus);

        await WpfTestHost.RunAsync(async () =>
        {
            var restarted = new MainViewModel(
                new StateStore(statePath),
                new SteamInstallationService(),
                new SteamLibraryService());
            await restarted.InitializeAsync();

            Assert.IsNull(restarted.SelectedPlayAccount);
            Assert.AreEqual(
                "Choose the Steam account required for launches",
                restarted.SelectedPlayAccountStatus);
        });
    }

    [TestMethod]
    public async Task Notifications_AlwaysIdentifyAccountAndSenderButHidePreviewByDefault()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Trading",
            SteamLoginName = "trading"
        });

        var notification = viewModel.AddNotification(
            account,
            new ChatNotificationPayload(
                "Alice",
                "secret preview",
                DateTimeOffset.UtcNow));

        Assert.AreEqual("Trading", notification.AccountDisplayName);
        Assert.AreEqual("Alice", notification.SteamTitle);
        Assert.AreEqual("New Steam Chat message", notification.Preview);
        Assert.IsTrue(viewModel.HasNotifications);
        Assert.AreEqual(1, viewModel.NotificationCount);
    }

    [TestMethod]
    public async Task NotificationHistory_IsMemoryOnlyAndBounded()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var viewModel = new MainViewModel(
            new StateStore(statePath),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "main"
        });

        for (var index = 0; index < 125; index++)
        {
            viewModel.AddNotification(
                account,
                new ChatNotificationPayload(
                    $"Sender {index}",
                    "Message",
                    DateTimeOffset.UtcNow));
        }

        Assert.HasCount(100, viewModel.Notifications);
        var json = await File.ReadAllTextAsync(statePath);
        Assert.IsFalse(json.Contains("Sender", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Message", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TaggedNotificationReplacement_ClosesOldLifecycleAndKeepsOneEntry()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "main"
        });
        var oldClosed = 0;
        var replacementClicked = 0;
        var replacementClosed = 0;
        _ = viewModel.AddNotification(
            account,
            new ChatNotificationPayload("Alice", "first", DateTimeOffset.UtcNow)
            {
                ReplacementTag = "conversation-1"
            },
            reportClosed: () => oldClosed++);

        var replacement = viewModel.AddNotification(
            account,
            new ChatNotificationPayload("Alice", "updated", DateTimeOffset.UtcNow)
            {
                ReplacementTag = "conversation-1"
            },
            reportClicked: () => replacementClicked++,
            reportClosed: () => replacementClosed++);
        replacement.ReportClickedAndClose();

        Assert.HasCount(1, viewModel.Notifications);
        Assert.AreEqual(1, oldClosed);
        Assert.AreEqual(1, replacementClicked);
        Assert.AreEqual(1, replacementClosed);
    }

    [TestMethod]
    public async Task NativeNotification_ReplacesRecentUnreadFallback()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Main",
            SteamLoginName = "main"
        });
        _ = viewModel.AddNotification(
            account,
            new ChatNotificationPayload("Steam Chat", "fallback", DateTimeOffset.UtcNow)
            {
                IsUnreadFallback = true
            });

        _ = viewModel.AddNotification(
            account,
            new ChatNotificationPayload("Alice", "native", DateTimeOffset.UtcNow)
            {
                ReplacesUnreadFallback = true
            });

        Assert.HasCount(1, viewModel.Notifications);
        Assert.AreEqual("Alice", viewModel.Notifications[0].SteamTitle);
        Assert.IsFalse(viewModel.Notifications[0].IsUnreadFallback);
    }

    [TestMethod]
    public async Task DisablingPreviews_RedactsExistingNotificationHistory()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Private",
            SteamLoginName = "private"
        });
        viewModel.ShowNotificationPreviews = true;
        var notification = viewModel.AddNotification(
            account,
            new ChatNotificationPayload("Alice", "sensitive text", DateTimeOffset.UtcNow));

        viewModel.ShowNotificationPreviews = false;

        Assert.AreEqual("New Steam Chat message", notification.Preview);
    }

    [TestMethod]
    public async Task MarkNotificationsRead_OnlyClearsTheChosenAccount()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        var first = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "First",
            SteamLoginName = "first"
        });
        var second = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Second",
            SteamLoginName = "second"
        });
        var firstNotification = viewModel.AddNotification(
            first,
            new ChatNotificationPayload("Alice", "one", DateTimeOffset.UtcNow));
        var secondNotification = viewModel.AddNotification(
            second,
            new ChatNotificationPayload("Bob", "two", DateTimeOffset.UtcNow));

        viewModel.MarkNotificationsRead(first.Id);

        Assert.IsFalse(firstNotification.IsUnread);
        Assert.IsTrue(secondNotification.IsUnread);
        Assert.AreEqual(1, viewModel.NotificationCount);
        Assert.IsTrue(viewModel.HasUnreadNotifications);
    }

    [TestMethod]
    public async Task Initialize_PreservesTheCorruptStateRecoveryWarning()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        await File.WriteAllTextAsync(
            statePath,
            """{"schemaVersion":3,"accounts":[],"settings":{},"unexpected":true}""");
        await WpfTestHost.RunAsync(async () =>
        {
            var viewModel = new MainViewModel(
                new StateStore(statePath),
                new SteamInstallationService(),
                new SteamLibraryService());

            await viewModel.InitializeAsync();

            StringAssert.Contains(viewModel.StatusMessage, "recovery copy");
            Assert.IsEmpty(viewModel.Accounts);
        });
    }

    [TestMethod]
    public void RemoveSelectedAccount_SelectsTheNextRemainingAccount()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var first = new AccountProfile
        {
            DisplayName = "First",
            SteamLoginName = "first_login"
        };
        var second = new AccountProfile
        {
            DisplayName = "Second",
            SteamLoginName = "second_login"
        };
        var store = new StateStore(statePath);
        var viewModel = new MainViewModel(
            store,
            new SteamInstallationService(),
            new SteamLibraryService());
        var firstViewModel = viewModel.AddAccountAsync(first).GetAwaiter().GetResult();
        _ = viewModel.AddAccountAsync(second).GetAwaiter().GetResult();
        viewModel.SelectedAccount = firstViewModel;

        viewModel.RemoveAccountAsync(firstViewModel).GetAwaiter().GetResult();

        Assert.IsNotNull(viewModel.SelectedAccount);
        Assert.AreEqual("Second", viewModel.SelectedAccount.DisplayName);
        Assert.HasCount(1, viewModel.Accounts);
    }

    [TestMethod]
    public async Task AddAccount_RollsBackMemoryWhenPersistenceFails()
    {
        using var temporary = new TemporaryDirectory();
        var parentFile = temporary.CreateFile("not-a-directory", "blocked");
        var statePath = System.IO.Path.Combine(parentFile, "state.json");
        var viewModel = new MainViewModel(
            new StateStore(statePath),
            new SteamInstallationService(),
            new SteamLibraryService());

        await Assert.ThrowsExactlyAsync<IOException>(
            () => viewModel.AddAccountAsync(new AccountProfile
            {
                DisplayName = "Will roll back",
                SteamLoginName = "rollback"
            }));

        Assert.IsEmpty(viewModel.Accounts);
        Assert.IsNull(viewModel.SelectedAccount);
    }

    [TestMethod]
    public async Task RemoveAccount_RollsBackMemoryWhenPersistenceFails()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var viewModel = new MainViewModel(
            new StateStore(statePath),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Keep me",
            SteamLoginName = "keep_me"
        });

        await using var lockStream = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => viewModel.RemoveAccountAsync(account));

        Assert.HasCount(1, viewModel.Accounts);
        Assert.AreSame(account, viewModel.SelectedAccount);
    }

    [TestMethod]
    public async Task ProfileDeletionTombstonePersistsUntilAccountRemovalSucceeds()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var store = new StateStore(statePath);
        var viewModel = new MainViewModel(
            store,
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Disposable",
            SteamLoginName = "disposable"
        });

        await viewModel.MarkAccountDeletionPendingAsync(account);
        var pending = await store.LoadAsync();

        CollectionAssert.AreEqual(
            new[] { account.Id },
            pending.PendingBrowserProfileDeletionIds);
        CollectionAssert.AreEqual(
            new[] { account.Id },
            pending.PendingWindowsNotificationAccountCleanupIds);
        Assert.IsTrue(viewModel.IsAccountDeletionPending(account.Id));

        await viewModel.RemoveAccountAsync(account);
        var completed = await store.LoadAsync();
        Assert.IsEmpty(completed.Accounts);
        Assert.IsEmpty(completed.PendingBrowserProfileDeletionIds);
        CollectionAssert.AreEqual(
            new[] { account.Id },
            completed.PendingWindowsNotificationAccountCleanupIds);

        var cleanupRequestId = Assert.IsInstanceOfType<Guid>(
            viewModel.PendingWindowsNotificationCleanupRequestId);
        await viewModel.CompleteWindowsNotificationCleanupAsync(
            removedAll: false,
            [account.Id],
            cleanupRequestId);
        var cleaned = await store.LoadAsync();
        Assert.IsEmpty(cleaned.PendingWindowsNotificationAccountCleanupIds);
    }

    [TestMethod]
    public async Task GlobalWindowsCleanupIntentPersistsUntilConfirmed()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var store = new StateStore(statePath);
        var viewModel = new MainViewModel(
            store,
            new SteamInstallationService(),
            new SteamLibraryService());

        Assert.IsTrue(viewModel.MarkWindowsNotificationCleanupPending(null));
        await viewModel.SaveAsync();
        var pending = await store.LoadAsync();
        Assert.IsTrue(pending.PendingWindowsNotificationHistoryClear);

        var cleanupRequestId = Assert.IsInstanceOfType<Guid>(
            viewModel.PendingWindowsNotificationCleanupRequestId);
        await viewModel.CompleteWindowsNotificationCleanupAsync(
            removedAll: true,
            [],
            cleanupRequestId);
        var completed = await store.LoadAsync();
        Assert.IsFalse(completed.PendingWindowsNotificationHistoryClear);
    }

    [TestMethod]
    public async Task FailedWindowsCleanupConfirmationKeepsDurableRetryIntent()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var viewModel = new MainViewModel(
            new StateStore(statePath),
            new SteamInstallationService(),
            new SteamLibraryService());
        _ = viewModel.MarkWindowsNotificationCleanupPending(null);
        await viewModel.SaveAsync();

        await using var lockStream = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => viewModel.CompleteWindowsNotificationCleanupAsync(
                removedAll: true,
                [],
                Assert.IsInstanceOfType<Guid>(
                    viewModel.PendingWindowsNotificationCleanupRequestId)));

        Assert.IsTrue(viewModel.HasPendingWindowsNotificationHistoryClear);
    }

    [TestMethod]
    public void NotificationCleanupRequestsEscalateToBoundedGlobalClear()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        for (var index = 0; index < AccountValidator.MaximumAccountProfiles; index++)
        {
            Assert.IsTrue(
                viewModel.MarkWindowsNotificationCleanupPending(Guid.NewGuid()));
        }

        Assert.IsTrue(
            viewModel.MarkWindowsNotificationCleanupPending(Guid.NewGuid()));

        Assert.IsTrue(viewModel.HasPendingWindowsNotificationHistoryClear);
        Assert.IsEmpty(viewModel.AccountsPendingWindowsNotificationCleanup);
    }

    [TestMethod]
    public async Task StaleCleanupConfirmationCannotClearNewerPrivacyRequest()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = new MainViewModel(
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json")),
            new SteamInstallationService(),
            new SteamLibraryService());
        _ = viewModel.MarkWindowsNotificationCleanupPending(null);
        var staleRequestId = Assert.IsInstanceOfType<Guid>(
            viewModel.PendingWindowsNotificationCleanupRequestId);
        _ = viewModel.MarkWindowsNotificationCleanupPending(null);
        var currentRequestId = Assert.IsInstanceOfType<Guid>(
            viewModel.PendingWindowsNotificationCleanupRequestId);

        await viewModel.CompleteWindowsNotificationCleanupAsync(
            removedAll: true,
            [],
            staleRequestId);

        Assert.IsTrue(viewModel.HasPendingWindowsNotificationHistoryClear);
        Assert.AreEqual(
            currentRequestId,
            viewModel.PendingWindowsNotificationCleanupRequestId);
    }

    [TestMethod]
    public async Task FailedDeletionSaveCannotRollbackNewerPrivacyGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var viewModel = new MainViewModel(
            new StateStore(statePath),
            new SteamInstallationService(),
            new SteamLibraryService());
        var account = await viewModel.AddAccountAsync(new AccountProfile
        {
            DisplayName = "Keep pending",
            SteamLoginName = "keep_pending"
        });
        await using var lockStream = new FileStream(
            statePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var deletion = viewModel.MarkAccountDeletionPendingAsync(account);
        _ = viewModel.MarkWindowsNotificationCleanupPending(accountId: null);
        var newerRequestId = Assert.IsInstanceOfType<Guid>(
            viewModel.PendingWindowsNotificationCleanupRequestId);
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(
            () => deletion);

        Assert.IsTrue(viewModel.HasPendingWindowsNotificationHistoryClear);
        Assert.AreEqual(
            newerRequestId,
            viewModel.PendingWindowsNotificationCleanupRequestId);
    }
}
