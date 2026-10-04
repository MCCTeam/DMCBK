using DMCBK.Core.Beacon;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using DMCBK.Testing;
using Umpk;
using Umpk.Game.Dialogs;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Server-dialog support: the <c>on dialog</c> hook, the <c>dialog.*</c> verbs, the manifest capabilities, and the scheduler's dialog trigger rules.
/// All headless through the script test host.
/// </summary>
public sealed class DialogBeaconTests : IDisposable
{
    public void Dispose()
    {
        BeaconProviders.Clear();
        BeaconHookCatalog.ClearCustomHooks();
    }

    private static (BeaconEngine Engine, ScriptTestHost Host) NewEngine(int seed = 21)
    {
        var host = new ScriptTestHost();
        var engine = new BeaconEngine(host, new VirtualClock(), new SeededRng(seed), new FuelBudget());
        engine.Variables = new VariableStore();
        return (engine, host);
    }

    private static string WithHeader(string body) => "# beacon 1\n" + body;

    private static BeaconDialogInfo LoginDialog() => new(
        "Login",
        "Enter your password",
        [new BeaconDialogInput("auth_login_password", "Password", "Text", string.Empty)],
        [new BeaconDialogButton(1, "Submit")],
        RegistryId: null);

    #region hook catalog

    [Fact]
    public void Catalog_ListsDialogWithFields()
    {
        Assert.Contains("dialog", BeaconHookCatalog.KnownHooks);
        Assert.True(BeaconHookCatalog.TryGetSchema("dialog", out BeaconHookSchema? schema));
        Assert.Contains(schema!.Fields, f => f.Name == "title");
        Assert.Contains(schema.Fields, f => f.Name == "input_keys");
        Assert.Contains(schema.Fields, f => f.Name == "buttons");
        Assert.Contains(schema.Fields, f => f.Name == "registry_id");
        Assert.False(BeaconHookCatalog.IsSuppressible("dialog"));
    }

    [Fact]
    public void StaticCheck_AcceptsOnDialog()
    {
        BeaconLexResult lexed = BeaconLexer.Lex("t.bcn", WithHeader("on dialog:\nshow title\nend on\n"));
        BeaconParseResult parsed = BeaconParser.Parse("t.bcn", lexed.Tokens, 1);
        Assert.NotNull(parsed.Script);
        IReadOnlyList<BeaconDiagnostic> diagnostics =
            BeaconStaticCheck.Check(BeaconDesugar.Desugar(parsed.Script!));
        Assert.DoesNotContain(diagnostics, d => d.Code == BeaconDiagnosticCodes.UnknownEvent);
    }

    [Fact]
    public void EventFields_DialogBuildsTheDocumentedMap()
    {
        IReadOnlyDictionary<string, BeaconValue> fields = BeaconEventFields.Dialog(
            "Login",
            ["Enter your password"],
            [("auth_login_password", (string?)"Password", "Text", string.Empty)],
            [(1, "Submit")]);

        Assert.Equal("Login", ((BeaconTextValue)fields["title"]).Value);
        Assert.Equal("Enter your password", ((BeaconTextValue)fields["body"]).Value);
        var keys = (BeaconListValue)fields["input_keys"];
        Assert.Equal("auth_login_password", ((BeaconTextValue)keys.Items[0]).Value);
        Assert.Equal(BeaconValue.None, fields["registry_id"]);
    }

    #endregion
    #region capabilities and manifest

    [Fact]
    public void Capabilities_DialogVerbsInferDialogCaps()
    {
        var (engine, _) = NewEngine();
        engine.LoadSource("d", "d.bcn", "# beacon 1\n# needs: chat.send\ndialog.show()\n");

        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("d"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("dialog.read", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("# needs: chat.send dialog.read", refusal.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_OnDialogInfersDialogRead()
    {
        var (engine, _) = NewEngine();
        engine.LoadSource(
            "d", "d.bcn",
            "# beacon 1\n# needs: chat.send\non dialog:\nshow title\nend on\n");

        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("d"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("dialog.read", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_AnswerInfersDialogWrite()
    {
        var (engine, _) = NewEngine();
        engine.LoadSource(
            "d", "d.bcn",
            "# beacon 1\n# needs: dialog.read\ndialog.answer({a: \"b\"})\n");

        BeaconDiagnostic refusal = Assert.Single(
            engine.Lint("d"), d => d.Code == BeaconDiagnosticCodes.ManifestNeedsMismatch);
        Assert.Contains("dialog.write", refusal.Message, StringComparison.Ordinal);
    }

    #endregion
    #region dialog.* verbs

    [Fact]
    public async Task Show_NoneWhenNoDialog()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read\nshow dialog.show() is none\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("yes", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Show_MapsTheOpenDialog()
    {
        var (engine, host) = NewEngine();
        host.DialogValue = LoginDialog();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read\nset d to dialog.show()\nshow \"{d.title} {d.input_keys[0]} {d.buttons[0].label}\"\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("Login auth_login_password Submit", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Set_ThenClick_SubmitsStaged()
    {
        var (engine, host) = NewEngine();
        host.DialogValue = LoginDialog();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read dialog.write\n" +
            "dialog.set(\"auth_login_password\", \"s3cret\")\nshow dialog.click(1)\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("yes", Assert.Single(run.LocalOutput));
        // click() with no explicit map submits what set() staged.
        Assert.Empty(host.DialogStaged);
        var (values, button) = Assert.Single(host.DialogAnswers);
        Assert.Equal(1, button);
        Assert.Null(values);
    }

    [Fact]
    public async Task Answer_SubmitsMapAtomically()
    {
        var (engine, host) = NewEngine();
        host.DialogValue = LoginDialog();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read dialog.write\n" +
            "show dialog.answer({auth_login_password: \"s3cret\"})\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("yes", Assert.Single(run.LocalOutput));
        var (values, button) = Assert.Single(host.DialogAnswers);
        Assert.Equal(1, button);
        Assert.Equal("s3cret", values!["auth_login_password"]);
    }

    [Fact]
    public async Task Click_ByLabelResolvesCaseInsensitively()
    {
        var (engine, host) = NewEngine();
        host.DialogValue = LoginDialog();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read dialog.write\nshow dialog.click(\"submit\")\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal("yes", Assert.Single(run.LocalOutput));
        Assert.Equal(1, Assert.Single(host.DialogAnswers).Button);
    }

    [Fact]
    public async Task Set_UnknownKey_RaisesCatchably()
    {
        var (engine, host) = NewEngine();
        host.DialogValue = LoginDialog();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read dialog.write\n" +
            "try\ndialog.set(\"nope\", \"x\")\ncatch err\nshow err.message\nend try\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Contains("nope", Assert.Single(run.LocalOutput));
    }

    [Fact]
    public async Task Verbs_WithoutOpenDialog_RaiseCatchably()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read dialog.write\n" +
            "try\ndialog.click(1)\ncatch err\nshow err.code\nend try\n" +
            "try\ndialog.close()\ncatch err\nshow err.code\nend try\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Equal(["B4002", "B4002"], run.LocalOutput);
    }

    [Fact]
    public async Task UnknownMember_NamesTheBadMember()
    {
        var (engine, _) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("d", WithHeader(
            "# needs: dialog.read dialog.write\ntry\ndialog.frob()\ncatch err\nshow err.message\nend try\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");
        Assert.Contains("dialog.frob", Assert.Single(run.LocalOutput));
    }

    #endregion
    #region on dialog end to end

    [Fact]
    public async Task OnDialog_FilterAndAnswer()
    {
        var (engine, host) = NewEngine();
        host.DialogValue = LoginDialog();
        BeaconRunResult run = await engine.RunScriptAsync("auth", WithHeader(
            "# needs: dialog.read dialog.write\n" +
            "on dialog as d when d.input_keys contains \"auth_login_password\"\n" +
            "dialog.answer({auth_login_password: \"s3cret\"}, 1)\nend on\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");

        BeaconFireResult fire = await engine.FireEventAsync(
            "dialog",
            BeaconEventFields.Dialog(
                "Login",
                ["Enter your password"],
                [("auth_login_password", (string?)"Password", "Text", string.Empty)],
                [(1, "Submit")]));
        BeaconHandlerFire handler = Assert.Single(fire.Handlers);
        Assert.True(handler.Result!.Success);
        var (values, button) = Assert.Single(host.DialogAnswers);
        Assert.Equal(1, button);
        Assert.Equal("s3cret", values!["auth_login_password"]);
    }

    [Fact]
    public async Task OnDialog_WhenFilterSkipsUnrelatedDialogs()
    {
        var (engine, host) = NewEngine();
        BeaconRunResult run = await engine.RunScriptAsync("auth", WithHeader(
            "# needs: dialog.read dialog.write\n" +
            "on dialog as d when d.input_keys contains \"auth_login_password\"\n" +
            "dialog.answer({auth_login_password: \"s3cret\"}, 1)\nend on\n"));
        Assert.True(run.Success, run.Error?.Message ?? "run failed");

        BeaconFireResult fire = await engine.FireEventAsync(
            "dialog",
            BeaconEventFields.Dialog("News", ["Hello"], [], [(1, "OK")]));
        Assert.Single(fire.Handlers);
        Assert.Empty(host.DialogAnswers);
    }

    #endregion
    #region pre-session snapshot

    private static Dialog LoginUmpkDialog() => new(
        Identifier.Minecraft("multi_action"),
        Component.Text("Login"),
        ExternalTitle: null,
        CanCloseWithEscape: true,
        Body: [new DialogBodyElement(Identifier.Minecraft("plain_message"), Component.Text("Enter it"), 200)],
        Inputs:
        [
            new DialogInput(
                "auth_login_password",
                DialogInputKind.Text,
                Identifier.Minecraft("text"),
                Component.Text("Password"),
                string.Empty,
                [],
                Range: null),
        ],
        Buttons:
        [
            new DialogButton(
                Component.Text("Submit"), null, 200,
                new DialogAction(DialogActionKind.Custom, Identifier.Minecraft("custom"), null, Identifier.Parse("example:login"), null)),
        ],
        ExitAction: null);

    [Fact]
    public void PreSessionDialog_ProjectsTitleBodyInputsButtons()
    {
        PreSessionDialogInfo info = PreSessionDialogInfo.FromUmpk(LoginUmpkDialog(), registryId: null);

        Assert.Equal("Login", info.Title);
        Assert.Equal(["Enter it"], info.Body);
        var input = Assert.Single(info.Inputs);
        Assert.Equal("auth_login_password", input.Key);
        Assert.Equal("Text", input.Kind);
        Assert.True(info.HasInput("auth_login_password"));
        Assert.False(info.HasInput("other"));
        Assert.Equal(["auth_login_password"], info.InputKeys);
        var button = Assert.Single(info.Buttons);
        Assert.Equal("Submit", button.Label);
        Assert.Equal("Custom", button.ActionKind);
        Assert.Equal("example:login", button.ActionId);
        Assert.Null(info.RegistryId);
    }

    [Fact]
    public void PreSessionDialog_RegistryReference_KeepsTheId()
    {
        PreSessionDialogInfo info = PreSessionDialogInfo.FromUmpk(dialog: null, registryId: 12);

        Assert.Equal(12, info.RegistryId);
        Assert.Empty(info.Inputs);
        Assert.Empty(info.Buttons);
    }

    #endregion
}
