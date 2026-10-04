namespace DMCBK.Samples.HeadlessClient;

/// <summary>
/// A public anchor type so tests can reference this assembly (its <c>Program</c>/logger types are internal).
/// <see cref="DMCBK.Core.Tests.DependencyGuardTests"/> uses it as a third dependency-guard root, making the "the embedding host drags in no console/UI library" law a CI gate for the sample host as well as the core.
/// </summary>
public static class HostSampleMarker
{
}
