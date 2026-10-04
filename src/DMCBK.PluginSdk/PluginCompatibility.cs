namespace DMCBK.PluginSdk;

/// <summary>One structured compatibility failure, rendered by the host.</summary>
/// <param name="Code">The stable diagnostic identifier.</param>
/// <param name="Values">The associated metadata values.</param>
public sealed record PluginCompatibilityFailure(string Code, params string[] Values);

/// <summary>Shared compatibility rules for marketplace resolution and runtime activation.</summary>
public static class PluginCompatibility
{
    /// <summary>Checks API, libraries, application, framework, capabilities and an optional selected target.</summary>
    public static IReadOnlyList<PluginCompatibilityFailure> Check(string apiVersion, string dmcbk, string umpk,
        string framework, IEnumerable<string> needs, IReadOnlyDictionary<string, string> hosts, HostInfo host, string? target = null)
    {
        var failures = new List<PluginCompatibilityFailure>();
        if (!PluginApiVersion.TryParse(host.ApiVersion, out int hostMajor, out int hostMinor)
            || !PluginApiVersion.TryParse(apiVersion, out int major, out int minor)
            || major != hostMajor || minor > hostMinor)
            failures.Add(new("compatibility.api", apiVersion, host.ApiVersion));
        if (!SemVerRange.TryParse(dmcbk, out SemVerRange? clientRange)
            || !SemVer.TryParse(host.DmcbkVersion, out SemVer clientVersion)
            || !clientRange!.Satisfies(clientVersion, includePrerelease: true))
            failures.Add(new("compatibility.dmcbk", dmcbk, host.DmcbkVersion));
        if (!SemVerRange.TryParse(umpk, out SemVerRange? engineRange)
            || !SemVer.TryParse(host.UmpkVersion, out SemVer engineVersion)
            || !engineRange!.Satisfies(engineVersion, includePrerelease: true))
            failures.Add(new("compatibility.umpk", umpk, host.UmpkVersion));
        string? range = hosts.FirstOrDefault(pair => pair.Key.Equals(host.ApplicationId, StringComparison.OrdinalIgnoreCase)).Value;
        if (hosts.Count > 0 && (range is null || !SemVerRange.Parse(range).Satisfies(SemVer.Parse(host.ApplicationVersion), includePrerelease: true)))
            failures.Add(new("compatibility.application", host.ApplicationId, host.ApplicationVersion, range ?? string.Join(", ", hosts.Keys)));
        if (framework != "net10.0") failures.Add(new("compatibility.framework", framework));
        if (target is not null && target != "any" && target != host.RuntimeTarget)
            failures.Add(new("compatibility.target", target, host.RuntimeTarget));
        foreach (string capability in needs.Where(need => !host.AvailableCapabilities.Contains(need)))
            failures.Add(new("compatibility.capability", capability));
        return failures;
    }
}
