namespace DMCBK.Core.Configuration;

// Immutable, validated configuration sections.
// These are produced by the loader pipeline (deserialize -> validate/derive -> snapshot) and are what consumers read.
// Fields whose consumers land in a later phase are marked with the landing phase in their doc comments; they are modeled now so the on-disk files stay stable, including a few no consumer reads yet.

/// <summary>How the client reaches a server (address, version and connection policy).</summary>
public sealed record ConnectionConfig
{
    /// <summary>The default server host (or SRV domain).</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>The default server port.</summary>
    public ushort Port { get; init; } = 25565;

    /// <summary>The version to use: <c>"auto"</c> for ping detection, or a release name such as <c>1.21.5</c>.</summary>
    public string Version { get; init; } = "auto";

    /// <summary>SRV-record resolution policy.</summary>
    public SrvResolveMode SrvResolve { get; init; } = SrvResolveMode.Fast;

    /// <summary>TCP connect/read timeout in seconds (clamped to at least 1).</summary>
    public int TcpTimeoutSeconds { get; init; } = 30;

    /// <summary>The brand string reported to the server.</summary>
    public BrandKind Brand { get; init; } = BrandKind.Mcc;

    /// <summary>
    /// Whether the client dials the configured server on start.
    /// False starts idle: the client is built, plugins load and the prompt runs, and nothing connects until <c>connect</c> or <c>reco</c> asks.
    /// </summary>
    public bool AutoConnect { get; init; } = true;

    /// <summary>Auto-reconnect policy applied after an unexpected disconnect.</summary>
    public ReconnectConfig Reconnect { get; init; } = new();
}

/// <summary>
/// Auto-reconnect policy (re-homes the legacy <c>ReconnectionAttemptsLeft</c> loop).
/// Maps onto UMPK's <c>ReconnectPolicy</c>.
/// Matching legacy defaults, auto-reconnect is OFF unless <see cref="MaxAttempts"/> is set to a non-zero value.
/// </summary>
public sealed record ReconnectConfig
{
    /// <summary>
    /// Maximum automatic reconnect attempts after an unexpected disconnect.
    /// <c>0</c> disables auto-reconnect (the legacy default); a negative value means unlimited attempts.
    /// </summary>
    public int MaxAttempts { get; init; }

    /// <summary>Delay before the first reconnect attempt, in seconds (clamped to at least 0). Legacy default 5.</summary>
    public double DelaySeconds { get; init; } = 5.0;

    /// <summary>
    /// Multiplier applied to the delay after each failed attempt (exponential backoff).
    /// <c>1.0</c> keeps the legacy fixed delay; clamped to at least 1.
    /// </summary>
    public double BackoffFactor { get; init; } = 1.0;

    /// <summary>Upper bound on the reconnect delay, in seconds (clamped to at least <see cref="DelaySeconds"/>).</summary>
    public double MaxDelaySeconds { get; init; } = 120.0;

    /// <summary>
    /// Whether an automatic reconnect follows a server KICK as well as a dropped connection.
    /// A kick is a deliberate server decision (a ban, a whitelist, a restart, an anti-bot rule) and reconnecting into one is how a client ends up hammering a server that just told it to go away, so this turns that off while leaving ordinary connection-loss recovery on.
    /// Defaults to <c>true</c>, which is the behavior that shipped while a kick could not be told apart from a transport fault at all.
    /// </summary>
    public bool RetryOnKick { get; init; } = true;
}

/// <summary>Feature gates and gameplay behavior.</summary>
public sealed record GameplayConfig
{
    /// <summary>Track terrain/world state.</summary>
    public bool Terrain { get; init; } = true;

    /// <summary>Track inventory/container state.</summary>
    public bool Inventory { get; init; } = true;

    /// <summary>Track entity state.</summary>
    public bool Entity { get; init; } = true;

    /// <summary>Run the local physics simulation.</summary>
    public bool Physics { get; init; } = true;

    /// <summary>Enable pathfinding/navigation.</summary>
    public bool Pathfinding { get; init; } = true;

    /// <summary>Automatically respawn after death.</summary>
    public bool AutoRespawn { get; init; }

    /// <summary>Rotate the head while walking. No consumer reads this yet.</summary>
    public bool MoveHeadWhileWalking { get; init; } = true;

    /// <summary>Movement speed in blocks per second (clamped to at least 1).</summary>
    public int MovementSpeed { get; init; } = 2;

    /// <summary>Workaround that forces terrain handling on to avoid a bad-packet disconnect. No consumer reads this yet.</summary>
    public bool TemporaryFixBadpacket { get; init; }

    /// <summary>Keep connecting even when the player name looks invalid.</summary>
    public bool IgnoreInvalidPlayerName { get; init; } = true;

    /// <summary>
    /// Announce in the console when a status effect is gained or expires.
    /// Legacy's <c>Main.Advanced.ShowEffectMessages</c> (Settings.cs:835), default ON.
    /// </summary>
    public bool ShowEffectMessages { get; init; } = true;


}

/// <summary>Public/private/teleport chat-format regexes.</summary>
public sealed record ChatFormatConfig
{
    /// <summary>Use the built-in chat-format detection.</summary>
    public bool Builtins { get; init; } = true;

    /// <summary>Use the user-defined regexes below.</summary>
    public bool UserDefined { get; init; }

    /// <summary>Public-message regex (capture 1 = sender, capture 2 = message).</summary>
    public string Public { get; init; } = "^<([a-zA-Z0-9_]+)> (.+)$";

    /// <summary>Private-message regex (capture 1 = sender, capture 2 = message).</summary>
    public string Private { get; init; } = "^([a-zA-Z0-9_]+) whispers to you: (.+)$";

    /// <summary>Teleport-request regex (capture 1 = sender).</summary>
    public string TeleportRequest { get; init; } = "^([a-zA-Z0-9_]+) has requested (?:to teleport to you|that you teleport to them)\\.$";
}

/// <summary>
/// Chat-signing behavior and display toggles.
/// <para>
/// This section once carried a <c>SignChat</c> and a <c>SignMessageInCommand</c> alongside <see cref="LoginWithSecureProfile"/>.
/// They were removed rather than left in place: the library signs chat and signs command arguments off ONE per-session signing provider with no per-message or per-kind lever, so there was nothing either key could switch, and a documented setting that silently does nothing is worse than an absent one.
/// <see cref="LoginWithSecureProfile"/> maps exactly onto that one lever and is live.
/// </para>
/// </summary>
public sealed record SignatureConfig
{
    /// <summary>
    /// Log in advertising a secure profile: fetch the account's player certificates and hand them to the signing seam.
    /// Turning this off sends no profile key at login, no chat session update, and no signature on chat or on command arguments, which is what an account with no key looks like to a server; an <c>enforce-secure-profile</c> server will then refuse the join.
    /// Microsoft accounts only, and only on 1.19+; an offline session is unsigned regardless.
    /// </summary>
    public bool LoginWithSecureProfile { get; init; } = true;

    /// <summary>Mark legally signed messages in the display.</summary>
    public bool MarkLegallySignedMsg { get; init; } = true;

    /// <summary>Mark modified messages in the display.</summary>
    public bool MarkModifiedMsg { get; init; } = true;

    /// <summary>Mark illegally signed messages in the display.</summary>
    public bool MarkIllegallySignedMsg { get; init; } = true;

    /// <summary>
    /// Mark messages that were signed but could not be checked (no profile key seen for the sender, or per-peer verification is not configured).
    /// This is a standing of its own: neither verified nor rejected, and the display must not imply either.
    /// </summary>
    public bool MarkUnverifiedMsg { get; init; } = true;

    /// <summary>
    /// Mark messages that carried no signature at all (insecure chat, as an offline-mode server sends).
    /// Off by default: on an offline-mode server EVERY player message is insecure, so marking them all puts a marker on every line and conveys nothing.
    /// </summary>
    public bool MarkInsecureMsg { get; init; }

    /// <summary>Mark system messages in the display.</summary>
    public bool MarkSystemMessage { get; init; } = true;

    /// <summary>Show messages whose content was modified by the server.</summary>
    public bool ShowModifiedChat { get; init; } = true;

    /// <summary>Show messages with an invalid signature.</summary>
    public bool ShowIllegalSignedChat { get; init; } = true;
}

/// <summary>Outgoing chat behavior plus format and signing sub-blocks.</summary>
public sealed record ChatConfig
{
    /// <summary>Minimum delay between outgoing chat messages, in seconds (clamped to at least 0).</summary>
    public double MessageCooldownSeconds { get; init; } = 1.0;

    /// <summary>Maximum outgoing chat message length (0 = server default; clamped to 0..32767).</summary>
    public int MaxChatMessageLength { get; init; }

    /// <summary>The command name used to send a private message.</summary>
    public string PrivateMessageCommand { get; init; } = "tell";

    /// <summary>Chat-format regexes.</summary>
    public ChatFormatConfig Format { get; init; } = new();

    /// <summary>Chat-signing toggles.</summary>
    public SignatureConfig Signature { get; init; } = new();
}

/// <summary>
/// Skin part visibility announced in the client-settings packet.
/// <para>
/// Every part defaults ON, which is what vanilla defaults to: its model-part set is <c>EnumSet.allOf(PlayerModelPart.class)</c> (1.21.11-client-decompiled Options.java:327) and <c>buildPlayerInformation</c> ORs the whole set into the client-information byte (same file, :1797-1802), so a fresh install announces every part.
/// Announcing a subset is a choice a player makes in the skin-customisation screen, and announcing the same unusual subset on every join is a distinguishing mark rather than a neutral default.
/// </para>
/// </summary>
public sealed record SkinConfig
{
    /// <summary>Cape visible.</summary>
    public bool Cape { get; init; } = true;

    /// <summary>Hat visible.</summary>
    public bool Hat { get; init; } = true;

    /// <summary>Jacket visible.</summary>
    public bool Jacket { get; init; } = true;

    /// <summary>Left sleeve visible.</summary>
    public bool SleeveLeft { get; init; } = true;

    /// <summary>Right sleeve visible.</summary>
    public bool SleeveRight { get; init; } = true;

    /// <summary>Left pants leg visible.</summary>
    public bool PantsLeft { get; init; } = true;

    /// <summary>Right pants leg visible.</summary>
    public bool PantsRight { get; init; } = true;
}

/// <summary>The vanilla client-settings the client announces to the server.</summary>
public sealed record ClientSettingsConfig
{
    /// <summary>Send the client-settings packet at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The client locale (e.g. <c>en_US</c>).</summary>
    public string Locale { get; init; } = "en_US";

    /// <summary>
    /// View distance in chunks.
    /// 16 is not a cosmetic choice for a headless client: the server sends chunks out to this radius, so it bounds what the world model, the pathfinder and any plugin that reads terrain can see at all, and it is also what the server hears the client claim about itself.
    /// </summary>
    public byte RenderDistance { get; init; } = 16;

    /// <summary>Announced difficulty.</summary>
    public DifficultyKind Difficulty { get; init; } = DifficultyKind.Peaceful;

    /// <summary>Announced chat mode.</summary>
    public ChatModeKind ChatMode { get; init; } = ChatModeKind.Enabled;

    /// <summary>Announce chat colors enabled.</summary>
    public bool ChatColors { get; init; } = true;

    /// <summary>Announced main hand.</summary>
    public MainHandKind MainHand { get; init; } = MainHandKind.Left;

    /// <summary>Skin part visibility.</summary>
    public SkinConfig Skin { get; init; } = new();
}

/// <summary>Language selection and translation-loading flags.</summary>
public sealed record LocalizationConfig
{
    /// <summary>
    /// The UI language: <c>auto</c> (the default, meaning the operating system's) or a normalized tag in Minecraft form, for example <c>pt_br</c>.
    /// Resolved to a <see cref="System.Globalization.CultureInfo"/> once at startup by <c>UiCulture.Resolve</c>, and from there it drives MCC's own strings, the config comments, every plugin's <c>lang/</c> table and the manual.
    /// </summary>
    public string Language { get; init; } = Localization.UiCulture.Auto;

    /// <summary>Load translations from resource packs.</summary>
    public bool LoadResourcePackTranslations { get; init; } = true;

    /// <summary>
    /// What the client answers when a server pushes a resource pack.
    /// <c>Accept</c> (the default) reports the pack as loaded so strict servers keep the session; <c>Decline</c> answers Declined; <c>Prompt</c> asks the host once per push.
    /// </summary>
    public ResourcePackPolicyMode ResourcePackPolicy { get; init; } = ResourcePackPolicyMode.Accept;

    /// <summary>Load Forge mod translations. Forge is dropped; stored only.</summary>
    public bool LoadForgeModTranslations { get; init; }

    /// <summary>Auto-discover Forge mod translation sources. Forge is dropped; stored only.</summary>
    public bool AutoDiscoverForgeModTranslationSources { get; init; } = true;

    /// <summary>Path to Forge mod translations. Forge is dropped; stored only.</summary>
    public string ForgeModTranslationPath { get; init; } = string.Empty;
}

/// <summary>
/// Per-session diagnostics: what goes into the bundle under <c>logs/</c> that a tester sends back.
/// </summary>
public sealed record DiagnosticsConfig
{
    /// <summary>
    /// Write a bundle at all.
    /// On by default, because a tester who hits a bug needs the evidence to have been collected BEFORE they hit it.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Record raw wire frames. The largest part of a bundle and the most useful.</summary>
    public bool CapturePackets { get; init; } = true;

    /// <summary>Cap on the capture across all chunks, in megabytes. Never more than twenty.</summary>
    public int MaxCaptureMegabytes { get; init; } = 20;

    /// <summary>How many zipped bundles to keep before deleting the oldest. Never more than five.</summary>
    public int KeepSessions { get; init; } = 5;
}

/// <summary>
/// How much a plugin may misbehave before the host stops it.
/// Plugins run in-process with no sandbox, so an exception cannot be contained; what can be contained is a plugin throwing without end.
/// </summary>
public sealed record PluginsConfig
{
    /// <summary>
    /// Exceptions one plugin may throw inside <see cref="CrashWindowSeconds"/> before it is disabled.
    /// Zero or less never disables a plugin for throwing.
    /// </summary>
    public int CrashThreshold { get; init; } = 10;

    /// <summary>The sliding window the threshold is counted over. Zero or less switches the counter off.</summary>
    public int CrashWindowSeconds { get; init; } = 60;
}

/// <summary>Logging levels, filters, and optional file output.</summary>
public sealed record LoggingConfig
{
    /// <summary>Emit debug-level messages.</summary>
    /// <summary>
    /// Whether debug-level log lines are shown.
    /// Settable at RUNTIME, unlike its neighbours, because <c>/debug on|off</c> has to take effect immediately: legacy assigned the same flag and pushed it into the live logger (Commands/Debug.cs:54-58).
    /// The CLI's level gate reads this property on every call through a closure over this record (Mcc.Cli/ConsoleLogger.cs:63-65), so an assignment here is live with no re-wiring.
    /// It is deliberately NOT persisted, matching legacy, which only wrote the in-memory settings object.
    /// </summary>
    public bool DebugMessages { get; set; }

    /// <summary>Emit packet-debug messages.</summary>
    public bool PacketDebugMessages { get; init; }

    /// <summary>Packet names excluded from packet-debug output.</summary>
    public IReadOnlyList<string> PacketDebugExclusions { get; init; } = [];

    /// <summary>Emit chat messages to the log.</summary>
    public bool ChatMessages { get; init; } = true;

    /// <summary>Emit info-level messages.</summary>
    public bool InfoMessages { get; init; } = true;

    /// <summary>Emit warning-level messages.</summary>
    public bool WarningMessages { get; init; } = true;

    /// <summary>Emit error-level messages.</summary>
    public bool ErrorMessages { get; init; } = true;

    /// <summary>Chat-line filter regex.</summary>
    public string ChatFilterRegex { get; init; } = ".*";

    /// <summary>Debug-line filter regex.</summary>
    public string DebugFilterRegex { get; init; } = ".*";

    /// <summary>Filter mode applied to the regexes above.</summary>
    public LogFilterMode FilterMode { get; init; } = LogFilterMode.Disable;

    /// <summary>Also write the log to a file.</summary>
    public bool LogToFile { get; init; }

    /// <summary>The log file path (when <see cref="LogToFile"/> is on).</summary>
    public string LogFile { get; init; } = "console-log.txt";

    /// <summary>Prepend a timestamp to file log lines.</summary>
    public bool PrependTimestamp { get; init; }

    /// <summary>Keep color codes in file output.</summary>
    public bool SaveColorCodes { get; init; }

    /// <summary>Default log file for bot/plugin output.</summary>
    public string ChatbotLogFile { get; init; } = string.Empty;
}

/// <summary>Internal-command prefix and bot/plugin owner accounts.</summary>
public sealed record PermissionsConfig
{
    /// <summary>The prefix that marks a console line as an internal command.</summary>
    public InternalCommandPrefix CommandPrefix { get; init; } = InternalCommandPrefix.Slash;

    /// <summary>Accounts (lowercased) allowed to drive bots/plugins.</summary>
    public IReadOnlyList<string> BotOwners { get; init; } = [];
}

/// <summary>A configured account. Never carries a plaintext password.</summary>
public sealed record ConfiguredAccount
{
    /// <summary>The local label used to select the account.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The authentication flow.</summary>
    public MccAccountKind Kind { get; init; } = MccAccountKind.Offline;

    /// <summary>The username (offline) or login hint/email (online).</summary>
    public string Login { get; init; } = string.Empty;

    /// <summary>The Yggdrasil (authlib-injector) base URL for <see cref="MccAccountKind.Yggdrasil"/>; else null.</summary>
    public string? AuthServer { get; init; }
}

/// <summary>Accounts, active selection, cache policy, and cache directory (secrets file).</summary>
public sealed record AccountsConfig
{
    /// <summary>The name of the active account; empty uses the first entry.</summary>
    public string ActiveAccount { get; init; } = string.Empty;

    /// <summary>The account list.</summary>
    public IReadOnlyList<ConfiguredAccount> Accounts { get; init; } = [];

    /// <summary>Where to persist the online session token.</summary>
    public CacheMode SessionCache { get; init; } = CacheMode.Disk;

    /// <summary>Where to persist the chat-signing certificate.</summary>
    public CacheMode ProfileKeyCache { get; init; } = CacheMode.Disk;

    /// <summary>The cache directory (relative to the configurations folder) for tokens and certificates.</summary>
    public string CacheDirectory { get; init; } = "cache";
}

/// <summary>A named server entry.</summary>
public sealed record ConfiguredServer
{
    /// <summary>The local label used to select the server.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The server host (or SRV domain).</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>The server port.</summary>
    public ushort Port { get; init; } = 25565;

    /// <summary>A pinned version (release name) or <c>"auto"</c>.</summary>
    public string Version { get; init; } = "auto";

    /// <summary>The server kind (<c>normal</c> or <c>realm</c>).</summary>
    public ConfiguredServerKind Kind { get; init; } = ConfiguredServerKind.Normal;

    /// <summary>
    /// For a <see cref="ConfiguredServerKind.Realm"/> entry, the Realms world to resolve: its display name or numeric world id.
    /// Empty falls back to <see cref="Name"/>.
    /// Ignored for normal entries (Host/Port are used).
    /// </summary>
    public string RealmWorld { get; init; } = string.Empty;
}

/// <summary>Named servers and active selection.</summary>
public sealed record ServersConfig
{
    /// <summary>The name of the active server; empty falls back to the client.toml connection block.</summary>
    public string ActiveServer { get; init; } = string.Empty;

    /// <summary>The server list.</summary>
    public IReadOnlyList<ConfiguredServer> Servers { get; init; } = [];
}

/// <summary>Proxy configuration (secrets file).</summary>
public sealed record ProxyConfig
{
    /// <summary>Route the login handshake through the proxy.</summary>
    public bool EnabledLogin { get; init; }

    /// <summary>Route in-game traffic through the proxy.</summary>
    public bool EnabledIngame { get; init; }

    /// <summary>Proxy host.</summary>
    public string Host { get; init; } = "0.0.0.0";

    /// <summary>Proxy port.</summary>
    public ushort Port { get; init; } = 8080;

    /// <summary>Proxy protocol.</summary>
    public ProxyKind Kind { get; init; } = ProxyKind.Http;

    /// <summary>Proxy username (secret).</summary>
    public string Username { get; init; } = string.Empty;

    /// <summary>Proxy password (secret).</summary>
    public string Password { get; init; } = string.Empty;
}
