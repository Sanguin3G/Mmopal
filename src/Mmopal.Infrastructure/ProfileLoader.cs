using System.Text.Json;

namespace Mmopal.Infrastructure;

public sealed class ProfileLoader
{
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public RunProfile Load(string profileName, string repositoryRoot)
    {
        var safeName = Path.GetFileNameWithoutExtension(profileName);
        var path = Path.Combine(repositoryRoot, "config", "profiles", safeName + ".json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Profile '{profileName}' was not found.", path);
        }

        var profile = JsonSerializer.Deserialize<RunProfile>(File.ReadAllText(path), _options);
        return profile ?? throw new InvalidDataException($"Profile '{profileName}' is empty.");
    }
}
