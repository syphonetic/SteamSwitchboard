using SteamSwitchboard.Models;
using SteamSwitchboard.Services;
using SteamSwitchboard.ViewModels;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class MainViewModelTests
{
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
        Assert.IsTrue(viewModel.IsAccountDeletionPending(account.Id));

        await viewModel.RemoveAccountAsync(account);
        var completed = await store.LoadAsync();
        Assert.IsEmpty(completed.Accounts);
        Assert.IsEmpty(completed.PendingBrowserProfileDeletionIds);
    }
}
