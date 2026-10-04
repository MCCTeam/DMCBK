namespace DMCBK.Core;

/// <summary>Host identity independent of the client library version.</summary>
/// <param name="Id">The application's stable identifier.</param>
/// <param name="Version">The application release version.</param>
/// <param name="Capabilities">Additional presentation or framework capabilities supplied by the host.</param>
public sealed record HostApplication(string Id, string Version, IReadOnlySet<string> Capabilities);
