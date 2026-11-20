using System.Text;
using Mmopal.Contracts;
using Mmopal.Desktop;
using Mmopal.Gw2;
using Mmopal.Native;

namespace Mmopal.Domain.Tests;

public sealed class OperatorConveniencesTests
{
    [Fact]
    public void Windows_dpapi_secret_store_encrypts_on_disk_and_round_trips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-dpapi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(tempDir, "gw2-api-key.dpapi");
            const string secret = "12345678-ABCD-EF01-2345-6789ABCDEF01";

            WindowsSecretStore.SaveText(path, secret);
            Assert.True(WindowsSecretStore.HasSecret(path));

            // Ciphertext on disk must never contain the plaintext key.
            var rawBytes = File.ReadAllBytes(path);
            var rawText = Encoding.UTF8.GetString(rawBytes);
            Assert.DoesNotContain("12345678-ABCD", rawText, StringComparison.Ordinal);

            var decrypted = WindowsSecretStore.LoadText(path);
            Assert.Equal(secret, decrypted);

            WindowsSecretStore.Delete(path);
            Assert.False(WindowsSecretStore.HasSecret(path));
            Assert.Null(WindowsSecretStore.LoadText(path));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Hotkey_gesture_parser_handles_valid_and_invalid_shortcuts()
    {
        Assert.True(HotkeyGesture.TryParse("ctrl+shift+p", out var pause, out var err1));
        Assert.Null(err1);
        Assert.NotNull(pause);
        Assert.Equal("Ctrl+Shift+P", pause.CanonicalText);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, pause.Modifiers);
        Assert.Equal((uint)'P', pause.VirtualKey);

        Assert.True(HotkeyGesture.TryParse("Alt+F10", out var f10, out _));
        Assert.NotNull(f10);
        Assert.Equal("Alt+F10", f10.CanonicalText);
        Assert.Equal(0x79u, f10.VirtualKey);

        Assert.True(HotkeyGesture.TryParse("", out var blank, out _));
        Assert.Null(blank);

        Assert.False(HotkeyGesture.TryParse("P", out _, out var noModError));
        Assert.Contains("modifier", noModError, StringComparison.OrdinalIgnoreCase);

        Assert.False(HotkeyGesture.TryParse("Ctrl+Shift", out _, out var noKeyError));
        Assert.Contains("key", noKeyError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Operator_lists_and_settings_persist_deduplicate_and_export_csv()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-oplists-" + Guid.NewGuid().ToString("N"));
        try
        {
            var listPath = Path.Combine(tempDir, "custom-shoppinglist.json");
            var csvListPath = Path.Combine(tempDir, "custom-shoppinglist.csv");
            var blacklistPath = Path.Combine(tempDir, "blacklist.json");
            var csvBlacklistPath = Path.Combine(tempDir, "blacklist.csv");
            var settingsPath = Path.Combine(tempDir, "operator-settings.json");

            OperatorListsStore.SaveShoppingList(listPath,
            [
                new ItemCandidate("Glob of Ectoplasm", "Rare", 10, BuyPriceCopper: 2500, SellPriceCopper: 3400),
                new ItemCandidate("glob of ectoplasm", "Rare", 20, BuyPriceCopper: 2500, SellPriceCopper: 3400),
                new ItemCandidate("Mystic Coin", "Rare", 5, BuyPriceCopper: 12000, SellPriceCopper: 16000)
            ]);

            var loadedList = OperatorListsStore.LoadShoppingList(listPath);
            Assert.Equal(2, loadedList.Count);
            Assert.Equal(20, loadedList.Single(i => i.ItemName.Equals("glob of ectoplasm", StringComparison.OrdinalIgnoreCase)).Quantity);

            OperatorListsStore.ExportShoppingListCsv(csvListPath, loadedList);
            var reimported = await CandidatesLoader.LoadCandidatesAsync(csvListPath);
            Assert.Equal(2, reimported.Count);

            OperatorListsStore.SaveBlacklist(blacklistPath, ["Bad Item", "bad item", "Another Item"]);
            var loadedBlacklist = OperatorListsStore.LoadBlacklist(blacklistPath);
            Assert.Equal(2, loadedBlacklist.Count);

            OperatorListsStore.ExportBlacklistCsv(csvBlacklistPath, loadedBlacklist);
            var reimportedBlacklist = await CandidatesLoader.LoadBlacklistAsync(csvBlacklistPath);
            Assert.Equal(2, reimportedBlacklist.Count);

            var opSettings = new OperatorSettings(
                ThemeMode: "Dark",
                PauseHotkey: "Ctrl+Shift+P",
                StopHotkey: "Ctrl+Shift+S",
                EmergencyStopHotkey: "Ctrl+Shift+X",
                OverbidBufferMinCopper: 2,
                OverbidBufferMaxCopper: 6);
            OperatorSettingsFile.Save(settingsPath, opSettings);
            Assert.Equal(opSettings, OperatorSettingsFile.LoadOrDefault(settingsPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Api_key_vault_persists_multiple_accounts_switches_active_and_migrates_legacy_key()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mmopal-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var legacyPath = Path.Combine(tempDir, "gw2-api-key.dpapi");
            var vaultPath = Path.Combine(tempDir, "gw2-api-keys.dpapi");

            WindowsSecretStore.SaveText(legacyPath, "LEGACY-KEY-1234567890");
            var migrated = ApiKeyVaultStore.Load(vaultPath, legacyPath);
            var migratedEntry = Assert.Single(migrated.Entries);
            Assert.Equal("LEGACY-KEY-1234567890", migratedEntry.ApiKey);
            Assert.Equal("LEGACY-KEY-1234567890", ApiKeyVaultStore.LoadActiveApiKey(vaultPath, legacyPath));

            var main = new Gw2ApiKeyEntry("id-main", "MAIN-API-KEY-111122223333", "SanguinE.9435", "Main", "VALID", ["account", "wallet"], 2_500_000, DateTimeOffset.UtcNow);
            var alt = new Gw2ApiKeyEntry("id-alt", "ALT-API-KEY-444455556666", "Hu Tao.1937", "Alt", "VALID", ["account", "tradingpost"], 900_000, DateTimeOffset.UtcNow);
            ApiKeyVaultStore.Save(vaultPath, new Gw2ApiKeyVaultData("id-alt", [main, alt]));

            var loaded = ApiKeyVaultStore.Load(vaultPath, legacyPath);
            Assert.Equal(2, loaded.Entries.Count);
            Assert.Equal("id-alt", loaded.ActiveKeyId);
            Assert.Equal("ALT-API-KEY-444455556666", ApiKeyVaultStore.LoadActiveApiKey(vaultPath, legacyPath));
            Assert.Equal("Hu Tao.1937 (Alt)", loaded.ActiveEntry?.DisplayLabel);

            ApiKeyVaultStore.Save(vaultPath, loaded with { ActiveKeyId = "id-main" });
            Assert.Equal("MAIN-API-KEY-111122223333", ApiKeyVaultStore.LoadActiveApiKey(vaultPath, legacyPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
