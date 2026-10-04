using DMCBK.Core;

namespace DMCBK.PluginSdk;

/// <summary>Independent library, engine, application and process compatibility information.</summary>
/// <param name="ApiVersion">The plugin contract version.</param>
/// <param name="DmcbkVersion">The DMCBK library version.</param>
/// <param name="UmpkVersion">The protocol engine version.</param>
/// <param name="ApplicationId">The application's stable identifier.</param>
/// <param name="ApplicationVersion">The application's independent version.</param>
/// <param name="RuntimeTarget">The running process platform target.</param>
/// <param name="AvailableCapabilities">Capabilities provided by this client and host.</param>
public sealed record HostInfo(string ApiVersion, string DmcbkVersion, string UmpkVersion,
    string ApplicationId, string ApplicationVersion, string RuntimeTarget,
    IReadOnlySet<string> AvailableCapabilities)
{
    /// <summary>Reads library versions and the explicit identity of one client.</summary>
    public static HostInfo FromClient(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(PluginApiVersion.Current, Core.DmcbkVersion.Current, UmpkVersionProbe.Current,
            client.Application.Id, client.Application.Version, PlatformTarget.Detect(), client.AvailableCapabilities);
    }
}
