using System.Resources;
namespace DMCBK.PluginSdk;

internal static class TextResources
{
    private static readonly ResourceManager Resources = new("DMCBK.PluginSdk.Resources.TextResources", typeof(TextResources).Assembly);
    internal static string Get(string key) => Resources.GetString(key, DMCBK.Core.Localization.McStrings.Culture) ?? key;
}
