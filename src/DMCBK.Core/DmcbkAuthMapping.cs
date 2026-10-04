using Umpk.Auth;

namespace DMCBK.Core;

/// <summary>
/// Pure mapping between the MCC account model and UMPK's auth flow selectors.
/// Kept separate from the client so the account-kind resolution table is unit-testable without a session.
/// </summary>
internal static class DmcbkAuthMapping
{
    /// <summary>Maps an account kind to the UMPK <see cref="AuthFlowKind"/> that runs it.</summary>
    public static AuthFlowKind ToFlowKind(DmcbkAccountKind kind) => kind switch
    {
        DmcbkAccountKind.Offline => AuthFlowKind.Offline,
        DmcbkAccountKind.MicrosoftDeviceCode => AuthFlowKind.MicrosoftDeviceCode,
        DmcbkAccountKind.MicrosoftBrowser => AuthFlowKind.MicrosoftBrowser,
        DmcbkAccountKind.Yggdrasil => AuthFlowKind.Yggdrasil,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown account kind."),
    };

    /// <summary>
    /// The authlib-injector API root for an account: null (Mojang defaults) for offline and Microsoft accounts, and the configured provider base for Yggdrasil, normalized through <see cref="YggdrasilEndpoints.EnsureTrailingSlash"/> (every consumer resolves relative URIs against it, and a base with no trailing slash would silently drop its last path segment).
    /// </summary>
    public static Uri? ProviderBaseUrl(DmcbkAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Kind == DmcbkAccountKind.Yggdrasil && account.AuthServerBaseUrl is { } provider
            ? YggdrasilEndpoints.EnsureTrailingSlash(provider)
            : null;
    }

    /// <summary>
    /// The session-server base URL for an account: null (Mojang default) for offline and Microsoft accounts, and the provider's <see cref="YggdrasilEndpoints.SessionServer"/> sub-API for Yggdrasil.
    /// </summary>
    public static Uri? SessionBaseUrl(DmcbkAccount account)
        => ProviderBaseUrl(account) is { } provider ? YggdrasilEndpoints.SessionServer(provider) : null;
}
