using System.Security.Cryptography;
using DMCBK.Core.Configuration;
using DMCBK.Core.Tests.Fakes;
using DMCBK.Testing.Server;
using Umpk;
using Umpk.Auth;
using Umpk.Client;
using Umpk.Data.Java;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Codecs;
using Umpk.Protocol.Java.Packets;
using Umpk.Protocol.Java.Signing;
using Umpk.Protocol.Java.Transport;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class EncryptedOfflineSigningTests
{
    [Fact]
    public async Task CachedOnlineAccount_EncryptedOfflineServer_DoesNotAnnounceProfileCertificate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dmcbk-encrypted-offline-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            CancellationToken ct = cts.Token;
            Assert.True(JavaVersions.TryGetByProtocol(776, out JavaVersion version));
            await using FakeJavaServer server = FakeJavaServer.Create();
            var accountProfile = new GameProfile(Guid.NewGuid(), "OnlineAccount");
            var store = new FileTokenStore(directory, TokenProtectors.CreateDefault());
            await store.SetAsync("session:ACCOUNT@EXAMPLE.INVALID", new JavaSession(accountProfile, "FAKE_TOKEN", DateTimeOffset.UtcNow.AddHours(12), null, AuthKind.Microsoft), ct);
            using RSA rsa = RSA.Create(2048);
            await store.SetAsync("certificates:ONLINEACCOUNT", new PlayerCertificates(rsa.ExportSubjectPublicKeyInfoPem(), rsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(new byte[512]), Convert.ToBase64String(new byte[512]), DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddHours(12)), ct);
            await using Client client = new ClientBuilder()
                .UseConfiguration(new DmcbkConfiguration { Chat = new ChatConfig { Signature = new SignatureConfig { LoginWithSecureProfile = true } } })
                .UseAccount(new DmcbkAccount { Kind = DmcbkAccountKind.MicrosoftBrowser, User = "account@example.invalid" })
                .UseTokenStorePath(directory)
                .UseHostInterface(new NoInteractionHost())
                .UseServer("test", 25565)
                .UseVersion(version)
                .UseProxy(new PipeConnections(server.ClientPipe))
                .ConfigureFeatures(f => { f.Physics = false; f.Pathfinding = false; })
                .Build();
            UmpkClient? session = null;
            client.ObserveSessionClients(u => session = u);
            Task start = client.StartAsync(ct);
            await server.NextFrameAsync(ct);
            server.ServerConnection.SetPhase(ProtocolPhase.Login);
            await server.NextFrameAsync(ct);
            byte[] token = [1, 2, 3, 4];
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Login,
                new ClientboundHelloPacket("", rsa.ExportSubjectPublicKeyInfo(), token, ShouldAuthenticate: false), ct, version);
            InboundFrame response = await server.NextFrameAsync(ct);
            PhaseRegistry login = version.Protocol.GetRegistry(ProtocolPhase.Login, PacketFlow.Serverbound);
            Assert.True(login.TryGetInbound(response.WireId, out BoundPacketCodec keyCodec));
            var key = (ServerboundKeyPacket)keyCodec.Decode(response.Payload, PacketCodecContext.Registryless);
            Assert.Equal(token, rsa.Decrypt(key.VerifyToken, RSAEncryptionPadding.Pkcs1));
            server.ServerConnection.EnableEncryption(rsa.Decrypt(key.SharedSecret, RSAEncryptionPadding.Pkcs1));
            Guid offlineId = OfflineIdentity.ComputeProfile(accountProfile.Name).Id;
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Login,
                new ClientboundLoginFinishedPacket(offlineId, accountProfile.Name, [], Guid.NewGuid()), ct, version);
            await server.NextFrameAsync(ct); // login acknowledgment
            server.ServerConnection.SetPhase(ProtocolPhase.Configuration);
            await server.NextFrameAsync(ct); // client information
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Configuration, new ClientboundFinishConfigurationPacket(), ct, version);
            await server.NextFrameAsync(ct); // configuration acknowledgment
            server.ServerConnection.SetPhase(ProtocolPhase.Play);
            var spawn = new CommonPlayerSpawnInfo(0, "minecraft:overworld", 0, 1, -1, false, true, null, 0, 64);
            // OfflineEncryptor reports OnlineMode=true in the 26.2 play login packet too. Neither that
            // value nor the encrypted transport establishes session authentication.
            await McPluginSessionHarness.SendAsync(server, ProtocolPhase.Play,
                new ClientboundLoginPacket(1, false, ["minecraft:overworld"], 5, 4, 4, false, true, false, spawn, OnlineMode: true, EnforcesSecureChat: false, Legacy: null), ct, version);
            await start;
            Assert.NotNull(session);
            Assert.True(session.IsConnectionEncrypted);
            Assert.False(session.Session!.IsAuthenticated);
            Assert.Equal(offlineId, session.Session.Profile.Id);
            await client.Game.Chat.SendAsync("offline encrypted witness", ct);
            PhaseRegistry play = version.Protocol.GetRegistry(ProtocolPhase.Play, PacketFlow.Serverbound);
            while (true)
            {
                InboundFrame frame = await server.NextFrameAsync(ct);
                Assert.True(play.TryGetInbound(frame.WireId, out BoundPacketCodec codec));
                Assert.NotEqual(Identifier.Minecraft("chat_session_update"), codec.Type.Id);
                if (codec.Type.Id != Identifier.Minecraft("chat"))
                    continue;
                // Receiving the fresh chat frame proves the deferred signing setup completed without
                // announcing the cached account certificate under the server's offline UUID.
                break;
            }
            Assert.Equal(ClientStatus.Playing, client.Status);
            await client.StopAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PipeConnections(System.IO.Pipelines.IDuplexPipe pipe) : IConnectionFactory
    {
        public ValueTask<System.IO.Pipelines.IDuplexPipe> ConnectAsync(ServerEndpoint endpoint, CancellationToken ct) => ValueTask.FromResult(pipe);
    }

    private sealed class NoInteractionHost : IHostInterface
    {
        public IUserPrompt? Prompt => null;
        public IAuthInteraction? AuthInteraction { get; } = new NoInteraction();
    }

    private sealed class NoInteraction : IAuthInteraction
    {
        public Task<string> GetBrowserAuthCodeAsync(Uri url, CancellationToken ct) => throw new InvalidOperationException("Unexpected browser authentication.");
        public Task ShowDeviceCodeAsync(DeviceCodePrompt prompt, CancellationToken ct) => throw new InvalidOperationException("Unexpected device authentication.");
        public Task<YggdrasilCredentials> GetYggdrasilCredentialsAsync(CancellationToken ct) => throw new InvalidOperationException("Unexpected Yggdrasil authentication.");
    }
}
