using DMCBK.Core;
using Umpk.Auth;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The one branch of version resolution MCC still owns: falling back to the host's <see cref="IUserPrompt"/> when the status ping fails.
/// Parsing <c>version.protocol</c> and mapping it onto a catalog entry are now UMPK's own <c>ServerStatusParseTests</c>/<c>ServerVersionNegotiationTests</c>.
/// </summary>
public sealed class MccSessionFactoryTests
{
    [Fact]
    public async Task ResolveVersion_FallsBackToTheHostPrompt()
    {
        Assert.True(JavaVersions.TryGetByName("1.21.5", out JavaVersion version));
        var host = new PromptingHost(version);

        // Port 1 on loopback has nothing listening, so the status ping is refused immediately: no explicit version was pinned, so this drives straight into the host-prompt fallback.
        await using Client client = new ClientBuilder().UseCommands().UseBeacon()
            .UseServer("127.0.0.1", 1)
            .UseUsername("p_prompt")
            .UseHostInterface(host)
            .Build();

        // The prompt supplies a version, so the attempt proceeds to a real connect, which then fails against the closed port.
        // A ConnectFailedException (not a VersionResolutionException) is the proof the prompt's version made it all the way through.
        await Assert.ThrowsAsync<ConnectFailedException>(() => client.StartAsync());

        Assert.NotNull(host.LastRequest);
        Assert.Equal(version, client.NegotiatedVersion);
    }

    private sealed class PromptingHost(JavaVersion version) : IHostInterface
    {
        public VersionResolutionRequest? LastRequest { get; private set; }

        public IUserPrompt? Prompt => new VersionPrompt(this, version);

        public IAuthInteraction? AuthInteraction => null;

        private sealed class VersionPrompt(PromptingHost owner, JavaVersion version) : IUserPrompt
        {
            public ValueTask<JavaVersion?> ResolveVersionAsync(VersionResolutionRequest request, CancellationToken ct)
            {
                owner.LastRequest = request;
                return ValueTask.FromResult<JavaVersion?>(version);
            }
        }
    }
}
