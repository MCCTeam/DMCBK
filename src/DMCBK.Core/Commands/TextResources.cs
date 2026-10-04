using System.Resources;
namespace DMCBK.Core.Commands;

internal static class TextResources
{
    private static readonly ResourceManager Resources = new("DMCBK.Core.Commands.Resources.TextResources", typeof(TextResources).Assembly);
    internal static string Get(string key) => Resources.GetString(key, DMCBK.Core.Localization.McStrings.Culture) ?? key;
}
