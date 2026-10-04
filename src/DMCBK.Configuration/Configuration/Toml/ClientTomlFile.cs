using Tomlet.Attributes;

namespace DMCBK.Core.Configuration.Toml;

// Mutable, TOML-facing model of client.toml. Deserialized by Tomlet, then folded into the immutable MccConfiguration snapshot by ConfigurationValidation.
// Enums are kept as strings here so the validator owns case-insensitive parsing and warn-and-default handling for unknown values.
// Comment attributes carry
// $CorpusKey$ placeholders that DefaultConfigWriter expands from the owned ConfigComments corpus.
//
// These are internal to DMCBK.Core: hosts and tests deal only with the immutable snapshot.

internal sealed class ClientTomlFile
{
    [TomlPrecedingComment("$Mcc.connection$")]
    public ConnectionTable Connection { get; set; } = new();

    [TomlPrecedingComment("$Mcc.gameplay$")]
    public GameplayTable Gameplay { get; set; } = new();

    [TomlPrecedingComment("$Mcc.chat$")]
    public ChatTable Chat { get; set; } = new();

    [TomlPrecedingComment("$Mcc.client_settings$")]
    public ClientSettingsTable ClientSettings { get; set; } = new();

    [TomlPrecedingComment("$Mcc.localization$")]
    public LocalizationTable Localization { get; set; } = new();

    [TomlPrecedingComment("$Logging$")]
    public LoggingTable Logging { get; set; } = new();

    [TomlPrecedingComment("$Diagnostics$")]
    public DiagnosticsTable Diagnostics { get; set; } = new();

    [TomlPrecedingComment("$Mcc.plugins$")]
    public PluginsTable Plugins { get; set; } = new();

    [TomlPrecedingComment("$Mcc.permissions$")]
    public PermissionsTable Permissions { get; set; } = new();

    [TomlPrecedingComment("$Mcc.variables$")]
    public Dictionary<string, string> Variables { get; set; } = new();

    internal sealed class ConnectionTable
    {
        [TomlPrecedingComment("$Mcc.connection.host$")]
        public string Host { get; set; } = string.Empty;

        [TomlPrecedingComment("$Mcc.connection.port$")]
        public int Port { get; set; } = 25565;

        [TomlPrecedingComment("$Main.Advanced.mc_version$")]
        public string Version { get; set; } = "auto";

        [TomlPrecedingComment("$Main.Advanced.resolve_srv_records$")]
        public string SrvResolve { get; set; } = "fast";

        [TomlPrecedingComment("$Main.Advanced.timeout$")]
        public int TcpTimeout { get; set; } = 30;

        [TomlPrecedingComment("$Main.Advanced.brand_info$")]
        public string Brand { get; set; } = "mcc";

        [TomlPrecedingComment("$Mcc.connection.auto_connect$")]
        public bool AutoConnect { get; set; } = true;

        [TomlPrecedingComment("$Mcc.connection.reconnect$")]
        public ReconnectTable Reconnect { get; set; } = new();
    }

    internal sealed class ReconnectTable
    {
        [TomlPrecedingComment("$Mcc.connection.reconnect.max_attempts$")]
        public int MaxAttempts { get; set; }

        [TomlPrecedingComment("$Mcc.connection.reconnect.delay_seconds$")]
        public double DelaySeconds { get; set; } = 5.0;

        [TomlPrecedingComment("$Mcc.connection.reconnect.backoff_factor$")]
        public double BackoffFactor { get; set; } = 1.0;

        [TomlPrecedingComment("$Mcc.connection.reconnect.max_delay_seconds$")]
        public double MaxDelaySeconds { get; set; } = 120.0;

        [TomlPrecedingComment("$Mcc.connection.reconnect.retry_on_kick$")]
        public bool RetryOnKick { get; set; } = true;
    }

    internal sealed class GameplayTable
    {
        [TomlPrecedingComment("$Main.Advanced.terrain_and_movements$")]
        public bool Terrain { get; set; } = true;

        [TomlPrecedingComment("$Main.Advanced.inventory_handling$")]
        public bool Inventory { get; set; } = true;

        [TomlPrecedingComment("$Main.Advanced.entity_handling$")]
        public bool Entity { get; set; } = true;

        [TomlPrecedingComment("$Mcc.gameplay.physics$")]
        public bool Physics { get; set; } = true;

        [TomlPrecedingComment("$Mcc.gameplay.pathfinding$")]
        public bool Pathfinding { get; set; } = true;

        [TomlPrecedingComment("$Main.Advanced.auto_respawn$")]
        public bool AutoRespawn { get; set; }

        [TomlPrecedingComment("$Main.Advanced.move_head_while_walking$")]
        public bool MoveHeadWhileWalking { get; set; } = true;

        [TomlPrecedingComment("$Main.Advanced.movement_speed$")]
        public int MovementSpeed { get; set; } = 2;

        [TomlPrecedingComment("$Main.Advanced.temporary_fix_badpacket$")]
        public bool TemporaryFixBadpacket { get; set; }

        [TomlPrecedingComment("$Main.Advanced.ignore_invalid_playername$")]
        public bool IgnoreInvalidPlayerName { get; set; } = true;


        public bool ShowEffectMessages { get; set; } = true;

    }

    internal sealed class ChatTable
    {
        [TomlPrecedingComment("$Main.Advanced.message_cooldown$")]
        public double MessageCooldown { get; set; } = 1.0;

        [TomlPrecedingComment("$Main.Advanced.max_chat_message_length$")]
        public int MaxChatMessageLength { get; set; }

        [TomlPrecedingComment("$Main.Advanced.private_msgs_cmd_name$")]
        public string PrivateMessageCommand { get; set; } = "tell";

        [TomlPrecedingComment("$ChatFormat$")]
        public ChatFormatTable Format { get; set; } = new();

        [TomlPrecedingComment("$Signature$")]
        public SignatureTable Signature { get; set; } = new();
    }

    internal sealed class ChatFormatTable
    {
        [TomlPrecedingComment("$ChatFormat.Builtins$")]
        public bool Builtins { get; set; } = true;

        [TomlPrecedingComment("$ChatFormat.UserDefined$")]
        public bool UserDefined { get; set; }

        public string Public { get; set; } = "^<([a-zA-Z0-9_]+)> (.+)$";

        public string Private { get; set; } = "^([a-zA-Z0-9_]+) whispers to you: (.+)$";

        public string TeleportRequest { get; set; }
            = "^([a-zA-Z0-9_]+) has requested (?:to teleport to you|that you teleport to them)\\.$";
    }

    internal sealed class SignatureTable
    {
        [TomlPrecedingComment("$Signature.LoginWithSecureProfile$")]
        public bool LoginWithSecureProfile { get; set; } = true;

        [TomlPrecedingComment("$Signature.MarkLegallySignedMsg$")]
        public bool MarkLegallySignedMsg { get; set; } = true;

        [TomlPrecedingComment("$Signature.MarkModifiedMsg$")]
        public bool MarkModifiedMsg { get; set; } = true;

        [TomlPrecedingComment("$Signature.MarkIllegallySignedMsg$")]
        public bool MarkIllegallySignedMsg { get; set; } = true;

        [TomlPrecedingComment("$Signature.MarkUnverifiedMsg$")]
        public bool MarkUnverifiedMsg { get; set; } = true;

        // Off by default on purpose: on an offline-mode server EVERY player message is insecure, so marking them all would put a marker on every single line.
        [TomlPrecedingComment("$Signature.MarkInsecureMsg$")]
        public bool MarkInsecureMsg { get; set; }

        [TomlPrecedingComment("$Signature.MarkSystemMessage$")]
        public bool MarkSystemMessage { get; set; } = true;

        [TomlPrecedingComment("$Signature.ShowModifiedChat$")]
        public bool ShowModifiedChat { get; set; } = true;

        [TomlPrecedingComment("$Signature.ShowIllegalSignedChat$")]
        public bool ShowIllegalSignedChat { get; set; } = true;
    }

    internal sealed class ClientSettingsTable
    {
        [TomlPrecedingComment("$MCSettings.Enabled$")]
        public bool Enabled { get; set; } = true;

        [TomlPrecedingComment("$MCSettings.Locale$")]
        public string Locale { get; set; } = "en_US";

        [TomlPrecedingComment("$MCSettings.RenderDistance$")]
        public int RenderDistance { get; set; } = 16;

        [TomlPrecedingComment("$MCSettings.Difficulty$")]
        public string Difficulty { get; set; } = "peaceful";

        [TomlPrecedingComment("$MCSettings.ChatMode$")]
        public string ChatMode { get; set; } = "enabled";

        [TomlPrecedingComment("$MCSettings.ChatColors$")]
        public bool ChatColors { get; set; } = true;

        [TomlPrecedingComment("$MCSettings.MainHand$")]
        public string MainHand { get; set; } = "left";

        public SkinTable Skin { get; set; } = new();
    }

    internal sealed class SkinTable
    {
        public bool Cape { get; set; } = true;
        public bool Hat { get; set; } = true;
        public bool Jacket { get; set; } = true;
        public bool SleeveLeft { get; set; } = true;
        public bool SleeveRight { get; set; } = true;
        public bool PantsLeft { get; set; } = true;
        public bool PantsRight { get; set; } = true;
    }

    internal sealed class LocalizationTable
    {
        [TomlPrecedingComment("$Main.Advanced.language$")]
        public string Language { get; set; } = DMCBK.Core.Localization.UiCulture.Auto;

        // Obsolete, superseded by Language.
        // Nullable, and therefore absent from a newly generated file: it exists only so an existing client.toml that still carries the key parses without an unknown-key warning, and so `false` can be read one last time as Language = "en".
        [TomlPrecedingComment("$Main.Advanced.LoadMccTrans$")]
        public bool? LoadMccTranslation { get; set; }

        [TomlPrecedingComment("$Main.Advanced.load_resourcepack_translations$")]
        public bool LoadResourcePackTranslations { get; set; } = true;

        [TomlPrecedingComment("$Main.Advanced.resourcepack_policy$")]
        public string ResourcePackPolicy { get; set; } = "accept";

        [TomlPrecedingComment("$Main.Advanced.load_forge_mod_translations$")]
        public bool LoadForgeModTranslations { get; set; }

        [TomlPrecedingComment("$Main.Advanced.auto_discover_forge_mod_translation_sources$")]
        public bool AutoDiscoverForgeModTranslationSources { get; set; } = true;

        [TomlPrecedingComment("$Main.Advanced.forge_mod_translation_path$")]
        public string ForgeModTranslationPath { get; set; } = string.Empty;
    }

    internal sealed class DiagnosticsTable
    {
        [TomlPrecedingComment("$Diagnostics.Enabled$")]
        public bool Enabled { get; set; } = true;

        [TomlPrecedingComment("$Diagnostics.CapturePackets$")]
        public bool CapturePackets { get; set; } = true;

        [TomlPrecedingComment("$Diagnostics.MaxCaptureMegabytes$")]
        public int MaxCaptureMegabytes { get; set; } = 20;

        [TomlPrecedingComment("$Diagnostics.KeepSessions$")]
        public int KeepSessions { get; set; } = 5;
    }

    internal sealed class PluginsTable
    {
        [TomlPrecedingComment("$Mcc.plugins.crash_threshold$")]
        public int CrashThreshold { get; set; } = 10;

        [TomlPrecedingComment("$Mcc.plugins.crash_window_seconds$")]
        public int CrashWindowSeconds { get; set; } = 60;
    }

    internal sealed class LoggingTable
    {
        [TomlPrecedingComment("$Logging.DebugMessages$")]
        public bool DebugMessages { get; set; }

        [TomlPrecedingComment("$Logging.PacketDebugMessages$")]
        public bool PacketDebugMessages { get; set; }

        [TomlPrecedingComment("$Logging.PacketDebugExclusions$")]
        public List<string> PacketDebugExclusions { get; set; } = new();

        [TomlPrecedingComment("$Logging.ChatMessages$")]
        public bool ChatMessages { get; set; } = true;

        [TomlPrecedingComment("$Logging.InfoMessages$")]
        public bool InfoMessages { get; set; } = true;

        [TomlPrecedingComment("$Logging.WarningMessages$")]
        public bool WarningMessages { get; set; } = true;

        [TomlPrecedingComment("$Logging.ErrorMessages$")]
        public bool ErrorMessages { get; set; } = true;

        [TomlPrecedingComment("$Logging.ChatFilter$")]
        public string ChatFilterRegex { get; set; } = ".*";

        [TomlPrecedingComment("$Logging.DebugFilter$")]
        public string DebugFilterRegex { get; set; } = ".*";

        [TomlPrecedingComment("$Logging.FilterMode$")]
        public string FilterMode { get; set; } = "disable";

        [TomlPrecedingComment("$Logging.LogToFile$")]
        public bool LogToFile { get; set; }

        [TomlPrecedingComment("$Logging.LogFile$")]
        public string LogFile { get; set; } = "console-log.txt";

        [TomlPrecedingComment("$Logging.PrependTimestamp$")]
        public bool PrependTimestamp { get; set; }

        [TomlPrecedingComment("$Logging.SaveColorCodes$")]
        public bool SaveColorCodes { get; set; }

        [TomlPrecedingComment("$Main.Advanced.chatbot_log_file$")]
        public string ChatbotLogFile { get; set; } = string.Empty;
    }

    internal sealed class PermissionsTable
    {
        [TomlPrecedingComment("$Main.Advanced.internal_cmd_char$")]
        public string CommandPrefix { get; set; } = "slash";

        [TomlPrecedingComment("$Main.Advanced.bot_owners$")]
        public List<string> BotOwners { get; set; } = new() { "Player1", "Player2" };
    }
}
