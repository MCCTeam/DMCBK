using System.Text.RegularExpressions;
using DMCBK.Core.Configuration.Toml;
using DMCBK.Core.Localization;

namespace DMCBK.Core.Configuration;

/// <summary>
/// Pure validation/derivation: folds the mutable TOML file models into an immutable <see cref="DmcbkConfiguration"/> snapshot, re-homing the legacy <c>OnSettingUpdate</c> clamps and derivations (cooldown/timeout/movement clamps, language normalization, badpacket-forces-terrain, account/server resolution) as pure functions with no session state.
/// Recoverable problems become <see cref="ConfigurationWarning"/>s, never exceptions.
/// </summary>
internal static partial class ConfigurationValidation
{
    public const string DefaultLanguage = Localization.UiCulture.Auto;

    public static ConfigurationValidationResult Validate(
        ClientTomlFile client,
        AccountsTomlFile accounts,
        ServersTomlFile servers,
        string? sourceFolder)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(servers);

        var warnings = new List<ConfigurationWarning>();

        ConnectionConfig connection = ValidateConnection(client.Connection, warnings);
        GameplayConfig gameplay = ValidateGameplay(client.Gameplay, warnings);
        ChatConfig chat = ValidateChat(client.Chat, warnings);
        ClientSettingsConfig clientSettings = ValidateClientSettings(client.ClientSettings, warnings);
        LocalizationConfig localization = ValidateLocalization(client.Localization, warnings);
        LoggingConfig logging = ValidateLogging(client.Logging);
        PermissionsConfig permissions = ValidatePermissions(client.Permissions, warnings);
        var variables = new Dictionary<string, string>(client.Variables);

        AccountsConfig accountsConfig = ValidateAccounts(accounts, warnings);
        ProxyConfig proxy = ValidateProxy(accounts.Proxy, warnings);
        ServersConfig serversConfig = ValidateServers(servers, warnings);

        ConfiguredAccount resolvedAccount = ResolveAccount(accountsConfig, warnings);
        (string host, ushort port, string version) = ResolveServer(connection, serversConfig, warnings);

        var config = new DmcbkConfiguration
        {
            Connection = connection,
            Gameplay = gameplay,
            Chat = chat,
            ClientSettings = clientSettings,
            Localization = localization,
            Logging = logging,
            Diagnostics = new DiagnosticsConfig
            {
                Enabled = client.Diagnostics.Enabled,
                CapturePackets = client.Diagnostics.CapturePackets,

                // Keep diagnostics small enough to attach to a report even when the configured values are stale or mistyped.
                // CapturePackets is the explicit way to disable packet recording.
                MaxCaptureMegabytes = Math.Clamp(client.Diagnostics.MaxCaptureMegabytes, 1, 20),
                KeepSessions = Math.Clamp(client.Diagnostics.KeepSessions, 1, 5),
            },
            Plugins = new PluginsConfig
            {
                CrashThreshold = client.Plugins.CrashThreshold,
                CrashWindowSeconds = client.Plugins.CrashWindowSeconds,
            },
            Permissions = permissions,
            Variables = variables,
            Accounts = accountsConfig,
            Proxy = proxy,
            Servers = serversConfig,
            ResolvedHost = host,
            ResolvedPort = port,
            ResolvedVersion = version,
            ResolvedAccount = resolvedAccount,
            SourceFolder = sourceFolder,
        };

        return new ConfigurationValidationResult(config, warnings);
    }

    private static ConnectionConfig ValidateConnection(ClientTomlFile.ConnectionTable table, List<ConfigurationWarning> warnings)
    {
        // A host may carry an inline ":port" (legacy SetServerIP behavior); split it out.
        string host = table.Host.Trim();
        ushort? inlinePort = null;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && colon < host.Length - 1 && ushort.TryParse(host[(colon + 1)..], out ushort parsedInline))
        {
            inlinePort = parsedInline;
            host = host[..colon];
        }

        ushort port = ClampPort(table.Port, warnings, "Connection.Port");
        if (inlinePort is not null)
            port = inlinePort.Value;

        int timeout = table.TcpTimeout;
        if (timeout < 1)
        {
            warnings.Add(new ConfigurationWarning($"Connection.TcpTimeout {timeout} is below 1; clamped to 1."));
            timeout = 1;
        }

        return new ConnectionConfig
        {
            Host = host,
            Port = port,
            Version = string.IsNullOrWhiteSpace(table.Version) ? "auto" : table.Version.Trim(),
            SrvResolve = ParseEnum(table.SrvResolve, SrvResolveMode.Fast, warnings, "Connection.SrvResolve"),
            TcpTimeoutSeconds = timeout,
            Brand = ParseEnum(table.Brand, BrandKind.Mcc, warnings, "Connection.Brand"),
            AutoConnect = table.AutoConnect,
            Reconnect = ValidateReconnect(table.Reconnect, warnings),
        };
    }

    private static ReconnectConfig ValidateReconnect(ClientTomlFile.ReconnectTable table, List<ConfigurationWarning> warnings)
    {
        double delay = table.DelaySeconds;
        if (delay < 0)
        {
            warnings.Add(new ConfigurationWarning($"Connection.Reconnect.DelaySeconds {delay} is below 0; clamped to 0."));
            delay = 0;
        }

        double backoff = table.BackoffFactor;
        if (backoff < 1)
        {
            warnings.Add(new ConfigurationWarning($"Connection.Reconnect.BackoffFactor {backoff} is below 1; clamped to 1."));
            backoff = 1;
        }

        double maxDelay = table.MaxDelaySeconds;
        if (maxDelay < delay)
        {
            warnings.Add(new ConfigurationWarning(
                $"Connection.Reconnect.MaxDelaySeconds {maxDelay} is below DelaySeconds {delay}; clamped to {delay}."));
            maxDelay = delay;
        }

        return new ReconnectConfig
        {
            MaxAttempts = table.MaxAttempts,
            DelaySeconds = delay,
            BackoffFactor = backoff,
            MaxDelaySeconds = maxDelay,
            RetryOnKick = table.RetryOnKick,
        };
    }

    private static GameplayConfig ValidateGameplay(ClientTomlFile.GameplayTable table, List<ConfigurationWarning> warnings)
    {
        bool terrain = table.Terrain;

        // Re-homed derivation: TemporaryFixBadpacket requires terrain handling.
        if (table.TemporaryFixBadpacket && !terrain)
        {
            terrain = true;
            warnings.Add(new ConfigurationWarning(
                "Gameplay.TemporaryFixBadpacket is on; forcing Gameplay.Terrain on (it depends on terrain handling)."));
        }

        // The inverse derivation, and it runs DOWNWARD on purpose: UMPK's own ClientFeatures.Normalized() (consulted at UmpkClientBuilder.Build()) resolves the same Terrain/Physics/Pathfinding dependency UPWARD instead (physics implies terrain; pathfinding implies physics), because its caller opts INTO one capability at a time and a defaulted-off key should stay off.
        // A TOML file is different: every key carries an explicit value, so an explicit Terrain = false sitting beside a defaulted Physics/Pathfinding = true can only mean terrain (and everything that depends on it) is off.
        // Left undone, that composition reached UMPK's builder as Physics/Pathfinding = true with Terrain = false, which Normalized() silently flips back ON for the engine while the command gates (which read the raw, un-normalized composition) still refuse on "terrain is disabled" - an incoherent client that half-believes terrain is on.
        bool physics = table.Physics;
        bool pathfinding = table.Pathfinding;
        if (!terrain && (physics || pathfinding))
        {
            physics = false;
            pathfinding = false;
            warnings.Add(new ConfigurationWarning(
                "Gameplay.Terrain is off; forcing Gameplay.Physics and Gameplay.Pathfinding off "
                + "(both require terrain)."));
        }

        int movementSpeed = table.MovementSpeed;
        if (movementSpeed < 1)
        {
            warnings.Add(new ConfigurationWarning($"Gameplay.MovementSpeed {movementSpeed} is below 1; clamped to 1."));
            movementSpeed = 1;
        }

        return new GameplayConfig
        {
            Terrain = terrain,
            Inventory = table.Inventory,
            Entity = table.Entity,
            Physics = physics,
            Pathfinding = pathfinding,
            AutoRespawn = table.AutoRespawn,
            MoveHeadWhileWalking = table.MoveHeadWhileWalking,
            MovementSpeed = movementSpeed,
            TemporaryFixBadpacket = table.TemporaryFixBadpacket,
            IgnoreInvalidPlayerName = table.IgnoreInvalidPlayerName,
            ShowEffectMessages = table.ShowEffectMessages,
        };
    }

    private static ChatConfig ValidateChat(ClientTomlFile.ChatTable table, List<ConfigurationWarning> warnings)
    {
        double cooldown = table.MessageCooldown;
        if (cooldown < 0)
        {
            warnings.Add(new ConfigurationWarning($"Chat.MessageCooldown {cooldown} is below 0; clamped to 0."));
            cooldown = 0;
        }

        int maxLength = table.MaxChatMessageLength;
        if (maxLength < 0)
        {
            warnings.Add(new ConfigurationWarning($"Chat.MaxChatMessageLength {maxLength} is below 0; clamped to 0."));
            maxLength = 0;
        }
        else if (maxLength > 32767)
        {
            warnings.Add(new ConfigurationWarning($"Chat.MaxChatMessageLength {maxLength} exceeds 32767; clamped to 32767."));
            maxLength = 32767;
        }

        return new ChatConfig
        {
            MessageCooldownSeconds = cooldown,
            MaxChatMessageLength = maxLength,
            PrivateMessageCommand = string.IsNullOrWhiteSpace(table.PrivateMessageCommand)
                ? "tell"
                : table.PrivateMessageCommand.Trim(),
            Format = new ChatFormatConfig
            {
                Builtins = table.Format.Builtins,
                UserDefined = table.Format.UserDefined,
                Public = table.Format.Public,
                Private = table.Format.Private,
                TeleportRequest = table.Format.TeleportRequest,
            },
            Signature = new SignatureConfig
            {
                LoginWithSecureProfile = table.Signature.LoginWithSecureProfile,
                MarkLegallySignedMsg = table.Signature.MarkLegallySignedMsg,
                MarkModifiedMsg = table.Signature.MarkModifiedMsg,
                MarkIllegallySignedMsg = table.Signature.MarkIllegallySignedMsg,
                MarkUnverifiedMsg = table.Signature.MarkUnverifiedMsg,
                MarkInsecureMsg = table.Signature.MarkInsecureMsg,
                MarkSystemMessage = table.Signature.MarkSystemMessage,
                ShowModifiedChat = table.Signature.ShowModifiedChat,
                ShowIllegalSignedChat = table.Signature.ShowIllegalSignedChat,
            },
        };
    }

    private static ClientSettingsConfig ValidateClientSettings(ClientTomlFile.ClientSettingsTable table, List<ConfigurationWarning> warnings)
    {
        int render = table.RenderDistance;
        if (render is < 0 or > 255)
        {
            warnings.Add(new ConfigurationWarning($"ClientSettings.RenderDistance {render} is out of 0..255; clamped."));
            render = Math.Clamp(render, 0, 255);
        }

        return new ClientSettingsConfig
        {
            Enabled = table.Enabled,
            Locale = string.IsNullOrWhiteSpace(table.Locale) ? "en_US" : table.Locale.Trim(),
            RenderDistance = (byte)render,
            Difficulty = ParseEnum(table.Difficulty, DifficultyKind.Peaceful, warnings, "ClientSettings.Difficulty"),
            ChatMode = ParseEnum(table.ChatMode, ChatModeKind.Enabled, warnings, "ClientSettings.ChatMode"),
            ChatColors = table.ChatColors,
            MainHand = ParseEnum(table.MainHand, MainHandKind.Left, warnings, "ClientSettings.MainHand"),
            Skin = new SkinConfig
            {
                Cape = table.Skin.Cape,
                Hat = table.Skin.Hat,
                Jacket = table.Skin.Jacket,
                SleeveLeft = table.Skin.SleeveLeft,
                SleeveRight = table.Skin.SleeveRight,
                PantsLeft = table.Skin.PantsLeft,
                PantsRight = table.Skin.PantsRight,
            },
        };
    }

    private static LocalizationConfig ValidateLocalization(ClientTomlFile.LocalizationTable table, List<ConfigurationWarning> warnings)
    {
        string language = NormalizeLanguage(table.Language);
        if (language.Length == 0)
        {
            warnings.Add(new ConfigurationWarning(
                $"Localization.Language '{table.Language}' is invalid; using '{DefaultLanguage}'."));
            language = DefaultLanguage;
        }
        else if (!UiCulture.IsAuto(language) && !UiCulture.TryParseTag(language, out _))
        {
            warnings.Add(new ConfigurationWarning(
                $"Localization.Language '{table.Language}' names no known language; using '{DefaultLanguage}'."));
            language = DefaultLanguage;
        }

        // The retired Localization.LoadMccTranslation, read for one release.
        // It was a boolean where a language belongs: true meant "follow the OS UI culture", which is exactly what auto means now, and false meant "pin English".
        // Only false still has to be honoured, and only while the file has not moved on to a language of its own.
        if (table.LoadMccTranslation is false)
        {
            warnings.Add(new ConfigurationWarning(
                "Localization.LoadMccTranslation is obsolete; use Localization.Language ('auto', or a tag"
                + " such as 'pt_br'). Reading LoadMccTranslation = false as Language = 'en'."));
            if (UiCulture.IsAuto(language))
                language = "en";
        }
        else if (table.LoadMccTranslation is true)
        {
            warnings.Add(new ConfigurationWarning(
                "Localization.LoadMccTranslation is obsolete and can be deleted; 'true' meant the same as"
                + " Localization.Language = 'auto', which is the default."));
        }

        return new LocalizationConfig
        {
            Language = language,
            LoadResourcePackTranslations = table.LoadResourcePackTranslations,
            ResourcePackPolicy = ParseEnum(table.ResourcePackPolicy, ResourcePackPolicyMode.Accept, warnings, "Localization.ResourcePackPolicy"),
            LoadForgeModTranslations = table.LoadForgeModTranslations,
            AutoDiscoverForgeModTranslationSources = table.AutoDiscoverForgeModTranslationSources,
            ForgeModTranslationPath = table.ForgeModTranslationPath.Trim(),
        };
    }

    private static LoggingConfig ValidateLogging(ClientTomlFile.LoggingTable table)
    {
        var warnings = new List<ConfigurationWarning>();
        return new LoggingConfig
        {
            DebugMessages = table.DebugMessages,
            PacketDebugMessages = table.PacketDebugMessages,
            PacketDebugExclusions = table.PacketDebugExclusions.ToArray(),
            ChatMessages = table.ChatMessages,
            InfoMessages = table.InfoMessages,
            WarningMessages = table.WarningMessages,
            ErrorMessages = table.ErrorMessages,
            ChatFilterRegex = table.ChatFilterRegex,
            DebugFilterRegex = table.DebugFilterRegex,
            FilterMode = ParseEnum(table.FilterMode, LogFilterMode.Disable, warnings, "Logging.FilterMode"),
            LogToFile = table.LogToFile,
            LogFile = table.LogFile,
            PrependTimestamp = table.PrependTimestamp,
            SaveColorCodes = table.SaveColorCodes,
            ChatbotLogFile = table.ChatbotLogFile,
        };
    }

    private static PermissionsConfig ValidatePermissions(ClientTomlFile.PermissionsTable table, List<ConfigurationWarning> warnings)
        => new()
        {
            CommandPrefix = ParseEnum(table.CommandPrefix, InternalCommandPrefix.Slash, warnings, "Permissions.CommandPrefix"),
            BotOwners = table.BotOwners.Select(o => o.ToLowerInvariant()).ToArray(),
        };

    private static AccountsConfig ValidateAccounts(AccountsTomlFile file, List<ConfigurationWarning> warnings)
    {
        var accounts = new List<ConfiguredAccount>(file.Account.Count);
        foreach (AccountsTomlFile.AccountEntry entry in file.Account)
        {
            accounts.Add(new ConfiguredAccount
            {
                Name = entry.Name,
                Kind = ParseAccountKind(entry.Kind, warnings, entry.Name),
                Login = entry.Login,
                AuthServer = string.IsNullOrWhiteSpace(entry.AuthServer) ? null : entry.AuthServer.Trim(),
            });
        }

        return new AccountsConfig
        {
            ActiveAccount = file.Active,
            Accounts = accounts,
            SessionCache = ParseEnum(file.SessionCache, CacheMode.Disk, warnings, "Accounts.SessionCache"),
            ProfileKeyCache = ParseEnum(file.ProfileKeyCache, CacheMode.Disk, warnings, "Accounts.ProfileKeyCache"),
            CacheDirectory = string.IsNullOrWhiteSpace(file.CacheDirectory) ? "cache" : file.CacheDirectory.Trim(),
        };
    }

    private static ProxyConfig ValidateProxy(AccountsTomlFile.ProxyTable table, List<ConfigurationWarning> warnings)
        => new()
        {
            EnabledLogin = table.EnabledLogin,
            EnabledIngame = table.EnabledIngame,
            Host = table.Host,
            Port = ClampPort(table.Port, warnings, "Proxy.Port"),
            Kind = ParseEnum(table.Kind, ProxyKind.Http, warnings, "Proxy.Kind"),
            Username = table.Username,
            Password = table.Password,
        };

    private static ServersConfig ValidateServers(ServersTomlFile file, List<ConfigurationWarning> warnings)
    {
        var servers = new List<ConfiguredServer>(file.Server.Count);
        foreach (ServersTomlFile.ServerEntry entry in file.Server)
        {
            servers.Add(new ConfiguredServer
            {
                Name = entry.Name,
                Host = entry.Host.Trim(),
                Port = ClampPort(entry.Port, warnings, $"Server '{entry.Name}' port"),
                Version = string.IsNullOrWhiteSpace(entry.Version) ? "auto" : entry.Version.Trim(),
                Kind = ParseEnum(entry.Kind, ConfiguredServerKind.Normal, warnings, $"Server '{entry.Name}' kind"),
                RealmWorld = entry.RealmWorld.Trim(),
            });
        }

        return new ServersConfig
        {
            ActiveServer = file.Active,
            Servers = servers,
        };
    }

    private static ConfiguredAccount ResolveAccount(AccountsConfig accounts, List<ConfigurationWarning> warnings)
    {
        if (accounts.Accounts.Count == 0)
            return new ConfiguredAccount();

        if (!string.IsNullOrWhiteSpace(accounts.ActiveAccount))
        {
            ConfiguredAccount? match = accounts.Accounts
                .FirstOrDefault(a => string.Equals(a.Name, accounts.ActiveAccount, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;

            warnings.Add(new ConfigurationWarning(
                $"Active account '{accounts.ActiveAccount}' not found; using the first account '{accounts.Accounts[0].Name}'."));
        }

        return accounts.Accounts[0];
    }

    private static (string Host, ushort Port, string Version) ResolveServer(
        ConnectionConfig connection, ServersConfig servers, List<ConfigurationWarning> warnings)
    {
        if (!string.IsNullOrWhiteSpace(servers.ActiveServer))
        {
            ConfiguredServer? match = servers.Servers
                .FirstOrDefault(s => string.Equals(s.Name, servers.ActiveServer, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return (match.Host, match.Port, match.Version);

            warnings.Add(new ConfigurationWarning(
                $"Active server '{servers.ActiveServer}' not found; using the client.toml connection block."));
        }

        return (connection.Host, connection.Port, connection.Version);
    }

    internal static string NormalizeLanguage(string language)
    {
        string cleaned = LanguageSanitizer().Replace(language ?? string.Empty, string.Empty).Replace('-', '_');
        return cleaned.ToLowerInvariant();
    }

    internal static DmcbkAccountKind ParseAccountKind(string value, List<ConfigurationWarning> warnings, string accountName)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "offline":
                return DmcbkAccountKind.Offline;
            case "microsoft":
            case "microsoft-devicecode":
            case "device-code":
            case "devicecode":
                return DmcbkAccountKind.MicrosoftDeviceCode;
            case "microsoft-browser":
            case "browser":
                return DmcbkAccountKind.MicrosoftBrowser;
            case "yggdrasil":
                return DmcbkAccountKind.Yggdrasil;
            default:
                warnings.Add(new ConfigurationWarning(
                    $"Account '{accountName}' has unknown kind '{value}'; using 'offline'."));
                return DmcbkAccountKind.Offline;
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback, List<ConfigurationWarning> warnings, string label)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse(value?.Trim(), ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed))
            return parsed;

        warnings.Add(new ConfigurationWarning($"{label} has unknown value '{value}'; using '{fallback}'."));
        return fallback;
    }

    private static ushort ClampPort(int port, List<ConfigurationWarning> warnings, string label)
    {
        if (port is >= 0 and <= ushort.MaxValue)
            return (ushort)port;

        warnings.Add(new ConfigurationWarning($"{label} {port} is out of 0..65535; using 25565."));
        return 25565;
    }

    [GeneratedRegex(@"[^-_\w*\d]")]
    private static partial Regex LanguageSanitizer();
}

/// <summary>The result of validating configuration: the immutable snapshot plus any recoverable warnings.</summary>
internal sealed record ConfigurationValidationResult(
    DmcbkConfiguration Config,
    IReadOnlyList<ConfigurationWarning> Warnings);
