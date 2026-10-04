using DMCBK.Core.Configuration;
using Umpk.Auth;
using Umpk.Protocol.Java;
using Umpk.Realms;

namespace DMCBK.Core.Commands;

/// <summary>
/// The <c>connect</c> command's Realm branch, factored out so it can be tested with a fake <see cref="IRealmsClient"/> and a constructed <see cref="JavaSession"/> without a live client.
/// It turns a <see cref="ConfiguredServerKind.Realm"/> entry into either a <see cref="ServerSelection"/> ready for <c>ReconnectAsync</c> or a localized error, mapping each <see cref="RealmsErrorKind"/> to a <see cref="CommandStrings"/> message.
/// It never connects and never touches the console.
/// </summary>
internal static class RealmConnectPlanner
{
    /// <summary>
    /// Resolves the Realm world named by <paramref name="server"/> (its <see cref="ConfiguredServer.RealmWorld"/>, or its <see cref="ConfiguredServer.Name"/> when that is empty) to a connectable selection.
    /// <paramref name="clientVersion"/> is the version string reported to Realms; <paramref name="pinnedVersion"/> pins the reconnect version (null re-detects on the resolved host).
    /// <paramref name="realmsClientFactory"/> builds the short-lived <see cref="IRealmsClient"/> for this one resolve; it is disposed before this method returns.
    /// It is never invoked for a null or non-Microsoft <paramref name="session"/>: both are classified before the factory would otherwise run.
    /// </summary>
    public static async Task<RealmConnectPlan> PlanAsync(
        Func<RealmsSessionCredential, IRealmsClient> realmsClientFactory,
        JavaSession? session,
        string clientVersion,
        ConfiguredServer server,
        JavaVersion? pinnedVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(realmsClientFactory);
        ArgumentNullException.ThrowIfNull(server);

        string selector = string.IsNullOrWhiteSpace(server.RealmWorld) ? server.Name : server.RealmWorld;

        try
        {
            // A null session is a fact about the world here (no account is signed in yet), not a caller bug, so it is classified the same way RealmsSessionCredential.FromSession classifies a non-Microsoft one below, rather than thrown as an ArgumentNullException.
            if (session is null)
                throw new RealmsException(RealmsErrorKind.RequiresMicrosoftAccount, "Realms requires a signed-in Microsoft account.");

            RealmsSessionCredential credential = RealmsSessionCredential.FromSession(session, clientVersion);
            using IRealmsClient client = realmsClientFactory(credential);
            RealmServerAddress address = await client.ResolveWorldAsync(selector, ct).ConfigureAwait(false);
            var selection = new ServerSelection
            {
                Host = address.Host,
                Port = (ushort)Math.Clamp(address.Port, 0, ushort.MaxValue),
                Version = pinnedVersion,
            };
            return RealmConnectPlan.Ok(selector, selection);
        }
        catch (RealmsException ex)
        {
            return RealmConnectPlan.Failed(selector, Describe(ex.Kind, selector));
        }
    }

    private static string Describe(RealmsErrorKind kind, string selector) => kind switch
    {
        RealmsErrorKind.RequiresMicrosoftAccount => CommandStrings.ConnectRealmRequiresMicrosoft,
        RealmsErrorKind.TermsNotAgreed => CommandStrings.ConnectRealmTermsNotAgreed,
        RealmsErrorKind.Unauthorized => CommandStrings.ConnectRealmAccessDenied,
        RealmsErrorKind.WorldNotFound => CommandStrings.ConnectRealmWorldNotFound(selector),
        RealmsErrorKind.ServiceBusy => CommandStrings.ConnectRealmServiceBusy,
        RealmsErrorKind.ClientOutdated => CommandStrings.ConnectRealmClientOutdated,
        _ => CommandStrings.ConnectRealmServiceError,
    };
}

/// <summary>The outcome of <see cref="RealmConnectPlanner.PlanAsync"/>: a ready selection or a localized error.</summary>
internal sealed class RealmConnectPlan
{
    private RealmConnectPlan(bool success, string worldSelector, ServerSelection? selection, string? error)
    {
        Success = success;
        WorldSelector = worldSelector;
        Selection = selection;
        Error = error;
    }

    /// <summary>True when the world resolved to a connectable selection.</summary>
    public bool Success { get; }

    /// <summary>The world name/id that was resolved (for display).</summary>
    public string WorldSelector { get; }

    /// <summary>The resolved selection when <see cref="Success"/>; else null.</summary>
    public ServerSelection? Selection { get; }

    /// <summary>The localized error when not <see cref="Success"/>; else null.</summary>
    public string? Error { get; }

    /// <summary>Builds a successful plan.</summary>
    public static RealmConnectPlan Ok(string worldSelector, ServerSelection selection)
        => new(true, worldSelector, selection, null);

    /// <summary>Builds a failed plan carrying a localized error.</summary>
    public static RealmConnectPlan Failed(string worldSelector, string error)
        => new(false, worldSelector, null, error);
}
