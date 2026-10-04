using Umpk.Game.Dialogs;

namespace DMCBK.PluginSdk;

/// <summary>One input control of a pre-session dialog snapshot.</summary>
/// <param name="Key">The payload key this control writes to.</param>
/// <param name="Kind">The control kind (Text, Boolean, SingleOption, NumberRange, Unknown).</param>
/// <param name="Label">The control's label, when it has one.</param>
/// <param name="InitialValue">The value submitted when the control is left untouched.</param>
public sealed record PreSessionDialogInput(
    string Key,
    string Kind,
    string? Label,
    string InitialValue);

/// <summary>One pressable button of a pre-session dialog snapshot.</summary>
/// <param name="Label">The button label.</param>
/// <param name="ActionKind">What pressing the button does (None, RunCommand, Custom, ...).</param>
/// <param name="ActionValue">The action's string argument, when it takes one.</param>
/// <param name="ActionId">The custom-click action id, for the custom kinds.</param>
public sealed record PreSessionDialogButton(
    string Label,
    string ActionKind,
    string? ActionValue,
    string? ActionId);

/// <summary>
/// A snapshot of the server dialog showing before play begins (or during play).
/// Projected from UMPK state without touching the session loop, so it is safe to read from a <c>SessionCreated</c> handler during configuration.
/// </summary>
/// <param name="Title">The dialog title; empty for a registry reference.</param>
/// <param name="Body">The body lines in order, flattened to plain text.</param>
/// <param name="Inputs">The input controls in order.</param>
/// <param name="Buttons">The pressable buttons in order.</param>
/// <param name="RegistryId">Set when the server sent a registry reference with no resolvable body.</param>
public sealed record PreSessionDialogInfo(
    string Title,
    IReadOnlyList<string> Body,
    IReadOnlyList<PreSessionDialogInput> Inputs,
    IReadOnlyList<PreSessionDialogButton> Buttons,
    int? RegistryId)
{
    /// <summary>The input keys in order.</summary>
    public IReadOnlyList<string> InputKeys => Inputs.Select(i => i.Key).ToList();

    /// <summary>True when the dialog declares an input with this key.</summary>
    public bool HasInput(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Inputs.Any(i => string.Equals(i.Key, key, StringComparison.Ordinal));
    }

    /// <summary>
    /// Projects a UMPK dialog into a pre-session snapshot.
    /// Null dialog with a registry id means "a dialog is open that cannot be rendered", not "no dialog".
    /// </summary>
    public static PreSessionDialogInfo FromUmpk(Dialog? dialog, int? registryId)
    {
        if (dialog is null)
            return new PreSessionDialogInfo(string.Empty, [], [], [], registryId);

        var body = new List<string>(dialog.Body.Count);
        foreach (DialogBodyElement element in dialog.Body)
        {
            string text;
            try
            {
                text = element.Contents?.ToPlainText(null) ?? element.Type.ToString();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                text = element.Type.ToString();
            }

            if (!string.IsNullOrWhiteSpace(text))
                body.Add(text);
        }

        string title;
        try
        {
            title = dialog.Title.ToPlainText(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            title = string.Empty;
        }

        var inputs = new List<PreSessionDialogInput>(dialog.Inputs.Count);
        foreach (DialogInput input in dialog.Inputs)
        {
            string? label;
            try
            {
                label = input.Label?.ToPlainText(null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                label = null;
            }

            inputs.Add(new PreSessionDialogInput(
                input.Key, input.Kind.ToString(), label, input.InitialValue));
        }

        var buttons = new List<PreSessionDialogButton>(dialog.Buttons.Count);
        foreach (DialogButton button in dialog.Buttons)
        {
            string label;
            try
            {
                label = button.Label.ToPlainText(null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                label = string.Empty;
            }

            buttons.Add(new PreSessionDialogButton(
                label,
                button.Action?.Kind.ToString() ?? "None",
                button.Action?.Value,
                button.Action?.Id?.ToString()));
        }

        return new PreSessionDialogInfo(title, body, inputs, buttons, RegistryId: null);
    }
}
