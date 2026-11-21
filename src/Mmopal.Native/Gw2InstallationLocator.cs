using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mmopal.Native;

public enum Gw2InstallKind
{
    Standalone,
    Steam,
    Epic,
    Unknown
}

public sealed record Gw2InstallCandidate(
    string ExecutablePath,
    Gw2InstallKind Kind,
    string Source,
    string? SuggestedLaunchTarget = null);

public static partial class Gw2InstallationLocator
{
    private const string SteamAppManifest = "appmanifest_1284210.acf";
    private const string SteamLaunchUri = "steam://rungameid/1284210";

    private static readonly string[] RelativeInstallPaths =
    [
        Path.Combine("Program Files", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("Program Files (x86)", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("Program Files (x86)", "Steam", "steamapps", "common", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("SteamLibrary", "steamapps", "common", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("Games", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("Steam", "steamapps", "common", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("Games", "Steam", "steamapps", "common", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("Games", "SteamLibrary", "steamapps", "common", "Guild Wars 2", "Gw2-64.exe"),
        Path.Combine("MMO", "Guild Wars 2", "Gw2-64.exe")
    ];

    public static string? DetectDefaultExecutablePath() =>
        DiscoverInstallations().FirstOrDefault()?.ExecutablePath;

    public static IReadOnlyList<Gw2InstallCandidate> DiscoverInstallations()
    {
        var results = new List<Gw2InstallCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddFile(string? candidatePath, Gw2InstallKind kind, string source, string? launchTarget = null)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return;
            }

            var cleaned = CleanExecutablePath(candidatePath);
            if (!IsValidExecutableFilePath(cleaned))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(cleaned!);
                if (File.Exists(full) && seen.Add(full))
                {
                    var inferredKind = kind == Gw2InstallKind.Unknown ? KindFromPath(full, Gw2InstallKind.Unknown) : kind;
                    results.Add(new Gw2InstallCandidate(full, inferredKind, source, launchTarget));
                }
            }
            catch
            {
                // Ignore malformed paths from external sources.
            }
        }

        // 1. Running Gw2-64 process
        try
        {
            var processes = Process.GetProcessesByName("Gw2-64");
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var fileName = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(fileName))
                        {
                            TryAddFile(fileName, KindFromPath(fileName, Gw2InstallKind.Standalone), "Running process");
                        }
                    }
                    catch
                    {
                        // Access can be denied on elevated processes.
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Ignore process enumeration issues.
        }

        // 2. Windows Registry (ArenaNet keys + Uninstall entries)
        DiscoverFromRegistry(TryAddFile);

        // 3. Steam libraryfolders.vdf
        DiscoverFromSteam(TryAddFile, (uri, source) =>
        {
            if (seen.Add(uri))
            {
                results.Add(new Gw2InstallCandidate(uri, Gw2InstallKind.Steam, source, uri));
            }
        });

        // 4. Epic Games Launcher manifests
        DiscoverFromEpicManifests(TryAddFile);

        // 5. Common fixed-drive paths
        foreach (var path in FindInstalledPaths())
        {
            TryAddFile(path, KindFromPath(path, Gw2InstallKind.Standalone), "Common drive scan");
        }

        return results;
    }

    /// <summary>Steam when the path is inside a Steam library ("steamapps"), otherwise <paramref name="otherwise"/>.</summary>
    private static Gw2InstallKind KindFromPath(string path, Gw2InstallKind otherwise) =>
        path.Contains("steamapps", StringComparison.OrdinalIgnoreCase) ? Gw2InstallKind.Steam : otherwise;

    public static IReadOnlyList<string> FindInstalledPaths(
        IEnumerable<string>? driveRoots = null,
        Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var roots = driveRoots ?? EnumerateFixedDriveRoots();
        var discovered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var relative in RelativeInstallPaths)
            {
                var candidate = Path.Combine(root, relative);
                if (seen.Add(candidate) && exists(candidate))
                {
                    discovered.Add(candidate);
                }
            }
        }

        return discovered;
    }

    internal static IReadOnlyList<string> ParseSteamLibraryFolders(string vdfContent)
    {
        if (string.IsNullOrWhiteSpace(vdfContent))
        {
            return Array.Empty<string>();
        }

        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFolder(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var unescaped = raw.Replace(@"\\", @"\").Trim();
            if (unescaped.Length > 0 && seen.Add(unescaped))
            {
                folders.Add(unescaped);
            }
        }

        foreach (Match match in SteamPathPropertyRegex().Matches(vdfContent))
        {
            AddFolder(match.Groups["path"].Value);
        }

        foreach (Match match in LegacySteamFolderRegex().Matches(vdfContent))
        {
            AddFolder(match.Groups["path"].Value);
        }

        return folders;
    }

    internal static string? ParseEpicManifestInstallLocation(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("DisplayName", out var displayNameProp) ||
                displayNameProp.GetString() is not { } displayName ||
                !displayName.Contains("Guild Wars 2", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!root.TryGetProperty("InstallLocation", out var installProp) ||
                installProp.GetString() is not { } installLocation ||
                string.IsNullOrWhiteSpace(installLocation))
            {
                return null;
            }

            var launchExe = root.TryGetProperty("LaunchExecutable", out var exeProp) &&
                            !string.IsNullOrWhiteSpace(exeProp.GetString())
                ? exeProp.GetString()!
                : "Gw2-64.exe";
            return Path.Combine(installLocation.Trim(), launchExe.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void DiscoverFromRegistry(Action<string?, Gw2InstallKind, string, string?> addFile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string[] arenaNetPaths =
        [
            @"SOFTWARE\ArenaNet\Guild Wars 2",
            @"SOFTWARE\WOW6432Node\ArenaNet\Guild Wars 2"
        ];
        foreach (var regPath in arenaNetPaths)
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey(regPath);
                    var raw = key?.GetValue("Path")?.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var exe = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? raw
                            : Path.Combine(raw, "Gw2-64.exe");
                        addFile(exe, Gw2InstallKind.Standalone, "Windows Registry (ArenaNet)", null);
                    }
                }
                catch
                {
                    // Ignore inaccessible registry hives.
                }
            }
        }

        string[] uninstallRoots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var uninstallPath in uninstallRoots)
            {
                try
                {
                    using var baseKey = root.OpenSubKey(uninstallPath);
                    if (baseKey is null) continue;
                    foreach (var subName in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = baseKey.OpenSubKey(subName);
                            var displayName = subKey?.GetValue("DisplayName")?.ToString();
                            if (string.IsNullOrWhiteSpace(displayName) ||
                                !displayName.Contains("Guild Wars 2", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var installLoc = subKey?.GetValue("InstallLocation")?.ToString();
                            if (!string.IsNullOrWhiteSpace(installLoc))
                            {
                                addFile(Path.Combine(installLoc, "Gw2-64.exe"), Gw2InstallKind.Unknown, "Windows Uninstall Registry", null);
                            }

                            var displayIcon = subKey?.GetValue("DisplayIcon")?.ToString();
                            if (!string.IsNullOrWhiteSpace(displayIcon))
                            {
                                var cleanedIcon = CleanExecutablePath(displayIcon);
                                if (IsValidExecutableFilePath(cleanedIcon))
                                {
                                    addFile(cleanedIcon, Gw2InstallKind.Unknown, "Windows Uninstall Registry", null);
                                }
                            }
                        }
                        catch
                        {
                            // Ignore individual unreadable subkeys.
                        }
                    }
                }
                catch
                {
                    // Ignore unreadable base keys.
                }
            }
        }
    }

    private static void DiscoverFromSteam(
        Action<string?, Gw2InstallKind, string, string?> addFile,
        Action<string, string> addUri)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var steamRoots = new List<string>();
        try
        {
            using var cuKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steamPath = cuKey?.GetValue("SteamPath")?.ToString();
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                steamRoots.Add(steamPath.Replace('/', '\\'));
            }
        }
        catch { }

        try
        {
            using var lmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var installPath = lmKey?.GetValue("InstallPath")?.ToString();
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                steamRoots.Add(installPath);
            }
        }
        catch { }

        var libraryFolders = new List<string>();
        var seenLibs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddLibrary(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            var normalized = folder.Trim().TrimEnd('\\', '/');
            if (normalized.Length > 0 && seenLibs.Add(normalized))
            {
                libraryFolders.Add(normalized);
            }
        }

        foreach (var steamRoot in steamRoots)
        {
            AddLibrary(steamRoot);
            var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    var content = File.ReadAllText(vdfPath);
                    foreach (var lib in ParseSteamLibraryFolders(content))
                    {
                        AddLibrary(lib);
                    }
                }
                catch { }
            }
        }

        var steamManifestFound = false;
        foreach (var lib in libraryFolders)
        {
            if (File.Exists(Path.Combine(lib, "steamapps", SteamAppManifest)))
            {
                steamManifestFound = true;
            }

            var exePath = Path.Combine(lib, "steamapps", "common", "Guild Wars 2", "Gw2-64.exe");
            addFile(exePath, Gw2InstallKind.Steam, "Steam Library", SteamLaunchUri);
        }

        if (steamManifestFound)
        {
            addUri(SteamLaunchUri, "Steam App Manifest");
        }
    }

    private static void DiscoverFromEpicManifests(Action<string?, Gw2InstallKind, string, string?> addFile)
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData))
            {
                return;
            }

            var manifestDir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestDir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(manifestDir, "*.item"))
            {
                try
                {
                    var candidate = ParseEpicManifestInstallLocation(File.ReadAllText(file));
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        addFile(candidate, Gw2InstallKind.Epic, "Epic Games Manifest", null);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    internal static bool IsValidExecutableFilePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string? CleanExecutablePath(string raw)
    {
        var trimmed = raw.Trim().Trim('"');
        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex > 0 && trimmed[..commaIndex].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..commaIndex];
        }

        return trimmed.Trim().Trim('"');
    }

    private static IEnumerable<string> EnumerateFixedDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathPropertyRegex();

    [GeneratedRegex("\"\\d+\"\\s+\"(?<path>[A-Za-z]:[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LegacySteamFolderRegex();
}
