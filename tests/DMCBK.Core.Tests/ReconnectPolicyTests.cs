using DMCBK.Core;
using DMCBK.Core.Configuration;
using Umpk.Client;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The one piece of the reconnect state machine MCC still owns: the config -> <see cref="ReconnectPolicy"/> mapping (<see cref="ConfigReconnectPolicyProvider"/>, the default <c>Umpk.Client.IReconnectPolicyProvider</c>).
/// The retry classification (user-stop vs connection-lost vs cancellation, plus the policy's veto) and the kick/transport-fault distinction are UMPK's own contract now, pinned in <c>Umpk.Client.Tests.ClientStatusTests</c>; <see cref="ReconnectPolicy.DelayFor"/>'s backoff/cap math is not pinned there, so it stays pinned here.
/// </summary>
public sealed class ReconnectPolicyTests
{
    [Fact]
    public void ConfigProvider_ZeroAttempts_DisablesAutoReconnect()
    {
        var provider = new ConfigReconnectPolicyProvider(new ReconnectConfig { MaxAttempts = 0 });
        Assert.Null(provider.GetReconnectPolicy());
    }

    [Fact]
    public void ConfigProvider_MapsFieldsOntoPolicy()
    {
        var config = new ReconnectConfig
        {
            MaxAttempts = 3,
            DelaySeconds = 5,
            BackoffFactor = 1.0,
            MaxDelaySeconds = 120,
        };

        ReconnectPolicy? policy = new ConfigReconnectPolicyProvider(config).GetReconnectPolicy();

        Assert.NotNull(policy);
        Assert.Equal(3, policy!.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.InitialDelay);
        Assert.Equal(1.0, policy.BackoffFactor);
        Assert.Equal(TimeSpan.FromSeconds(120), policy.MaxDelay);
    }

    [Fact]
    public void ConfigProvider_NegativeAttempts_MeansUnlimited()
    {
        ReconnectPolicy? policy = new ConfigReconnectPolicyProvider(new ReconnectConfig { MaxAttempts = -1 }).GetReconnectPolicy();
        Assert.NotNull(policy);
        Assert.True(policy!.MaxAttempts < 0);
    }

    [Fact]
    public void Policy_DelayFor_HonorsBackoffAndCap()
    {
        var config = new ReconnectConfig
        {
            MaxAttempts = 10,
            DelaySeconds = 5,
            BackoffFactor = 2.0,
            MaxDelaySeconds = 120,
        };

        ReconnectPolicy policy = new ConfigReconnectPolicyProvider(config).GetReconnectPolicy()!;

        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayFor(0));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(20), policy.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(120), policy.DelayFor(10)); // capped
    }

    [Fact]
    public void ConfigProvider_RetryOnKickDefaultsOn_AndInstallsNoVeto()
    {
        ReconnectPolicy policy = new ConfigReconnectPolicyProvider(new ReconnectConfig { MaxAttempts = 3 })
            .GetReconnectPolicy()!;

        // Nothing to pay per disconnect when the key is on: the shipped behavior is byte-for-byte the old one.
        Assert.Null(policy.ShouldRetry);
        Assert.True(ReconnectPolicy.IsRetryable(
            policy, new DisconnectInfo { Reason = CloseReason.DisconnectMessage }));
    }

    [Fact]
    public void ConfigProvider_RetryOnKickOff_RefusesAKickButStillRetriesATransportFault()
    {
        ReconnectPolicy policy =
            new ConfigReconnectPolicyProvider(new ReconnectConfig { MaxAttempts = 3, RetryOnKick = false })
                .GetReconnectPolicy()!;

        Assert.NotNull(policy.ShouldRetry);

        // The kick is refused...
        Assert.False(ReconnectPolicy.IsRetryable(
            policy, new DisconnectInfo { Reason = CloseReason.DisconnectMessage }));

        // ...while every transport fault still recovers, which is the whole point of the distinction.
        Assert.True(ReconnectPolicy.IsRetryable(
            policy, new DisconnectInfo { Reason = CloseReason.SocketEof }));
        Assert.True(ReconnectPolicy.IsRetryable(
            policy, new DisconnectInfo { Reason = CloseReason.IdleTimeout }));
        Assert.True(ReconnectPolicy.IsRetryable(
            policy, new DisconnectInfo { Reason = CloseReason.ProtocolViolation }));
    }
}
