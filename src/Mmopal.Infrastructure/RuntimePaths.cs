namespace Mmopal.Infrastructure;

public sealed record RuntimePaths(
    string Root,
    string Logs,
    string Database,
    string Checkpoints,
    string Profiles,
    string Screenshots)
{
    public static RuntimePaths Create(string? root = null)
    {
        var resolvedRoot = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mmopal")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));

        return new RuntimePaths(
            resolvedRoot,
            Path.Combine(resolvedRoot, "logs"),
            Path.Combine(resolvedRoot, "mmopal.db"),
            Path.Combine(resolvedRoot, "checkpoints"),
            Path.Combine(resolvedRoot, "profiles"),
            Path.Combine(resolvedRoot, "screenshots"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Checkpoints);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Screenshots);
    }
}
