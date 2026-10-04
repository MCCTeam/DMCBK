using DMCBK.Core.Commands;
using DMCBK.Core.Configuration;
using Umpk;
using Umpk.Auth;
using Umpk.Realms;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Tests the <c>connect</c> command's Realm branch (<see cref="RealmConnectPlanner"/>) against a fake <see cref="IRealmsClient"/> reached through a <c>Func&lt;RealmsSessionCredential, IRealmsClient&gt;</c> factory: a resolved world becomes a <see cref="ServerSelection"/> the reconnect path can consume, and every classified <see cref="RealmsException"/> the resolve chain (list, match, join) can throw maps to its localized <see cref="CommandStrings"/> message.
/// The selector falls back from the entry's RealmWorld to its Name.
/// No live client and no network.
/// </summary>
public sealed class RealmConnectBranchTests
{
    private const string ClientVersion = "1.21.11";

    private static JavaSession MicrosoftSession() => new(
        new GameProfile(Guid.NewGuid(), "Dinnerbone"),
        "MC_TOKEN_SECRET",
        DateTimeOffset.UtcNow.AddHours(1),
        RefreshToken: null,
        AuthKind.Microsoft);

    private static JavaSession OfflineSession() => new(
        new GameProfile(Guid.NewGuid(), "OfflinePlayer"),
        AccessToken: "",
        DateTimeOffset.UtcNow.AddHours(1),
        RefreshToken: null,
        AuthKind.Offline);

    private static ConfiguredServer RealmEntry(string name, string realmWorld = "") => new()
    {
        Name = name,
        Kind = ConfiguredServerKind.Realm,
        RealmWorld = realmWorld,
    };

    private static RealmWorld NewWorld(string name) => new(
        1, name, Motd: "", Owner: "", OwnerUuid: null, RealmState.Open, WorldType: "NORMAL",
        Expired: false, ExpiredTrial: false, DaysLeft: 0, MaxPlayers: 10, ActiveSlot: null, Member: false);

    /// <summary>
    /// Builds a factory over a fake client, plus a call counter so tests can assert the factory (and therefore any real HTTP path) was never reached.
    /// </summary>
    private static (Func<RealmsSessionCredential, IRealmsClient> Factory, Func<int> CallCount) NewFactory(IRealmsClient client)
    {
        int calls = 0;
        Func<RealmsSessionCredential, IRealmsClient> factory = _ =>
        {
            calls++;
            return client;
        };
        return (factory, () => calls);
    }

    [Fact]
    public async Task Plan_ResolvedWorld_ProducesSelectionFromAddress()
    {
        FakeRealmsClient client = FakeRealmsClient.Returning("myrealm", new RealmServerAddress("realm.example", 25566));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.True(plan.Success);
        Assert.NotNull(plan.Selection);
        Assert.Equal("realm.example", plan.Selection!.Host);
        Assert.Equal((ushort)25566, plan.Selection.Port);
        Assert.Null(plan.Selection.Version);
    }

    [Fact]
    public async Task Plan_UsesRealmWorldFieldAsSelector_WhenSet()
    {
        FakeRealmsClient client = FakeRealmsClient.Returning("My Survival World", new RealmServerAddress("host", 25565));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("shortlabel", realmWorld: "My Survival World");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.True(plan.Success);
        Assert.Equal("My Survival World", plan.WorldSelector);
    }

    [Fact]
    public async Task Plan_FallsBackToName_WhenRealmWorldEmpty()
    {
        FakeRealmsClient client = FakeRealmsClient.Returning("myrealm", new RealmServerAddress("host", 25565));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.True(plan.Success);
        Assert.Equal("myrealm", plan.WorldSelector);
    }

    [Fact]
    public async Task Plan_PortAboveUshortRange_IsClamped()
    {
        FakeRealmsClient client = FakeRealmsClient.Returning("myrealm", new RealmServerAddress("host", 70000));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.True(plan.Success);
        Assert.Equal(ushort.MaxValue, plan.Selection!.Port);
    }

    [Fact]
    public async Task Plan_NullSession_MapsToRequiresMicrosoftAccount_FactoryNeverInvoked()
    {
        FakeRealmsClient client = FakeRealmsClient.Returning("myrealm", new RealmServerAddress("host", 25565));
        (Func<RealmsSessionCredential, IRealmsClient> factory, Func<int> calls) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, session: null, ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmRequiresMicrosoft, plan.Error);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task Plan_OfflineSession_MapsToRequiresMicrosoftAccount_FactoryNeverInvoked()
    {
        FakeRealmsClient client = FakeRealmsClient.Returning("myrealm", new RealmServerAddress("host", 25565));
        (Func<RealmsSessionCredential, IRealmsClient> factory, Func<int> calls) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, OfflineSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmRequiresMicrosoft, plan.Error);
        Assert.Equal(0, calls());
    }

    [Fact]
    public async Task Plan_TermsNotAgreed_MapsToLocalizedError()
    {
        FakeRealmsClient client = FakeRealmsClient.Throwing(new RealmsException(RealmsErrorKind.TermsNotAgreed, "fake failure"));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmTermsNotAgreed, plan.Error);
    }

    [Fact]
    public async Task Plan_AccessDenied_MapsToLocalizedError()
    {
        FakeRealmsClient client = FakeRealmsClient.Throwing(new RealmsException(RealmsErrorKind.Unauthorized, "fake failure"));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmAccessDenied, plan.Error);
    }

    [Fact]
    public async Task Plan_WorldNotFound_MapsToLocalizedError()
    {
        FakeRealmsClient client = FakeRealmsClient.Throwing(new RealmsException(RealmsErrorKind.WorldNotFound, "fake failure"));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("ghost");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmWorldNotFound("ghost"), plan.Error);
    }

    [Fact]
    public async Task Plan_ServiceBusy_MapsToLocalizedError()
    {
        FakeRealmsClient client = FakeRealmsClient.Throwing(new RealmsException(RealmsErrorKind.ServiceBusy, "fake failure"));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmServiceBusy, plan.Error);
    }

    [Fact]
    public async Task Plan_ClientOutdated_MapsToLocalizedError()
    {
        FakeRealmsClient client = FakeRealmsClient.Throwing(new RealmsException(RealmsErrorKind.ClientOutdated, "fake failure"));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmClientOutdated, plan.Error);
    }

    [Fact]
    public async Task Plan_ServiceError_MapsToLocalizedError()
    {
        FakeRealmsClient client = FakeRealmsClient.Throwing(new RealmsException(RealmsErrorKind.ServiceError, "fake failure"));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmServiceError, plan.Error);
    }

    [Fact]
    public async Task Plan_UnmappedKind_FallsBackToServiceErrorString()
    {
        // WorldLocked has no CommandStrings mapping of its own; it must land in the same generic bucket as ServiceError rather than crash an unhandled-enum-value switch.
        FakeRealmsClient client = FakeRealmsClient.Throwing(new RealmsException(RealmsErrorKind.WorldLocked, "fake failure"));
        (Func<RealmsSessionCredential, IRealmsClient> factory, _) = NewFactory(client);
        ConfiguredServer server = RealmEntry("myrealm");

        RealmConnectPlan plan = await RealmConnectPlanner.PlanAsync(
            factory, MicrosoftSession(), ClientVersion, server, pinnedVersion: null, CancellationToken.None);

        Assert.False(plan.Success);
        Assert.Equal(CommandStrings.ConnectRealmServiceError, plan.Error);
    }

    /// <summary>
    /// A fake <see cref="IRealmsClient"/>: either lists a single world (matched by name) and joins it to a canned address, or throws a canned failure from <see cref="ListWorldsAsync"/>.
    /// </summary>
    private sealed class FakeRealmsClient : IRealmsClient
    {
        private readonly IReadOnlyList<RealmWorld>? _worlds;
        private readonly RealmServerAddress? _address;
        private readonly RealmsException? _failure;

        private FakeRealmsClient(IReadOnlyList<RealmWorld>? worlds, RealmServerAddress? address, RealmsException? failure)
        {
            _worlds = worlds;
            _address = address;
            _failure = failure;
        }

        public static FakeRealmsClient Returning(string worldName, RealmServerAddress address) =>
            new([NewWorld(worldName)], address, null);

        public static FakeRealmsClient Throwing(RealmsException failure) => new(null, null, failure);

        public Task<IReadOnlyList<RealmWorld>> ListWorldsAsync(CancellationToken ct) =>
            _failure is not null
                ? Task.FromException<IReadOnlyList<RealmWorld>>(_failure)
                : Task.FromResult(_worlds!);

        public Task<RealmServerAddress> JoinWorldAsync(long worldId, CancellationToken ct) =>
            Task.FromResult(_address!);

        public Task<RealmsCompatibility> CheckClientCompatibleAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task AgreeToTermsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
