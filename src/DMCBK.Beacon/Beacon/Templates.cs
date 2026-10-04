namespace DMCBK.Core.Beacon;

/// <summary>
/// The <c>new</c> scaffolds: working commented scripts, one per template.
/// Each template runs as-is; none starts from a blank file.
/// Every scaffold lints clean (canonical end labels, no forgiven spellings, exact manifest cover) and runs on fakes; <c>TemplateTests</c> pins both.
/// <para/>
/// Templates deliberately stay inside the interpreter's runtime surface (<c>say</c>/<c>show</c>, comparisons, <c>saved</c>).
/// The <c>inv.*</c> calls raise <c>B2002</c> at runtime, so a scaffold that used them would lint clean and then fail its first run.
/// </summary>
public static class BeaconTemplates
{
    /// <summary>Greeter: remembers first-timers versus regulars in saved state.</summary>
    public const string Welcome =
        "# beacon 1\n"
        + "# needs: chat.send\n"
        + "\n"
        + "# Welcome bot: greets every player who joins.\n"
        + "# Run it with: /scripts run welcome\n"
        + "\n"
        + "on join:\n"
        + "  set seen to saved(\"seen\") or {}\n"
        + "  if seen[player] is set then\n"
        + "    say \"Welcome back, {player}!\"\n"
        + "  else\n"
        + "    say \"Welcome for the first time, {player}! Type !rules to start.\"\n"
        + "    set seen[player] to yes\n"
        + "  end if\n"
        + "  save \"seen\" to seen\n"
        + "end on\n";

    /// <summary>Vitals guard: warns in chat when health or food drops.</summary>
    public const string Guard =
        "# beacon 1\n"
        + "# needs: chat.send\n"
        + "\n"
        + "# Guard: watches vitals and warns in chat when they drop.\n"
        + "# Run it with: /scripts run guard\n"
        + "\n"
        + "on health when health < 6:\n"
        + "  say \"Low health! Eat something soon.\"\n"
        + "end on\n"
        + "\n"
        + "on hunger when food < 6:\n"
        + "  say \"Getting hungry. Time for a snack.\"\n"
        + "end on\n";

    /// <summary>Shopkeeper: answers <c>!price</c> questions from its saved ledger.</summary>
    public const string Shop =
        "# beacon 1\n"
        + "# needs: chat.send\n"
        + "\n"
        + "# Shopkeeper: answers !price questions from its saved ledger.\n"
        + "# The ledger starts empty, so every item is unlisted until a seller script saves prices.\n"
        + "# Run it with: /scripts run shop\n"
        + "\n"
        + "on chat when message starts with \"!price \":\n"
        + "  set item to trim(slice(message, 7))\n"
        + "  set prices to saved(\"prices\") or {}\n"
        + "  set cost to prices[item]\n"
        + "  if cost is set then\n"
        + "    say \"{item} costs {cost} coins.\"\n"
        + "  else\n"
        + "    say \"Sorry, I do not sell {item}.\"\n"
        + "  end if\n"
        + "end on\n";

    /// <summary>Empty: a starting point with a load announcement and nothing wired up.</summary>
    public const string Empty =
        "# beacon 1\n"
        + "\n"
        + "# Empty script: a starting point with nothing wired up yet.\n"
        + "# Add your own on blocks below, then run it with: /scripts run empty\n"
        + "\n"
        + "on start:\n"
        + "  show \"Empty script loaded. Add your own on blocks above this line.\"\n"
        + "end on\n";

    /// <summary>Template names in picker order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["empty", "guard", "shop", "welcome"];

    /// <summary>Returns the scaffold source for <paramref name="name"/> (case-insensitive).</summary>
    /// <exception cref="ArgumentException">Thrown for an unknown template name.</exception>
    public static string Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().ToLowerInvariant() switch
        {
            "welcome" => Welcome,
            "guard" => Guard,
            "shop" => Shop,
            "empty" => Empty,
            _ => throw new ArgumentException(
                $"Unknown Beacon template '{name}'. Available templates: {string.Join(", ", Names)}.",
                nameof(name)),
        };
    }

    /// <summary>
    /// Writes a scaffold into <paramref name="scriptsDir"/> as <c>&lt;id&gt;.bcn</c>.
    /// Refuses to overwrite; validates the id is a safe file name.
    /// </summary>
    public static bool TryWriteNew(
        string scriptsDir, string template, string id, out string path, out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDir);
        path = string.Empty;
        error = string.Empty;

        string name;
        try
        {
            name = Get(template);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id.Contains('/') || id.Contains('\\') || id.StartsWith('.'))
        {
            error = $"The script id '{id}' is not a safe file name. Use letters, digits, '-' and '_'.";
            return false;
        }

        string file = id.Trim() + BeaconScriptDiscovery.ScriptExtension;
        path = Path.Combine(scriptsDir, file);
        if (File.Exists(path))
        {
            error = $"The script '{file}' already exists; delete it first or pick another id.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(scriptsDir);
            File.WriteAllText(path, name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not write '{path}': {ex.Message}";
            return false;
        }
    }
}
