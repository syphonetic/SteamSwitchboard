using SteamSwitchboard.Models;
using SteamSwitchboard.Services;

namespace SteamSwitchboard.Tests;

[TestClass]
public sealed class StateStoreTests
{
    [TestMethod]
    public async Task SaveAndLoad_RoundTripsProfilesWithoutCredentialFields()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var store = new StateStore(statePath);
        var accountId = Guid.NewGuid();
        var state = new PersistedState
        {
            LastSelectedAccountId = accountId,
            LastPlayAccountId = accountId,
            Accounts =
            [
                new AccountProfile
                {
                    Id = accountId,
                    DisplayName = "Main",
                    SteamLoginName = "main_login"
                }
            ]
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        var json = await File.ReadAllTextAsync(statePath);

        Assert.AreEqual(accountId, loaded.LastSelectedAccountId);
        Assert.AreEqual(accountId, loaded.LastPlayAccountId);
        Assert.AreEqual("main_login", loaded.Accounts.Single().SteamLoginName);
        Assert.IsFalse(json.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("guard", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, Directory.EnumerateFiles(temporary.Path, "*.tmp").Count());
    }

    [TestMethod]
    public async Task Load_PreservesCorruptStateForRecovery()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile("state.json", "{not valid json");
        var store = new StateStore(statePath);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync());

        Assert.AreEqual(
            1,
            Directory.EnumerateFiles(temporary.Path, "state.corrupt.*.json").Count());
    }

    [TestMethod]
    public async Task Load_RejectsDuplicateAccountIdentifiersBeforeUse()
    {
        using var temporary = new TemporaryDirectory();
        var id = Guid.NewGuid();
        var statePath = temporary.CreateFile(
            "state.json",
            $$"""
            {
              "schemaVersion": 1,
              "accounts": [
                { "id": "{{id}}", "displayName": "One", "steamLoginName": "one_login", "accentHex": "#66C0F4" },
                { "id": "{{id}}", "displayName": "Two", "steamLoginName": "two_login", "accentHex": "#66C0F4" }
              ],
              "settings": {}
            }
            """);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new StateStore(statePath).LoadAsync());

        Assert.AreEqual(
            1,
            Directory.EnumerateFiles(temporary.Path, "state.corrupt.*.json").Count());
    }

    [TestMethod]
    public async Task Save_RejectsAnUnsafeNumberOfAccountProfiles()
    {
        using var temporary = new TemporaryDirectory();
        var state = new PersistedState
        {
            Accounts = Enumerable.Range(
                    0,
                    AccountValidator.MaximumAccountProfiles + 1)
                .Select(index => new AccountProfile
                {
                    DisplayName = $"Profile {index}",
                    SteamLoginName = $"profile_{index}"
                })
                .ToList()
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new StateStore(System.IO.Path.Combine(temporary.Path, "state.json"))
                .SaveAsync(state));
    }

    [TestMethod]
    public async Task Load_RejectsOversizedStateFiles()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        using (var stream = File.Create(statePath))
        {
            stream.SetLength(StateStore.MaximumStateFileBytes + 1L);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new StateStore(statePath).LoadAsync());
    }

    [TestMethod]
    public async Task Load_RetainsOnlyThreeCorruptRecoveryCopies()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile("state.json", "{broken");
        var store = new StateStore(statePath);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.LoadAsync());
        }

        Assert.AreEqual(
            3,
            Directory.EnumerateFiles(temporary.Path, "state.corrupt.*.json").Count());
    }

    [TestMethod]
    public async Task Load_ClearsASelectedIdentifierThatNoLongerExists()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile(
            "state.json",
            $$"""
            {
              "schemaVersion": 1,
              "lastSelectedAccountId": "{{Guid.NewGuid()}}",
              "accounts": [],
              "settings": {}
            }
            """);

        var state = await new StateStore(statePath).LoadAsync();

        Assert.IsNull(state.LastSelectedAccountId);
    }

    [TestMethod]
    public async Task Load_ClearsAPlayIdentifierThatNoLongerExists()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile(
            "state.json",
            $$"""
            {
              "schemaVersion": 3,
              "lastPlayAccountId": "{{Guid.NewGuid()}}",
              "accounts": [],
              "settings": {}
            }
            """);

        var state = await new StateStore(statePath).LoadAsync();

        Assert.IsNull(state.LastPlayAccountId);
    }

    [TestMethod]
    public async Task Load_MigratesLegacyStateToThePreviousSelectedPlayAccount()
    {
        using var temporary = new TemporaryDirectory();
        var accountId = Guid.NewGuid();
        var statePath = temporary.CreateFile(
            "state.json",
            $$"""
            {
              "schemaVersion": 2,
              "lastSelectedAccountId": "{{accountId}}",
              "accounts": [
                {
                  "id": "{{accountId}}",
                  "displayName": "Legacy",
                  "steamLoginName": "legacy_login",
                  "accentHex": "#66C0F4"
                }
              ],
              "settings": {}
            }
            """);

        var state = await new StateStore(statePath).LoadAsync();

        Assert.AreEqual(PersistedState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.AreEqual(accountId, state.LastPlayAccountId);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsPendingProfileDeletionTombstone()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var id = Guid.NewGuid();
        var state = new PersistedState
        {
            Accounts =
            [
                new AccountProfile
                {
                    Id = id,
                    DisplayName = "Disposable",
                    SteamLoginName = "disposable"
                }
            ],
            PendingBrowserProfileDeletionIds = [id]
        };

        var store = new StateStore(statePath);
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.AreEqual(PersistedState.CurrentSchemaVersion, loaded.SchemaVersion);
        CollectionAssert.AreEqual(
            new[] { id },
            loaded.PendingBrowserProfileDeletionIds);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsDetachedWindowsNotificationCleanup()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = System.IO.Path.Combine(temporary.Path, "state.json");
        var detachedAccountId = Guid.NewGuid();
        var state = new PersistedState
        {
            PendingWindowsNotificationAccountCleanupIds = [detachedAccountId]
        };

        var store = new StateStore(statePath);
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.IsFalse(loaded.PendingWindowsNotificationHistoryClear);
        CollectionAssert.AreEqual(
            new[] { detachedAccountId },
            loaded.PendingWindowsNotificationAccountCleanupIds);
    }

    [TestMethod]
    public async Task Load_VersionThreeCreatesDurableNotificationCleanupIntent()
    {
        using var temporary = new TemporaryDirectory();
        var accountId = Guid.NewGuid();
        var statePath = temporary.CreateFile(
            "state.json",
            $$"""
            {
              "schemaVersion": 3,
              "accounts": [
                {
                  "id": "{{accountId}}",
                  "displayName": "Pending",
                  "steamLoginName": "pending_login",
                  "accentHex": "#66C0F4"
                }
              ],
              "settings": {
                "enableWindowsNotifications": true,
                "showNotificationPreviews": true
              },
              "pendingBrowserProfileDeletionIds": ["{{accountId}}"]
            }
            """);

        var loaded = await new StateStore(statePath).LoadAsync();

        Assert.AreEqual(PersistedState.CurrentSchemaVersion, loaded.SchemaVersion);
        CollectionAssert.AreEqual(
            new[] { accountId },
            loaded.PendingWindowsNotificationAccountCleanupIds);
    }

    [TestMethod]
    public async Task Save_RejectsUnboundedWindowsNotificationCleanupRequests()
    {
        using var temporary = new TemporaryDirectory();
        var state = new PersistedState
        {
            PendingWindowsNotificationAccountCleanupIds = Enumerable.Range(
                    0,
                    AccountValidator.MaximumAccountProfiles + 1)
                .Select(_ => Guid.NewGuid())
                .ToList()
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new StateStore(
                    System.IO.Path.Combine(temporary.Path, "state.json"))
                .SaveAsync(state));
    }

    [TestMethod]
    public async Task Load_RejectsCleanupTombstoneWithoutMatchingAccount()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile(
            "state.json",
            $$"""
            {
              "schemaVersion": 2,
              "accounts": [],
              "settings": {},
              "pendingBrowserProfileDeletionIds": ["{{Guid.NewGuid()}}"]
            }
            """);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new StateStore(statePath).LoadAsync());
    }

    [TestMethod]
    public async Task Load_RejectsNullAccountEntries()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile(
            "state.json",
            """
            {
              "schemaVersion": 2,
              "accounts": [null],
              "settings": {}
            }
            """);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new StateStore(statePath).LoadAsync());
    }

    [TestMethod]
    public async Task Load_RejectsCaseInsensitiveDuplicateJsonProperties()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile(
            "state.json",
            """
            {
              "schemaVersion": 2,
              "SchemaVersion": 1,
              "accounts": [],
              "settings": {}
            }
            """);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new StateStore(statePath).LoadAsync());
    }

    [TestMethod]
    public async Task Load_RejectsUnknownJsonProperties()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.CreateFile(
            "state.json",
            """
            {
              "schemaVersion": 2,
              "accounts": [],
              "settings": {},
              "credential": "must-not-be-accepted"
            }
            """);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => new StateStore(statePath).LoadAsync());
    }
}
