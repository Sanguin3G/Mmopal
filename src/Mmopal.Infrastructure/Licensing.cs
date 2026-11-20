using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mmopal.Contracts;

namespace Mmopal.Licensing;

public sealed class LicensingOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Disabled";
    public string? LicensePath { get; set; }
    public string? Endpoint { get; set; }
    public int RefreshMinutes { get; set; } = 60;
    public int GracePeriodHours { get; set; } = 72;
    public string FailurePolicy { get; set; } = "UseCachedStatus";
    public string? PublicKeyPem { get; set; }
}

public interface ILicenseProvider
{
    string Name { get; }
    Task<LicenseSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface ILicenseProviderFactory
{
    ILicenseProvider Create(LicensingOptions options);
}

public sealed class DisabledLicenseProvider : ILicenseProvider
{
    public string Name => "Disabled";

    public Task<LicenseSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var features = Enum.GetValues<LicenseFeature>().ToHashSet();
        return Task.FromResult(new LicenseSnapshot(
            IsEnabled: false,
            IsValid: true,
            Features: features,
            Provider: Name,
            LicenseId: null,
            ExpiresAt: null,
            CheckedAt: DateTimeOffset.UtcNow,
            Message: "Licensing is disabled; all application features are available."));
    }
}

public sealed class LocalSignedFileProvider : ILicenseProvider
{
    private readonly LicensingOptions _options;

    public LocalSignedFileProvider(LicensingOptions options) => _options = options;

    public string Name => "LocalFile";

    public async Task<LicenseSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(_options.LicensePath) || !File.Exists(_options.LicensePath))
        {
            return Invalid(checkedAt, "No local license file was configured.");
        }

        try
        {
            await using var stream = File.OpenRead(Environment.ExpandEnvironmentVariables(_options.LicensePath));
            var document = await JsonSerializer.DeserializeAsync<LicenseDocument>(stream, cancellationToken: cancellationToken);
            if (document is null)
            {
                return Invalid(checkedAt, "The local license file is empty.");
            }

            var payload = document.CanonicalPayload();
            if (!Verify(payload, document.Signature, _options.PublicKeyPem))
            {
                return Invalid(checkedAt, "The local license signature is invalid.");
            }

            var expiresAt = document.ExpiresAt;
            if (expiresAt.HasValue && expiresAt.Value <= checkedAt)
            {
                return Invalid(checkedAt, "The local license has expired.", document.LicenseId, expiresAt);
            }

            return new LicenseSnapshot(
                IsEnabled: true,
                IsValid: true,
                Features: document.Features.ToHashSet(),
                Provider: Name,
                LicenseId: document.LicenseId,
                ExpiresAt: expiresAt,
                CheckedAt: checkedAt,
                Message: "Local license verified.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or CryptographicException)
        {
            return Invalid(checkedAt, $"Local license could not be read: {exception.Message}");
        }
    }

    private static bool Verify(string payload, string? signature, string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return rsa.VerifyData(
            Encoding.UTF8.GetBytes(payload),
            Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    private LicenseSnapshot Invalid(DateTimeOffset checkedAt, string message, string? id = null, DateTimeOffset? expiresAt = null) =>
        new(true, false, new HashSet<LicenseFeature>(), Name, id, expiresAt, checkedAt, message);
}

public sealed class RemoteHttpProvider : ILicenseProvider
{
    private readonly HttpClient _httpClient;
    private readonly LicensingOptions _options;

    public RemoteHttpProvider(HttpClient httpClient, LicensingOptions options)
    {
        _httpClient = httpClient;
        _options = options;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public string Name => "RemoteHttp";

    public async Task<LicenseSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return new LicenseSnapshot(true, false, new HashSet<LicenseFeature>(), Name, null, null, checkedAt, "No remote license endpoint was configured.");
        }

        try
        {
            var response = await _httpClient.GetFromJsonAsync<RemoteLicenseResponse>(
                Environment.ExpandEnvironmentVariables(_options.Endpoint),
                cancellationToken);

            if (response is null)
            {
                return new LicenseSnapshot(true, false, new HashSet<LicenseFeature>(), Name, null, null, checkedAt, "Remote license response was empty.");
            }

            return new LicenseSnapshot(
                true,
                response.Valid,
                response.Features.ToHashSet(),
                Name,
                response.LicenseId,
                response.ExpiresAt,
                checkedAt,
                response.Message);
        }
        catch (HttpRequestException exception)
        {
            return new LicenseSnapshot(true, false, new HashSet<LicenseFeature>(), Name, null, null, checkedAt, $"Remote license check failed: {exception.Message}");
        }
        catch (TaskCanceledException)
        {
            return new LicenseSnapshot(true, false, new HashSet<LicenseFeature>(), Name, null, null, checkedAt, "Remote license check timed out.");
        }
    }
}

public sealed class LicenseService
{
    private readonly ILicenseProvider _provider;
    private readonly LicensingOptions _options;
    private LicenseSnapshot? _cached;

    public LicenseService(ILicenseProvider provider, LicensingOptions options)
    {
        _provider = provider;
        _options = options;
    }

    public LicenseSnapshot? Current => _cached;

    public async Task<LicenseSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var result = await _provider.GetSnapshotAsync(cancellationToken);
        if (result.IsValid || _cached is null || _options.FailurePolicy.Equals("FailOpen", StringComparison.OrdinalIgnoreCase))
        {
            _cached = result;
            return result;
        }

        var age = DateTimeOffset.UtcNow - _cached.CheckedAt;
        if (_options.FailurePolicy.Equals("UseCachedStatus", StringComparison.OrdinalIgnoreCase) &&
            age <= TimeSpan.FromHours(Math.Max(0, _options.GracePeriodHours)))
        {
            return _cached with { Message = $"Using cached license status after provider failure: {result.Message}" };
        }

        _cached = result;
        return result;
    }
}

public sealed class LicenseProviderFactory : ILicenseProviderFactory
{
    private readonly Func<HttpClient>? _httpClientFactory;

    public LicenseProviderFactory(Func<HttpClient>? httpClientFactory = null) => _httpClientFactory = httpClientFactory;

    public ILicenseProvider Create(LicensingOptions options)
    {
        if (!options.Enabled || options.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new DisabledLicenseProvider();
        }

        if (options.Provider.Equals("LocalFile", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalSignedFileProvider(options);
        }

        if (options.Provider.Equals("RemoteHttp", StringComparison.OrdinalIgnoreCase))
        {
            var client = _httpClientFactory?.Invoke() ?? new HttpClient();
            return new RemoteHttpProvider(client, options);
        }

        throw new InvalidOperationException($"Unknown license provider '{options.Provider}'.");
    }
}

public sealed class LicenseDocument
{
    public string LicenseId { get; set; } = string.Empty;
    public List<LicenseFeature> Features { get; set; } = new();
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Signature { get; set; }

    public string CanonicalPayload() => JsonSerializer.Serialize(new
    {
        LicenseId,
        Features,
        ExpiresAt
    });
}

public sealed class RemoteLicenseResponse
{
    public bool Valid { get; set; }
    public string? LicenseId { get; set; }
    public List<LicenseFeature> Features { get; set; } = new();
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Message { get; set; }
}
