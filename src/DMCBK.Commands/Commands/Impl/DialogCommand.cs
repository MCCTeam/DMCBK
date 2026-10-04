using System.Globalization;
using System.Text;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.Core.Localization;
using Umpk.Client.Actions;
using Umpk.Client.Navigation;
using Umpk.Commands;
using Umpk.Geometry;

namespace DMCBK.Core.Commands.Impl;

/// <summary>
/// The <c>dialog</c> command: show and answer the server dialog the dialog state tracks (1.21.6+).
/// Ported from the legacy <c>MinecraftClient/Commands/Dialog.cs</c> and the <c>DialogManager</c> / <c>DialogFormatter</c> behind it, including the <c>open</c>, <c>input</c> and <c>click-label</c> grammar, the 1-based button numbering, the per-kind input validation, and the boxed rendering.
/// <para>
/// The staged input values live here, not in core state: they are a half-composed answer belonging to this text interaction, and the server has no notion of them until a button is pressed.
/// Legacy kept them on its own <c>DialogInstance</c> for the same reason.
/// They are cleared whenever a dialog is submitted or cancelled, and only the keys the shown dialog declares are ever submitted, so a later dialog never inherits an earlier one's answers.
/// The command instance is shared by the dispatcher, so the map is guarded.
/// </para>
/// </summary>
public sealed class DialogCommand : CommandBase
{
    /// <summary>The width of the box legacy drew around a rendered dialog (DialogFormatter.cs:18).</summary>
    private const int BoxWidth = 50;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _staged = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override string CmdName => "dialog";

    /// <inheritdoc/>
    public override string CmdDesc => McStrings.Get("cmd.dialog.desc");

    /// <inheritdoc/>
    public override string CmdUsage => McStrings.Get("cmd.dialog.usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Interaction;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("show", "print the dialog the server is showing"),
        new("open", "open it in the host UI"),
        new("set <input> <value>", "fill one input"),
        new("click <index>", "press a button by position"),
        new("click-label <label>", "press a button by its text"),
        new("cancel", "cancel the dialog"),
        new("dismiss", "dismiss it without answering"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["dialog show", "dialog click 1"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["book"];

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Show(ctx.Source))
            .ThenLiteral("show", h => h.Executes(ctx => Show(ctx.Source)))
            .ThenLiteral("open", h => h.Executes(ctx => Open(ctx.Source)))
            .ThenLiteral("set", h => h
                .ThenArgument("Input", Arguments.QuotableString(), a => a
                    .ThenArgument("Value", Arguments.GreedyString(), b => b
                        .Executes(ctx => SetInput(
                            ctx.Source, ctx.GetArgument<string>("Input"), ctx.GetArgument<string>("Value"))))))
            .ThenLiteral("input", h => h
                .ThenArgument("Input", Arguments.QuotableString(), a => a
                    .ThenArgument("Value", Arguments.GreedyString(), b => b
                        .Executes(ctx => SetInput(
                            ctx.Source, ctx.GetArgument<string>("Input"), ctx.GetArgument<string>("Value"))))))
            .ThenLiteral("click", h => h
                .ThenArgument("Index", Arguments.Integer(1, int.MaxValue), a => a
                    .Executes(ctx => Click(ctx.Source, ctx.GetArgument<int>("Index")))))
            .ThenLiteral("click-label", h => h
                .ThenArgument("Label", Arguments.GreedyString(), a => a
                    .Executes(ctx => ClickLabel(ctx.Source, ctx.GetArgument<string>("Label")))))
            .ThenLiteral("cancel", h => h.Executes(ctx => Cancel(ctx.Source)))
            .ThenLiteral("dismiss", h => h.Executes(ctx => Dismiss(ctx.Source)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    /// <summary>
    /// Renders a dialog snapshot the way legacy <c>DialogFormatter.Render</c> did (DialogFormatter.cs:20-51): the boxed title, the body lines, the inputs with their current (staged or initial) values, the numbered buttons a <c>dialog click</c> takes, and the help hint.
    /// A dialog the server sent as a registry reference renders legacy's unresolved-dialog title and body, which is "a dialog is open that cannot be rendered" rather than "no dialog".
    /// </summary>
    /// <param name="dialog">The dialog snapshot.</param>
    /// <param name="staged">The input values staged so far, keyed by input key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dialog"/> or <paramref name="staged"/> is null.</exception>
    public static IReadOnlyList<string> FormatDialog(DialogSnapshot dialog, IReadOnlyDictionary<string, string> staged)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(staged);

        string border = new('-', BoxWidth);
        var lines = new List<string> { border, "     " + DisplayTitle(dialog), border };

        foreach (string body in BodyLines(dialog))
        {
            if (!string.IsNullOrWhiteSpace(body))
                lines.Add(body);
        }

        if (dialog.Inputs.Count > 0)
        {
            lines.Add(McStrings.Get("dialog.render.inputs"));
            foreach (DialogInputInfo input in dialog.Inputs)
            {
                string value = staged.TryGetValue(input.Key, out string? edited) ? edited : input.InitialValue;
                lines.Add(McStrings.Format(
                    "dialog.render.input",
                    input.Key,
                    DescribeKind(input.Kind),
                    input.Label ?? string.Empty,
                    value,
                    DescribeInput(input)));
            }
        }

        if (dialog.Buttons.Count > 0)
        {
            lines.Add(McStrings.Get("dialog.render.actions"));
            for (int i = 0; i < dialog.Buttons.Count; i++)
            {
                DialogButtonInfo button = dialog.Buttons[i];
                lines.Add(McStrings.Format("dialog.render.action", i + 1, button.Label, DescribeAction(button)));
            }
        }

        lines.Add(string.Empty);
        lines.Add("§o" + McStrings.Get("dialog.render.help_hint") + "§r");
        lines.Add(border);
        return lines;
    }

    /// <summary>The port of legacy <c>Show</c> (Dialog.cs:53-63).</summary>
    private int Show(CommandContext ctx)
    {
        if (Current(ctx, out DialogSnapshot? dialog) is { } gate)
            return gate;

        foreach (string line in FormatDialog(dialog!, Snapshot()))
            ctx.Output.WriteLine(line);

        return ctx.Result.Set(CmdStatus.Done);
    }

    /// <summary>
    /// The port of legacy <c>Open</c> (Dialog.cs:65-78): hand the dialog to the host overlay.
    /// A host without one answers the way legacy's non-TUI console did.
    /// </summary>
    private int Open(CommandContext ctx)
    {
        if (Current(ctx, out DialogSnapshot? dialog) is { } gate)
            return gate;

        return ctx.Ui?.TryShowDialog(new DialogViewRequest(dialog!, Snapshot())) == true
            ? ctx.Result.Ok(McStrings.Get("dialog.tui_opened"))
            : ctx.Result.Fail(McStrings.Get("dialog.tui_unavailable"));
    }

    /// <summary>The port of legacy <c>DialogManager.SetInput</c> (DialogManager.cs:130-149).</summary>
    private int SetInput(CommandContext ctx, string key, string value)
    {
        if (Current(ctx, out DialogSnapshot? dialog) is { } gate)
            return gate;

        DialogInputInfo? input = null;
        foreach (DialogInputInfo candidate in dialog!.Inputs)
        {
            if (string.Equals(candidate.Key, key, StringComparison.Ordinal))
            {
                input = candidate;
                break;
            }
        }

        if (input is null)
            return ctx.Result.Fail(McStrings.Format("dialog.input_unknown", key));

        string normalized = NormalizeInputValue(input, value, out string? error);
        if (error is not null)
            return ctx.Result.Fail(error);

        lock (_gate)
            _staged[key] = normalized;

        return ctx.Result.Ok(McStrings.Format("dialog.input_set", key, normalized));
    }

    /// <summary>
    /// The port of legacy <c>DialogManager.Click</c> (DialogManager.cs:151-168).
    /// The index is 1-based, as legacy's button numbering and its rendering both were.
    /// </summary>
    private int Click(CommandContext ctx, int index)
    {
        if (Current(ctx, out DialogSnapshot? dialog) is { } gate)
            return gate;

        if (index < 1 || index > dialog!.Buttons.Count)
            return ctx.Result.Fail(McStrings.Format("dialog.action_unknown", index));

        return Press(ctx, dialog!, index);
    }

    /// <summary>The port of legacy <c>DialogManager.ClickLabel</c> (DialogManager.cs:170-188).</summary>
    private int ClickLabel(CommandContext ctx, string label)
    {
        if (Current(ctx, out DialogSnapshot? dialog) is { } gate)
            return gate;

        int match = 0;
        int found = 0;
        for (int i = 0; i < dialog!.Buttons.Count; i++)
        {
            if (dialog.Buttons[i].Label.Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                match = i + 1;
                found++;
            }
        }

        return found switch
        {
            0 => ctx.Result.Fail(McStrings.Format("dialog.action_label_unknown", label)),
            > 1 => ctx.Result.Fail(McStrings.Format("dialog.action_label_ambiguous", label)),
            _ => Press(ctx, dialog!, match),
        };
    }

    /// <summary>Presses the 1-based button and reports what the press actually did.</summary>
    private int Press(CommandContext ctx, DialogSnapshot dialog, int index)
    {
        if (dialog.RegistryId is not null)
            // Legacy refused every action on a dialog it could not resolve (DialogManager.cs:203-204).
            return ctx.Result.Fail(McStrings.Get("dialog.unresolved_action_disabled"));

        DialogButtonInfo button = dialog.Buttons[index - 1];
        DialogClickOutcome outcome = ctx.Run(ct => ctx.Game.Dialogs.ClickAsync(index - 1, Submitted(dialog), ct));
        Clear();

        (bool ok, string message) = DescribeClick(index, button, outcome);
        return ok ? ctx.Result.Ok(message) : ctx.Result.Fail(message);
    }

    /// <summary>
    /// Turns a press outcome into the reported success flag and message.
    /// A press reports reaching the server only when something was sent.
    /// A button whose action this client cannot perform closes the dialog and reaches no one.
    /// The three purely descriptive client-side kinds report what the button was.
    /// </summary>
    internal static (bool Ok, string Message) DescribeClick(
        int index, DialogButtonInfo button, DialogClickOutcome outcome) => outcome switch
        {
            DialogClickOutcome.ActionSent => (
                true, McStrings.Format("dialog.action_custom_sent", button.ActionId ?? string.Empty)),
            DialogClickOutcome.CommandSent => (
                true, McStrings.Format("dialog.action_command_sent", button.ActionValue ?? string.Empty)),
            DialogClickOutcome.ClosedOnly => (true, McStrings.Get("dialog.action_closed")),
            DialogClickOutcome.ActionNotPerformed => button.ActionKind switch
            {
                "OpenUrl" => (true, McStrings.Format("dialog.action_open_url", button.ActionValue ?? string.Empty)),
                "SuggestCommand" => (
                    true, McStrings.Format("dialog.action_suggest_command", button.ActionValue ?? string.Empty)),
                "CopyToClipboard" => (true, McStrings.Format("dialog.action_copy", button.ActionValue ?? string.Empty)),
                _ => (false, McStrings.Format("dialog.action_unsupported", button.ActionKind)),
            },
            _ => (false, McStrings.Format("dialog.action_unknown", index)),
        };

    /// <summary>The port of legacy <c>DialogManager.Cancel</c> (DialogManager.cs:190-202).</summary>
    private int Cancel(CommandContext ctx)
    {
        if (Current(ctx, out DialogSnapshot? dialog) is { } gate)
            return gate;

        if (!dialog!.CanCloseWithEscape && !dialog.HasExitAction)
            return ctx.Result.Fail(McStrings.Get("dialog.cannot_cancel"));

        if (dialog.RegistryId is not null)
            return ctx.Result.Fail(McStrings.Get("dialog.unresolved_action_disabled"));

        ctx.Run(ct => ctx.Game.Dialogs.CancelAsync(ct));
        Clear();
        return ctx.Result.Ok(McStrings.Get("dialog.action_closed"));
    }

    /// <summary>The port of legacy <c>DialogManager.Dismiss</c> (DialogManager.cs:113-128).</summary>
    private int Dismiss(CommandContext ctx)
    {
        if (Current(ctx, out _) is { } gate)
            return gate;

        ctx.Run(ct => ctx.Game.Dialogs.CancelAsync(ct));
        Clear();
        return ctx.Result.Ok(McStrings.Get("dialog.dismissed"));
    }

    /// <summary>
    /// Reads the shown dialog, or returns the failure every legacy subcommand answered with when none was shown.
    /// Returns null when a dialog IS shown, in which case <paramref name="dialog"/> is set.
    /// </summary>
    private static int? Current(CommandContext ctx, out DialogSnapshot? dialog)
    {
        dialog = null;
        if (!ctx.InSession)
            return ctx.Result.Fail(BookBedDialogCommandText.NotConnected(ctx));

        dialog = ctx.Run(ct => ctx.Game.Dialogs.GetCurrentAsync(ct));
        return dialog is null ? (int?)ctx.Result.Fail(McStrings.Get("dialog.none")) : null;
    }

    /// <summary>Legacy <c>DialogFormatter.DisplayTitle</c> (DialogFormatter.cs:9-16).</summary>
    private static string DisplayTitle(DialogSnapshot dialog)
    {
        if (dialog.RegistryId is { } registryId)
            return McStrings.Format("dialog.unresolved_title", registryId);

        if (!string.IsNullOrWhiteSpace(dialog.ExternalTitle))
            return dialog.ExternalTitle;

        return string.IsNullOrWhiteSpace(dialog.Title) ? DisplayType(dialog.TypeId) : dialog.Title;
    }

    /// <summary>
    /// The body of the dialog, or legacy's unresolved-reference body for a dialog the server sent as a registry index (DialogManager.cs:88-97).
    /// </summary>
    private static IReadOnlyList<string> BodyLines(DialogSnapshot dialog)
    {
        if (dialog.RegistryId is { } registryId)
            return [McStrings.Format("dialog.unresolved_body", registryId)];

        return dialog.Body;
    }

    /// <summary>Legacy <c>DialogFormatter.DisplayType</c> (DialogFormatter.cs:53-64).</summary>
    private static string DisplayType(string rawType) => rawType switch
    {
        "minecraft:notice" => McStrings.Get("dialog.type.notice"),
        "minecraft:confirmation" => McStrings.Get("dialog.type.confirmation"),
        "minecraft:multi_action" => McStrings.Get("dialog.type.multi_action"),
        "minecraft:dialog_list" => McStrings.Get("dialog.type.dialog_list"),
        "minecraft:server_links" => McStrings.Get("dialog.type.server_links"),
        _ => string.IsNullOrEmpty(rawType) ? McStrings.Get("dialog.type.unknown") : rawType,
    };

    /// <summary>Legacy <c>DialogFormatter.DescribeKind</c> (DialogFormatter.cs:66-76).</summary>
    private static string DescribeKind(string kind) => kind switch
    {
        "Text" => McStrings.Get("dialog.input_kind.text"),
        "Boolean" => McStrings.Get("dialog.input_kind.boolean"),
        "SingleOption" => McStrings.Get("dialog.input_kind.options"),
        "NumberRange" => McStrings.Get("dialog.input_kind.number"),
        _ => McStrings.Get("dialog.input_kind.unknown"),
    };

    /// <summary>
    /// Legacy <c>DialogFormatter.DescribeInput</c> (DialogFormatter.cs:78-90) for the kinds whose describing data this stack carries.
    /// A text field's maximum length and a checkbox's on/off strings are not modelled by UMPK's dialog view, so those two describe as nothing rather than as an invented limit.
    /// </summary>
    private static string DescribeInput(DialogInputInfo input) => input.Kind switch
    {
        "SingleOption" => McStrings.Format(
            "dialog.input_desc_options", string.Join(", ", input.Options.Select(static option => option.Id))),
        "NumberRange" => McStrings.Format(
            "dialog.input_desc_number", input.RangeStart ?? 0f, input.RangeEnd ?? 1f),
        "Text" or "Boolean" => string.Empty,
        _ => McStrings.Get("dialog.input_desc_unknown"),
    };

    /// <summary>Legacy <c>DialogFormatter.DescribeAction</c> (DialogFormatter.cs:92-107).</summary>
    private static string DescribeAction(DialogButtonInfo button) => button.ActionKind switch
    {
        "None" => McStrings.Get("dialog.action_desc_close"),
        "RunCommand" or "DynamicRunCommand" => McStrings.Get("dialog.action_desc_command"),
        "Custom" or "DynamicCustom" => McStrings.Get("dialog.action_desc_custom"),
        "ShowDialog" => McStrings.Get("dialog.action_desc_show_dialog"),
        "OpenUrl" => McStrings.Get("dialog.action_desc_open_url"),
        "SuggestCommand" => McStrings.Get("dialog.action_desc_suggest"),
        "CopyToClipboard" => McStrings.Get("dialog.action_desc_copy"),
        _ => McStrings.Get("dialog.action_desc_unknown"),
    };

    /// <summary>
    /// The port of legacy <c>NormalizeInputValue</c> (DialogManager.cs:333-381) over the input facts this stack carries: a checkbox takes a boolean, a chooser takes one of its option ids, and a slider takes a number inside its range.
    /// A text field's maximum length is not modelled here, so text passes through the way legacy's default branch did.
    /// </summary>
    private static string NormalizeInputValue(DialogInputInfo input, string value, out string? error)
    {
        error = null;
        switch (input.Kind)
        {
            case "Boolean":
                if (bool.TryParse(value, out bool boolValue))
                    return boolValue ? "true" : "false";

                error = McStrings.Format("dialog.input_boolean_invalid", input.Key);
                return input.InitialValue;

            case "SingleOption":
                foreach (DialogOptionInfo option in input.Options)
                {
                    if (option.Id.Equals(value, StringComparison.Ordinal))
                        return value;
                }

                error = McStrings.Format("dialog.input_option_invalid", input.Key);
                return input.InitialValue;

            case "NumberRange":
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                {
                    error = McStrings.Format("dialog.input_number_invalid", input.Key);
                    return input.InitialValue;
                }

                float start = input.RangeStart ?? 0f;
                float end = input.RangeEnd ?? 1f;
                float min = Math.Min(start, end);
                float max = Math.Max(start, end);
                if (number < min || number > max)
                {
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        McStrings.Get("dialog.input_number_range_invalid"),
                        input.Key,
                        min,
                        max);
                    return input.InitialValue;
                }

                return NumberToString(number);

            default:
                return value;
        }
    }

    /// <summary>The port of legacy <c>NumberToString</c> (DialogManager.cs:405-411).</summary>
    private static string NumberToString(float value)
    {
        int integer = (int)value;
        return integer == value
            ? integer.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The staged values the shown dialog actually declares.
    /// Legacy submitted the dialog instance's own value map, so a value staged against an earlier dialog could never travel with a later one.
    /// </summary>
    private Dictionary<string, string> Submitted(DialogSnapshot dialog)
    {
        Dictionary<string, string> staged = Snapshot();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DialogInputInfo input in dialog.Inputs)
        {
            if (staged.TryGetValue(input.Key, out string? value))
                values[input.Key] = value;
        }

        return values;
    }

    private Dictionary<string, string> Snapshot()
    {
        lock (_gate)
            return new Dictionary<string, string>(_staged, StringComparer.Ordinal);
    }

    private void Clear()
    {
        lock (_gate)
            _staged.Clear();
    }
}
