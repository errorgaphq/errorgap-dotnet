using Errorgap;
using Xunit;

namespace Errorgap.Tests;

public class ConfigurationTests
{
    [Fact]
    public void DefaultsWhenNothingProvided()
    {
        var cfg = new ErrorgapConfiguration();
        Assert.NotNull(cfg.Endpoint);
        Assert.True(cfg.Async);
        Assert.Contains("password", cfg.FilterKeys);
    }

    [Fact]
    public void ValidateThrowsWhenProjectSlugMissing()
    {
        var cfg = new ErrorgapConfiguration { ProjectSlug = null };
        var ex = Assert.Throws<System.InvalidOperationException>(() => cfg.Validate());
        Assert.Contains("ProjectSlug", ex.Message);
    }

    [Fact]
    public void ValidatePassesWhenProjectSlugSet()
    {
        var cfg = new ErrorgapConfiguration { ProjectSlug = "demo" };
        cfg.Validate();
    }
}
