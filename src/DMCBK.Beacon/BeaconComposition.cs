using DMCBK.Core.Commands.Impl;

namespace DMCBK.Core;

/// <summary>Composes the optional Beacon runtime and script command adapter.</summary>
public static class BeaconComposition
{
    extension(Client client)
    {
        /// <summary>The attached Beacon script service.</summary>
        public ScriptsApi Scripts => client.GetModule<ScriptsApi>();
    }

    /// <summary>Adds the per-client Beacon runtime. Commands must be attached first.</summary>
    public static ClientBuilder UseBeacon(this ClientBuilder builder)
        => builder.UseModule(client =>
        {
            var scripts = new ScriptsApi(client);
            scripts.CommandRegistration = client.Commands.RegisterScopedCommand(new ScriptsCommand());
            return scripts;
        }, "beacon");
}
