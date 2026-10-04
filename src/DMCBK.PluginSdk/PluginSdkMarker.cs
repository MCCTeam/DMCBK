namespace DMCBK.PluginSdk;

/// <summary>
/// Marker type for the plugin-facing contract assembly (<c>IPlugin</c>, <c>PluginContext</c>, settings/storage/messaging contracts).
/// This type exists only so the assembly compiles and can be referenced by the dependency guard; it intentionally carries no behavior.
/// </summary>
public sealed class PluginSdkMarker
{
}
