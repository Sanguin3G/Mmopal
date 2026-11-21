using System.Text.Json;
using Mmopal.Contracts;

namespace Mmopal.Native;

/// <summary>
/// Manages multiple Guild Wars 2 API keys encrypted with Windows DPAPI (CurrentUser),
/// with seamless migration from the single-key legacy gw2-api-key.dpapi file.
/// </summary>
public static class ApiKeyVaultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static Gw2ApiKeyVaultData Load(string vaultPath, string? legacySingleKeyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        var rawJson = WindowsSecretStore.LoadText(vaultPath);
        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Gw2ApiKeyVaultData>(rawJson, JsonOptions);
                if (parsed is not null)
                {
                    return new Gw2ApiKeyVaultData(parsed.ActiveKeyId, EntriesWithKey(parsed.Entries));
                }
            }
            catch (JsonException)
            {
                // Fall back to legacy or empty if corrupted.
            }
        }

        if (!string.IsNullOrWhiteSpace(legacySingleKeyPath))
        {
            var legacyKey = WindowsSecretStore.LoadText(legacySingleKeyPath)?.Trim();
            if (!string.IsNullOrWhiteSpace(legacyKey))
            {
                var entry = new Gw2ApiKeyEntry(
                    Id: Guid.NewGuid().ToString("N"),
                    ApiKey: legacyKey,
                    AccountName: "Default Account",
                    CustomName: string.Empty,
                    Status: "UNVERIFIED",
                    Permissions: Array.Empty<string>(),
                    LastWalletCopper: null,
                    LastVerifiedAt: null);
                return new Gw2ApiKeyVaultData(entry.Id, [entry]);
            }
        }

        return new Gw2ApiKeyVaultData(null, Array.Empty<Gw2ApiKeyEntry>());
    }

    public static void Save(string vaultPath, Gw2ApiKeyVaultData data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        ArgumentNullException.ThrowIfNull(data);
        var cleanEntries = EntriesWithKey(data.Entries);
        if (cleanEntries.Length == 0)
        {
            WindowsSecretStore.Delete(vaultPath);
            return;
        }

        var activeId = cleanEntries.Any(e => string.Equals(e.Id, data.ActiveKeyId, StringComparison.OrdinalIgnoreCase))
            ? data.ActiveKeyId
            : cleanEntries[0].Id;
        var cleanVault = new Gw2ApiKeyVaultData(activeId, cleanEntries);
        var json = JsonSerializer.Serialize(cleanVault, JsonOptions);
        WindowsSecretStore.SaveText(vaultPath, json);
    }

    public static string? LoadActiveApiKey(string vaultPath, string? legacySingleKeyPath = null)
    {
        var key = Load(vaultPath, legacySingleKeyPath).ActiveEntry?.ApiKey?.Trim();
        return string.IsNullOrWhiteSpace(key)
            ? Environment.GetEnvironmentVariable("MMOPAL_Native__Gw2ApiKey")
            : key;
    }

    private static Gw2ApiKeyEntry[] EntriesWithKey(IEnumerable<Gw2ApiKeyEntry>? entries) =>
        (entries ?? []).Where(e => !string.IsNullOrWhiteSpace(e.ApiKey)).ToArray();
}
