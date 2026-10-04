using Umpk;
using Umpk.Protocol.Java;

namespace DMCBK.Core;

/// <summary>
/// What the client is about to dial, offered for change before it does.
/// Raised as <see cref="Client.BeforeConnect"/> once per connect attempt, including every automatic reconnect, from inside the session factory and before the version ping, the login and the socket.
/// <para>
/// A handler may point the attempt at another address (a proxy pool, a server list, a hop the plugin decides), adjust the client-information announce, or <see cref="Veto"/> the attempt outright.
/// Changes apply to THIS attempt only: the next automatic retry starts from the configured address again and raises the event again.
/// </para>
/// </summary>
public sealed class ConnectPlan : EventArgs
{
    private string _host;

    internal ConnectPlan(
        ServerEndpoint endpoint,
        ClientInformationOptions clientInformation,
        int attempt,
        Umpk.Client.DisconnectInfo? previousDisconnect)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(clientInformation);
        _host = endpoint.Host;
        Port = endpoint.Port;
        ClientInformation = clientInformation;
        Attempt = attempt;
        PreviousDisconnect = previousDisconnect;
    }

    /// <summary>The host to dial. Assigning a blank value throws.</summary>
    public string Host
    {
        get => _host;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _host = value;
        }
    }

    /// <summary>The port to dial.</summary>
    public ushort Port { get; set; }

    /// <summary>
    /// The client-settings announce this session will send (locale, view distance, chat visibility, skin parts, main hand).
    /// It is a record: assign a modified copy, <c>plan.ClientInformation = plan.ClientInformation with { ViewDistance = 4 }</c>.
    /// </summary>
    public ClientInformationOptions ClientInformation { get; set; }

    /// <summary>0 for a connect the user or the host asked for, 1 and up for each automatic retry after a lost session.</summary>
    public int Attempt { get; }

    /// <summary>Why the previous session ended, when this attempt is an automatic retry. Null otherwise.</summary>
    public Umpk.Client.DisconnectInfo? PreviousDisconnect { get; }

    /// <summary>True once <see cref="Veto"/> has been called.</summary>
    public bool IsVetoed => VetoReason is not null;

    /// <summary>The reason the first vetoing handler gave, or null when nobody vetoed.</summary>
    public string? VetoReason { get; private set; }

    /// <summary>
    /// Stops this attempt.
    /// The connect fails with <see cref="DmcbkConnectVetoedException"/> carrying <paramref name="reason"/>, and no automatic retry follows, because a refusal the client made itself will be made again.
    /// A second veto does not replace the first reason.
    /// </summary>
    public void Veto(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        VetoReason ??= reason;
    }

    /// <summary>The address this plan currently names.</summary>
    internal ServerEndpoint ToEndpoint() => new(Host, Port);
}

/// <summary>
/// A <see cref="Client.BeforeConnect"/> handler stopped the attempt.
/// <see cref="Reason"/> is what the handler said, which is the only thing the user can act on.
/// </summary>
public sealed class DmcbkConnectVetoedException : DmcbkClientException
{
    /// <summary>Creates the exception.</summary>
    public DmcbkConnectVetoedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    /// <summary>The reason the handler gave.</summary>
    public string Reason { get; }
}
