using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Umpk.Auth;

namespace DMCBK.Core;

/// <summary>How an authlib-injector root answered <see cref="AuthServerProbe.ProbeAsync"/>.</summary>
public enum AuthServerProbeResult
{
    /// <summary>The root returned an authlib-injector metadata document.</summary>
    Valid,

    /// <summary>Nothing answered: DNS, TCP, TLS or timeout failure.</summary>
    Unreachable,

    /// <summary>Something answered, but not authlib-injector metadata.</summary>
    InvalidResponse,
}

/// <summary>
/// The two checks a Yggdrasil (authlib-injector) provider URL has to pass before it is worth writing into accounts.toml: that it is a usable base URL at all, and that the address actually serves an authlib-injector root.
/// <para>
/// This exists because an unusable provider URL is otherwise invisible until the first login, where it surfaces as an opaque auth failure long after the user has stopped looking at what they typed.
/// The probe is a setup-time convenience, not a security control: a reachable root proves the address is the right shape, nothing more.
/// </para>
/// </summary>
public static class AuthServerProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Normalizes a user-supplied provider URL into the form every consumer resolves relative URIs against: an absolute http/https URL with a host, no credentials, no query, no fragment, and a trailing slash (see <see cref="YggdrasilEndpoints.EnsureTrailingSlash"/> for why the slash is load bearing).
    /// Returns false for anything else rather than guessing, so a typo is reported instead of silently becoming a different address.
    /// </summary>
    public static bool TryNormalize(string? url, [NotNullWhen(true)] out Uri? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? parsed))
            return false;

        bool http = parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!http
            || string.IsNullOrWhiteSpace(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
            return false;

        normalized = YggdrasilEndpoints.EnsureTrailingSlash(parsed);
        return true;
    }

    /// <summary>
    /// Fetches <paramref name="authServerRoot"/> and reports whether it answered with an authlib-injector metadata document (a JSON object carrying a non-empty <c>meta.implementationName</c>, which is what every authlib-injector deployment serves at its root).
    /// </summary>
    public static async Task<AuthServerProbeResult> ProbeAsync(Uri authServerRoot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authServerRoot);

        string body;
        try
        {
            using var http = new HttpClient { Timeout = ProbeTimeout };
            using HttpResponseMessage response = await http.GetAsync(authServerRoot, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return AuthServerProbeResult.InvalidResponse;

            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException)
        {
            // A timeout arrives as an OperationCanceledException with the token unsignalled, so it lands here rather than in the rethrow above.
            return AuthServerProbeResult.Unreachable;
        }

        return HasImplementationName(body)
            ? AuthServerProbeResult.Valid
            : AuthServerProbeResult.InvalidResponse;
    }

    /// <summary>True when <paramref name="body"/> is a JSON object with a non-empty <c>meta.implementationName</c>.</summary>
    internal static bool HasImplementationName(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("meta", out JsonElement meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("implementationName", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString());
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
