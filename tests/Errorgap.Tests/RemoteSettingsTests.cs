using System;
using System.Threading.Tasks;
using Errorgap;
using Xunit;

namespace Errorgap.Tests;

public class RemoteSettingsTests
{
    [Fact]
    public async Task ASwitchedOffKindIsNotSentAndEverythingStaysOnWhenUnreadable()
    {
        using var ing = new FakeIngestor();
        ing.Settings = "{\"errors_enabled\":false,\"apm_enabled\":true,\"poll_interval_seconds\":600}";
        var config = new ErrorgapConfiguration
        {
            Endpoint = ing.Endpoint, ProjectSlug = "demo", ApiKey = "key-demo", Async = false, RemoteSettings = true,
        };
        await using (var client = new ErrorgapClient(config))
        {
            Assert.True(await client.Remote.RefreshAsync());
            Assert.False(client.Remote.Enabled(RemoteKind.Errors));
            Assert.True(client.Remote.Enabled(RemoteKind.Apm));
            Assert.Equal("errors are switched off for this project", client.Notify(new Exception("x")).Body);
            Assert.Empty(ing.Requests);

            // The switch is turned back on.
            ing.Settings = "{\"errors_enabled\":true}";
            Assert.True(await client.Remote.RefreshAsync());
            Assert.True(client.Notify(new Exception("y")).Success);
            Assert.Single(ing.Requests);

            // Unreadable settings keep what was known.
            ing.Settings = null;
            Assert.False(await client.Remote.RefreshAsync());
            Assert.True(client.Remote.Enabled(RemoteKind.Errors));
        }

        var off = new ErrorgapConfiguration { Endpoint = "http://127.0.0.1:1", ProjectSlug = "demo", RemoteSettings = false };
        await using var ignoring = new ErrorgapClient(off);
        Assert.True(ignoring.Remote.Enabled(RemoteKind.Errors));
    }
}
