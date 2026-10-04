using Tomlet.Models;
using DMCBK.Core.Configuration;
namespace DMCBK.Core.Beacon;

internal static class TomlUnknownKeyScanner
{
    public static void Scan(TomlTable table, Type type, string prefix, List<ConfigurationWarning> warnings)
        => ConfigurationKeyScanner.Scan(table.Entries, type, prefix, warnings,
            value => value is TomlTable sub ? sub.Entries : null,
            value => value is TomlArray array ? array.ArrayValues : null);
}
