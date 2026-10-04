using DMCBK.Core.Configuration;
using Umpk.Client;

namespace DMCBK.Core;

/// <summary>
/// The default <see cref="IReconnectPolicyProvider"/>: maps the immutable <see cref="ReconnectConfig"/> onto a UMPK <see cref="ReconnectPolicy"/>.
/// Matching legacy semantics, <see cref="ReconnectConfig.MaxAttempts"/> of <c>0</c> disables auto-reconnect entirely (returns <c>null</c>).
/// Instance-scoped, no static state.
/// <para>
/// <see cref="ReconnectConfig.RetryOnKick"/> becomes the policy's <see cref="ReconnectPolicy.ShouldRetry"/> veto, which is the seam UMPK provides for exactly this and the one place the kick / transport-fault distinction can change an outcome.
/// It is left null when the key is on, so an unconfigured client pays nothing per disconnect and behaves as it did before.
/// </para>
/// </summary>
public sealed class ConfigReconnectPolicyProvider : IReconnectPolicyProvider
{
    private readonly ReconnectPolicy? _policy;

    /// <summary>Builds a provider from the connection-section reconnect settings.</summary>
    public ConfigReconnectPolicyProvider(ReconnectConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _policy = config.MaxAttempts == 0
            ? null
            : new ReconnectPolicy
            {
                MaxAttempts = config.MaxAttempts,
                InitialDelay = TimeSpan.FromSeconds(config.DelaySeconds),
                BackoffFactor = config.BackoffFactor,
                MaxDelay = TimeSpan.FromSeconds(config.MaxDelaySeconds),
                ShouldRetry = config.RetryOnKick ? null : RefuseKicks,
            };
    }

    /// <inheritdoc />
    public ReconnectPolicy? GetReconnectPolicy() => _policy;

    /// <summary>
    /// The <c>retryonkick = false</c> veto: retry a broken connection, never a kick.
    /// A kick is the case where the server has told us to leave, so this is what stops a reconnect loop against a ban, a whitelist or an anti-bot rule.
    /// Only actionable since the play-phase disconnect became decodable on every protocol.
    /// </summary>
    private static bool RefuseKicks(DisconnectInfo info)
        => DisconnectInfo.Classify(info.Reason, info.WasLocal) != DisconnectKind.Kick;
}
