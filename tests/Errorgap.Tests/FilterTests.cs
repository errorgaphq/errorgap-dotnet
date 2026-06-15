using System.Collections.Generic;
using Errorgap;
using Xunit;

namespace Errorgap.Tests;

public class FilterTests
{
    private static readonly string[] Defaults = { "password", "token", "secret", "api_key", "authorization", "cookie" };

    [Fact]
    public void MasksFilteredKeys()
    {
        var input = new Dictionary<string, object?>
        {
            ["username"] = "alice",
            ["password"] = "hunter2",
            ["access_token"] = "x",
        };
        var output = Filter.Params(input, Defaults);
        Assert.Equal("alice", output["username"]);
        Assert.Equal("[FILTERED]", output["password"]);
        Assert.Equal("[FILTERED]", output["access_token"]);
    }

    [Fact]
    public void RecursesIntoNestedDictionaries()
    {
        var input = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["name"] = "alice",
                ["api_key"] = "x",
            },
        };
        var output = Filter.Params(input, Defaults);
        var nested = (IDictionary<string, object?>)output["user"]!;
        Assert.Equal("alice", nested["name"]);
        Assert.Equal("[FILTERED]", nested["api_key"]);
    }

    [Fact]
    public void CaseInsensitiveMatch()
    {
        var input = new Dictionary<string, object?> { ["Authorization"] = "Bearer xyz" };
        var output = Filter.Params(input, Defaults);
        Assert.Equal("[FILTERED]", output["Authorization"]);
    }
}
