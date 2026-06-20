using CiFail.Core;
using FluentAssertions;
using Xunit;

namespace CiFail.Core.Tests;

public class CiFailPathsTests
{
    [Fact]
    public void Home_honors_CIFAIL_HOME_env_var()
    {
        var original = Environment.GetEnvironmentVariable(CiFailPaths.HomeEnvVar);
        try
        {
            var custom = Path.Combine(Path.GetTempPath(), "cifail-home-test");
            Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, custom);

            CiFailPaths.Home.Should().Be(custom);
            CiFailPaths.HistoryDbPath.Should().Be(Path.Combine(custom, "history.db"));
            CiFailPaths.UserRulesDir.Should().Be(Path.Combine(custom, "rules"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, original);
        }
    }

    [Fact]
    public void Home_falls_back_to_dot_cifail_under_user_profile()
    {
        var original = Environment.GetEnvironmentVariable(CiFailPaths.HomeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, null);
            CiFailPaths.Home.Should().EndWith(".cifail");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CiFailPaths.HomeEnvVar, original);
        }
    }
}
