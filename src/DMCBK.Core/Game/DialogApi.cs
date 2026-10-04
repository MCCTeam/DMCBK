using Umpk.Client.Actions;
using Umpk.Game.Dialogs;

namespace DMCBK.Core;

/// <summary>
/// The server-dialog surface (1.21.6+): the dialog the server is currently showing, and the serverbound responses that press a button, submit an action id directly, or cancel the way escape does.
/// <para>
/// Everything is era-gated on the negotiated protocol carrying the dialog packets: below <see cref="FirstDialogProtocol"/> the server never shows a dialog and never accepts a response, so <see cref="IsSupportedAsync"/> reports false and a caller can say so instead of sending into the void.
/// UMPK itself also drops a response with a warning on such a version, so a stale gate can never corrupt the stream.
/// </para>
/// <para>
/// A dialog the server sent as a registry reference (rather than an inline body) has no resolvable body: the snapshot then reports <see cref="DialogSnapshot.RegistryId"/> with an empty title, body, input and button list, which is "a dialog is open that cannot be rendered" rather than "no dialog".
/// </para>
/// </summary>
public sealed class DialogApi
{
    /// <summary>The first protocol that carries the dialog packets (Minecraft 1.21.6).</summary>
    public const int FirstDialogProtocol = 771;

    private readonly GameSession _session;
    private readonly Umpk.Text.ITranslationSource? _translations;

    internal DialogApi(GameSession session, Umpk.Text.ITranslationSource? translations)
    {
        _session = session;
        _translations = translations;
    }

    /// <summary>Whether the negotiated version carries server dialogs (1.21.6+).</summary>
    public Task<bool> IsSupportedAsync(CancellationToken ct = default)
        => _session.ReadAsync(client => (client.Session?.Version.Version.Protocol ?? 0) >= FirstDialogProtocol, ct);

    /// <summary>
    /// Snapshots the dialog the server is currently showing, or null when none is open.
    /// Never throws on a version without dialogs; the state simply stays empty there.
    /// </summary>
    public Task<DialogSnapshot?> GetCurrentAsync(CancellationToken ct = default)
        => _session.RunAsync(async client =>
        {
            Umpk.Client.Snapshots.DialogSnapshot? dialog = await client.Snapshots.DialogAsync(ct).ConfigureAwait(false);
            return dialog is null ? null : Project(dialog);
        });

    /// <summary>
    /// Presses the button at <paramref name="buttonIndex"/> of the shown dialog, submitting the dialog's input values.
    /// The dialog closes client-side on a press, as it does in vanilla.
    ///
    /// <para>
    /// The returned <see cref="DialogClickOutcome"/> says what the press actually did, which a bare success flag cannot.
    /// Only the custom-click kinds put anything on the wire through the dialog packet.
    /// A <c>run_command</c> button runs the command, as a vanilla client does when that button is pressed.
    /// </para>
    /// </summary>
    /// <param name="buttonIndex">The zero-based index into <see cref="DialogSnapshot.Buttons"/>.</param>
    /// <param name="values">Input values keyed by input key; null submits each input's initial value.</param>
    /// <param name="ct">The cancellation token.</param>
    public Task<DialogClickOutcome> ClickAsync(
        int buttonIndex, IReadOnlyDictionary<string, string>? values = null, CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Dialog.ClickAsync(buttonIndex, values, ct));

    /// <summary>
    /// Submits a custom-click action id directly, for a caller that already knows it (a registry-referenced dialog whose body could not be resolved, or a recorded response being replayed).
    /// </summary>
    /// <param name="actionId">The namespaced custom-click action id.</param>
    /// <param name="values">Input values keyed by input key; null submits the dialog's own defaults.</param>
    /// <param name="ct">The cancellation token.</param>
    public Task SubmitActionAsync(
        string actionId, IReadOnlyDictionary<string, string>? values = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        Umpk.Identifier id = Umpk.Identifier.Parse(actionId);
        return _session.RunAsync(client => client.Actions.Dialog.SubmitActionAsync(id, values, ct));
    }

    /// <summary>
    /// Cancels the shown dialog the way escape does: triggers the declared exit action when there is one, then clears the local dialog state.
    /// </summary>
    public Task CancelAsync(CancellationToken ct = default)
        => _session.RunAsync(client => client.Actions.Dialog.CancelAsync(ct));

    private DialogSnapshot Project(Umpk.Client.Snapshots.DialogSnapshot state)
    {
        Dialog? dialog = state.Dialog;
        if (dialog is null)
        {
            // A registry reference: a dialog is open, but its body lives in a registry UMPK does not model.
            return new DialogSnapshot(
                string.Empty, string.Empty, null, CanCloseWithEscape: true, [], [], [],
                HasExitAction: false, state.RegistryId);
        }

        var body = new List<string>(dialog.Body.Count);
        foreach (DialogBodyElement element in dialog.Body)
        {
            // An element with no text (an item element) still contributes its type, so nothing is dropped.
            body.Add(element.Contents is { } contents ? contents.ToPlainText(_translations) : element.Type.ToString());
        }

        var inputs = new List<DialogInputInfo>(dialog.Inputs.Count);
        foreach (DialogInput input in dialog.Inputs)
        {
            var options = new List<DialogOptionInfo>(input.Options.Count);
            foreach (DialogOption option in input.Options)
            {
                options.Add(new DialogOptionInfo(
                    option.Id, option.Display?.ToPlainText(_translations), option.IsInitial));
            }

            inputs.Add(new DialogInputInfo(
                input.Key,
                input.Kind.ToString(),
                input.Label?.ToPlainText(_translations),
                input.InitialValue,
                options,
                input.Range?.Start,
                input.Range?.End));
        }

        var buttons = new List<DialogButtonInfo>(dialog.Buttons.Count);
        foreach (DialogButton button in dialog.Buttons)
            buttons.Add(ProjectButton(button));

        return new DialogSnapshot(
            dialog.Type.ToString(),
            dialog.Title.ToPlainText(_translations),
            dialog.ExternalTitle?.ToPlainText(_translations),
            dialog.CanCloseWithEscape,
            body,
            inputs,
            buttons,
            dialog.ExitAction?.Action?.Id is not null,
            RegistryId: null);
    }

    private DialogButtonInfo ProjectButton(DialogButton button)
        => new(
            button.Label.ToPlainText(_translations),
            button.Tooltip?.ToPlainText(_translations),
            button.Action?.Kind.ToString() ?? DialogActionKind.None.ToString(),
            button.Action?.Value,
            button.Action?.Id?.ToString());
}

/// <summary>A snapshot of the dialog the server is currently showing.</summary>
/// <param name="TypeId">The dialog's type id (for example <c>minecraft:multi_action</c>); empty for a registry reference.</param>
/// <param name="Title">The dialog title; empty for a registry reference.</param>
/// <param name="ExternalTitle">The shorter title used outside the dialog screen, when set.</param>
/// <param name="CanCloseWithEscape">Whether escape closes the dialog.</param>
/// <param name="Body">The body elements in order, flattened to plain text.</param>
/// <param name="Inputs">The input controls in order.</param>
/// <param name="Buttons">The pressable buttons in order (every dialog type normalised into one list).</param>
/// <param name="HasExitAction">Whether the dialog declares an action that escape triggers.</param>
/// <param name="RegistryId">
/// The registry index when the server referenced a registered dialog instead of sending a body; null for an inline dialog.
/// A non-null value with an empty title and no buttons means the dialog is open but its body is not resolvable in this build.
/// </param>
public sealed record DialogSnapshot(
    string TypeId,
    string Title,
    string? ExternalTitle,
    bool CanCloseWithEscape,
    IReadOnlyList<string> Body,
    IReadOnlyList<DialogInputInfo> Inputs,
    IReadOnlyList<DialogButtonInfo> Buttons,
    bool HasExitAction,
    int? RegistryId);

/// <summary>One dialog input control.</summary>
/// <param name="Key">The payload key this control writes to.</param>
/// <param name="Kind">The control kind (<c>Text</c>, <c>Boolean</c>, <c>SingleOption</c>, <c>NumberRange</c>, <c>Unknown</c>).</param>
/// <param name="Label">The control's label, when it has one.</param>
/// <param name="InitialValue">The value submitted when the control is left untouched.</param>
/// <param name="Options">The choices of a single-option control; empty otherwise.</param>
/// <param name="RangeStart">The lowest value of a number-range control; null otherwise.</param>
/// <param name="RangeEnd">The highest value of a number-range control; null otherwise.</param>
public sealed record DialogInputInfo(
    string Key,
    string Kind,
    string? Label,
    string InitialValue,
    IReadOnlyList<DialogOptionInfo> Options,
    float? RangeStart,
    float? RangeEnd);

/// <summary>One choice of a single-option dialog input.</summary>
/// <param name="Id">The value submitted when this option is chosen.</param>
/// <param name="Display">The label shown for the option, when it differs from the id.</param>
/// <param name="IsInitial">Whether this option is preselected.</param>
public sealed record DialogOptionInfo(string Id, string? Display, bool IsInitial);

/// <summary>One pressable dialog button.</summary>
/// <param name="Label">The button label.</param>
/// <param name="Tooltip">The hover tooltip, when set.</param>
/// <param name="ActionKind">What pressing the button does (the action kind name; <c>None</c> when it only closes).</param>
/// <param name="ActionValue">The action's string argument (url, command, page, clipboard text, dialog id), when it takes one.</param>
/// <param name="ActionId">The custom-click action id, for the custom and dynamic/custom kinds.</param>
public sealed record DialogButtonInfo(
    string Label, string? Tooltip, string ActionKind, string? ActionValue, string? ActionId);
