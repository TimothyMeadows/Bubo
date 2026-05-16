using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Abstractions.Tests;

public sealed class AgentDefaultsTests
{
    [Fact]
    public void AgentLimitsMatchApprovedFoundationDefaults()
    {
        var limits = new AgentLimits();

        Assert.Equal(8, limits.MaxIterations);
        Assert.Equal(80, limits.MaxToolCalls);
        Assert.Equal(600, limits.MaxCommandSeconds);
        Assert.Equal(262_144, limits.MaxPatchBytes);
        Assert.Equal(25, limits.MaxFilesChanged);
        Assert.Equal(8_192, limits.MaxTokensPerStep);
    }

    [Fact]
    public void NetworkPolicyIncludesApprovedModes()
    {
        var values = Enum.GetNames<NetworkPolicy>();

        Assert.Contains(nameof(NetworkPolicy.None), values);
        Assert.Contains(nameof(NetworkPolicy.PackageRestore), values);
        Assert.Contains(nameof(NetworkPolicy.Research), values);
        Assert.Contains(nameof(NetworkPolicy.Full), values);
    }

    [Fact]
    public void AgentRunConfigDefaultsToLocalPlannerAndCoderProfiles()
    {
        var config = new AgentRunConfig();

        Assert.Equal(AgentMode.Local, config.Mode);
        Assert.Equal("planner", config.Planner.Role);
        Assert.Equal(0.2, config.Planner.Temperature);
        Assert.Equal("coder", config.Coder.Role);
        Assert.Equal(0.1, config.Coder.Temperature);
    }
}
