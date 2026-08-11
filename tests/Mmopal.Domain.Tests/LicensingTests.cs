using Mmopal.Contracts;
using Mmopal.Licensing;

namespace Mmopal.Domain.Tests;

public sealed class LicensingTests
{
    [Fact]
    public async Task Disabled_provider_returns_unrestricted_features()
    {
        var snapshot = await new DisabledLicenseProvider().GetSnapshotAsync();

        Assert.False(snapshot.IsEnabled);
        Assert.True(snapshot.IsValid);
        Assert.All(Enum.GetValues<LicenseFeature>(), feature => Assert.True(snapshot.Has(feature)));
    }
}
