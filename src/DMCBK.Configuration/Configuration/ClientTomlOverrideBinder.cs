using System.Globalization;
using DMCBK.Core.Configuration.Toml;

namespace DMCBK.Core.Configuration;

/// <summary>
/// Applies <c>--section.setting=value</c> dotted overrides onto a mutable <see cref="ClientTomlFile"/> before validation, so overrides flow through the same clamps/derivations as file values.
/// Path matching is case-insensitive.
/// Unknown paths are warn-and-ignored (the documented tolerance policy).
/// The bound paths are a documented pragmatic subset (see the phase report); enums and numbers are parsed leniently by the validator that runs afterward.
/// </summary>
internal static class ClientTomlOverrideBinder
{
    // path (lowercased, dots) -> setter over the file model.
    // Values are stored as strings; the validator parses/clamps them exactly as it does file values.
    private static readonly IReadOnlyDictionary<string, Action<ClientTomlFile, string>> Setters = Build();

    /// <summary>The documented bindable paths (lowercased), for help text and tests.</summary>
    public static IReadOnlyCollection<string> BindablePaths => (IReadOnlyCollection<string>)Setters.Keys;

    public static void Apply(ClientTomlFile model, string path, string value, List<ConfigurationWarning> warnings)
    {
        // Hyphens are dropped, so a setting the file spells AutoConnect can be written --connection.auto-connect=false on the command line, which is how it reads in the manual.
        string key = path.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal);
        if (Setters.TryGetValue(key, out Action<ClientTomlFile, string>? setter))
            setter(model, value);
        else
            warnings.Add(new ConfigurationWarning($"Unknown override '--{path}=...'; ignored."));
    }

    private static Dictionary<string, Action<ClientTomlFile, string>> Build() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["connection.host"] = (m, v) => m.Connection.Host = v,
        ["connection.port"] = (m, v) => m.Connection.Port = ParseInt(v, m.Connection.Port),
        ["connection.version"] = (m, v) => m.Connection.Version = v,
        ["connection.srvresolve"] = (m, v) => m.Connection.SrvResolve = v,
        ["connection.tcptimeout"] = (m, v) => m.Connection.TcpTimeout = ParseInt(v, m.Connection.TcpTimeout),
        ["connection.brand"] = (m, v) => m.Connection.Brand = v,
        ["connection.autoconnect"] = (m, v) => m.Connection.AutoConnect = ParseBool(v, m.Connection.AutoConnect),
        ["connection.reconnect.maxattempts"] = (m, v) => m.Connection.Reconnect.MaxAttempts = ParseInt(v, m.Connection.Reconnect.MaxAttempts),
        ["connection.reconnect.delayseconds"] = (m, v) => m.Connection.Reconnect.DelaySeconds = ParseDouble(v, m.Connection.Reconnect.DelaySeconds),
        ["connection.reconnect.backofffactor"] = (m, v) => m.Connection.Reconnect.BackoffFactor = ParseDouble(v, m.Connection.Reconnect.BackoffFactor),
        ["connection.reconnect.maxdelayseconds"] = (m, v) => m.Connection.Reconnect.MaxDelaySeconds = ParseDouble(v, m.Connection.Reconnect.MaxDelaySeconds),

        ["gameplay.terrain"] = (m, v) => m.Gameplay.Terrain = ParseBool(v, m.Gameplay.Terrain),
        ["gameplay.inventory"] = (m, v) => m.Gameplay.Inventory = ParseBool(v, m.Gameplay.Inventory),
        ["gameplay.entity"] = (m, v) => m.Gameplay.Entity = ParseBool(v, m.Gameplay.Entity),
        ["gameplay.physics"] = (m, v) => m.Gameplay.Physics = ParseBool(v, m.Gameplay.Physics),
        ["gameplay.pathfinding"] = (m, v) => m.Gameplay.Pathfinding = ParseBool(v, m.Gameplay.Pathfinding),
        ["gameplay.autorespawn"] = (m, v) => m.Gameplay.AutoRespawn = ParseBool(v, m.Gameplay.AutoRespawn),
        ["gameplay.moveheadwhilewalking"] = (m, v) => m.Gameplay.MoveHeadWhileWalking = ParseBool(v, m.Gameplay.MoveHeadWhileWalking),
        ["gameplay.movementspeed"] = (m, v) => m.Gameplay.MovementSpeed = ParseInt(v, m.Gameplay.MovementSpeed),
        ["gameplay.temporaryfixbadpacket"] = (m, v) => m.Gameplay.TemporaryFixBadpacket = ParseBool(v, m.Gameplay.TemporaryFixBadpacket),
        ["gameplay.ignoreinvalidplayername"] = (m, v) => m.Gameplay.IgnoreInvalidPlayerName = ParseBool(v, m.Gameplay.IgnoreInvalidPlayerName),
        ["gameplay.showeffectmessages"] = (m, v) => m.Gameplay.ShowEffectMessages = ParseBool(v, m.Gameplay.ShowEffectMessages),

        ["chat.messagecooldown"] = (m, v) => m.Chat.MessageCooldown = ParseDouble(v, m.Chat.MessageCooldown),
        ["chat.maxchatmessagelength"] = (m, v) => m.Chat.MaxChatMessageLength = ParseInt(v, m.Chat.MaxChatMessageLength),
        ["chat.privatemessagecommand"] = (m, v) => m.Chat.PrivateMessageCommand = v,

        ["clientsettings.enabled"] = (m, v) => m.ClientSettings.Enabled = ParseBool(v, m.ClientSettings.Enabled),
        ["clientsettings.locale"] = (m, v) => m.ClientSettings.Locale = v,
        ["clientsettings.renderdistance"] = (m, v) => m.ClientSettings.RenderDistance = ParseInt(v, m.ClientSettings.RenderDistance),
        ["clientsettings.difficulty"] = (m, v) => m.ClientSettings.Difficulty = v,
        ["clientsettings.chatmode"] = (m, v) => m.ClientSettings.ChatMode = v,
        ["clientsettings.chatcolors"] = (m, v) => m.ClientSettings.ChatColors = ParseBool(v, m.ClientSettings.ChatColors),
        ["clientsettings.mainhand"] = (m, v) => m.ClientSettings.MainHand = v,

        ["localization.language"] = (m, v) => m.Localization.Language = v,
        // Obsolete, still bindable so a scripted run that passes it does not fail on an unknown key.
        ["localization.loadmcctranslation"] = (m, v) => m.Localization.LoadMccTranslation = ParseBool(v, m.Localization.LoadMccTranslation ?? true),

        ["logging.debugmessages"] = (m, v) => m.Logging.DebugMessages = ParseBool(v, m.Logging.DebugMessages),
        ["logging.packetdebugmessages"] = (m, v) => m.Logging.PacketDebugMessages = ParseBool(v, m.Logging.PacketDebugMessages),
        ["logging.chatmessages"] = (m, v) => m.Logging.ChatMessages = ParseBool(v, m.Logging.ChatMessages),
        ["logging.infomessages"] = (m, v) => m.Logging.InfoMessages = ParseBool(v, m.Logging.InfoMessages),
        ["logging.warningmessages"] = (m, v) => m.Logging.WarningMessages = ParseBool(v, m.Logging.WarningMessages),
        ["logging.errormessages"] = (m, v) => m.Logging.ErrorMessages = ParseBool(v, m.Logging.ErrorMessages),
        ["logging.logtofile"] = (m, v) => m.Logging.LogToFile = ParseBool(v, m.Logging.LogToFile),
        ["logging.logfile"] = (m, v) => m.Logging.LogFile = v,
        ["logging.filtermode"] = (m, v) => m.Logging.FilterMode = v,
        ["logging.prependtimestamp"] = (m, v) => m.Logging.PrependTimestamp = ParseBool(v, m.Logging.PrependTimestamp),
        ["logging.savecolorcodes"] = (m, v) => m.Logging.SaveColorCodes = ParseBool(v, m.Logging.SaveColorCodes),

        ["permissions.commandprefix"] = (m, v) => m.Permissions.CommandPrefix = v,
    };

    private static bool ParseBool(string value, bool fallback) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => fallback,
    };

    private static int ParseInt(string value, int fallback)
        => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    private static double ParseDouble(string value, double fallback)
        => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
}
