namespace DMCBK.Core;

/// <summary>The authentication flow an <see cref="MccAccount"/> uses.</summary>
/// <remarks>
/// The ordinal order deliberately differs from <see cref="Umpk.Auth.AuthFlowKind"/>: MCC's TOML-facing contract defaults an unspecified account kind to <see cref="Offline"/>, so Offline has to be ordinal 0 here even though UMPK lists it last.
/// <see cref="MccAuthMapping.ToFlowKind"/> is the single crossing point between the two enums; do not consolidate them.
/// </remarks>
public enum MccAccountKind
{
    /// <summary>Offline mode: deterministic UUID, no network auth (the offline path).</summary>
    Offline,

    /// <summary>Microsoft account via the OAuth 2.0 device-code flow (host renders the code and URL).</summary>
    MicrosoftDeviceCode,

    /// <summary>Microsoft account via the OAuth 2.0 browser auth-code flow (loopback listener).</summary>
    MicrosoftBrowser,

    /// <summary>A third-party Yggdrasil (authlib-injector) username/password account.</summary>
    Yggdrasil,
}

/// <summary>
/// An account the client logs in as.
/// Offline accounts carry only a username; online accounts carry a login hint (a username or email) and never a plaintext password (Microsoft credentials are entered through the host <see cref="Umpk.Auth.IAuthInteraction"/>; Yggdrasil credentials are prompted at login and never stored here).
/// <see cref="AuthServerBaseUrl"/> targets a third-party Yggdrasil provider.
/// </summary>
public sealed record MccAccount
{
    /// <summary>The authentication flow.</summary>
    public required MccAccountKind Kind { get; init; }

    /// <summary>The offline username, or the online login hint (username/email) used as the token-cache key.</summary>
    public required string User { get; init; }

    /// <summary>
    /// The base URL of a third-party Yggdrasil provider (authlib-injector) for <see cref="MccAccountKind.Yggdrasil"/>; null uses the Mojang defaults.
    /// Ignored for other kinds.
    /// </summary>
    public Uri? AuthServerBaseUrl { get; init; }

    /// <summary>True for any non-offline account.</summary>
    public bool IsOnline => Kind != MccAccountKind.Offline;

    /// <summary>Creates an offline account for a username.</summary>
    public static MccAccount Offline(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return new MccAccount { Kind = MccAccountKind.Offline, User = username };
    }
}
