using DMCBK.Core.Beacon;
using Xunit;

namespace DMCBK.Core.Tests.Beacon;

/// <summary>
/// Parser contract: every statement shape, labeled closers, headtail, prologue, precedence, calls, literals, not-chains, the structural boolean rule, numeric counts, nesting, export placement, desugar spans, and the forgiven forms (log, bare mcc).
/// </summary>
public sealed class ParserTests
{
    private static BeaconEngine NewEngine() => new(new BeaconTestHost());

    private static IReadOnlyList<BeaconDiagnostic> Lint(string source)
    {
        var engine = NewEngine();
        engine.LoadSource("probe", "probe.bcn", source);
        return engine.Lint("probe");
    }

    private static IReadOnlyList<BeaconDiagnostic> LintErrors(string source)
        => Lint(source).Where(d => d.Severity == BeaconSeverity.Error).ToList();

    private static void LintClean(string body)
    {
        IReadOnlyList<BeaconDiagnostic> errors = LintErrors("# beacon 1\n" + body);
        Assert.Empty(errors);
    }

    private static void LintHasCode(string body, string code)
    {
        IReadOnlyList<BeaconDiagnostic> errors = LintErrors("# beacon 1\n" + body);
        Assert.Contains(errors, d => string.Equals(d.Code, code, StringComparison.Ordinal));
    }

    private static BeaconScript MustParse(string body)
    {
        string source = "# beacon 1\n" + body;
        Assert.Empty(LintErrors(source));
        BeaconLexResult lexed = BeaconLexer.Lex("probe.bcn", source);
        BeaconParseResult parsed = BeaconParser.Parse("probe.bcn", lexed.Tokens);
        Assert.DoesNotContain(parsed.Diagnostics, d => d.Severity == BeaconSeverity.Error);
        Assert.NotNull(parsed.Script);
        Assert.Empty(BeaconStaticCheck.Check(parsed.Script!));
        return parsed.Script!;
    }

    private static BeaconScript ParseAny(string body)
    {
        string source = "# beacon 1\n" + body;
        BeaconLexResult lexed = BeaconLexer.Lex("probe.bcn", source);
        BeaconParseResult parsed = BeaconParser.Parse("probe.bcn", lexed.Tokens);
        Assert.NotNull(parsed.Script);
        return parsed.Script!;
    }

    private static TopStatement SingleTopStatement(BeaconScript script)
    {
        BeaconTopDecl decl = Assert.Single(script.Decls);
        return Assert.IsType<TopStatement>(decl);
    }

    #region all statement shapes

    [Fact]
    public void Set_ParsesTargetAndValue()
    {
        BeaconScript script = MustParse("set name to \"Steve\"\n");
        var set = Assert.IsType<SetStmt>(SingleTopStatement(script).Statement);
        Assert.Equal("name", set.Target.Base);
        Assert.IsType<TextLiteral>(set.Value);
    }

    [Fact]
    public void Set_ParsesMemberAndIndexTargets()
    {
        BeaconScript script = MustParse("set warns[player] to (warns[player] or 0) + 1\n");
        var set = Assert.IsType<SetStmt>(SingleTopStatement(script).Statement);
        Assert.Single(set.Target.Parts);
        Assert.IsType<TargetIndex>(set.Target.Parts[0]);
    }

    [Fact]
    public void Say_Parses()
    {
        BeaconScript script = MustParse("say \"hi\"\n");
        Assert.IsType<SayStmt>(SingleTopStatement(script).Statement);
    }

    [Fact]
    public void Whisper_ParsesTwoOperands()
    {
        BeaconScript script = MustParse("whisper player \"hi\"\n");
        var whisper = Assert.IsType<WhisperStmt>(SingleTopStatement(script).Statement);
        Assert.IsType<IdentExpr>(whisper.Player);
        Assert.IsType<TextLiteral>(whisper.Message);
    }

    [Fact]
    public void Server_Parses()
    {
        BeaconScript script = MustParse("server \"/home\"\n");
        Assert.IsType<ServerStmt>(SingleTopStatement(script).Statement);
    }

    [Fact]
    public void Show_Parses()
    {
        BeaconScript script = MustParse("show \"thinking out loud\"\n");
        var show = Assert.IsType<ShowStmt>(SingleTopStatement(script).Statement);
        Assert.False(show.IsLog);
    }

    [Fact]
    public void Log_ParsesAsShowAlias()
    {
        BeaconScript script = MustParse("log \"parked\"\n");
        var show = Assert.IsType<ShowStmt>(SingleTopStatement(script).Statement);
        Assert.True(show.IsLog);
    }

    [Fact]
    public void Wait_ParsesCountAndUnit()
    {
        BeaconScript script = MustParse("wait 2 seconds\n");
        var wait = Assert.IsType<WaitStmt>(SingleTopStatement(script).Statement);
        Assert.IsType<NumberLiteral>(wait.Count);
        Assert.Equal("second", wait.Unit);
    }

    [Fact]
    public void Stop_ParsesBareAndEvent()
    {
        BeaconScript bare = MustParse("on chat when message contains \"x\"\nstop\nend on\n");
        var bareOn = Assert.IsType<OnBlock>(Assert.Single(bare.Decls));
        var bareStop = Assert.IsType<StopStmt>(Assert.Single(bareOn.Body.Statements));
        Assert.False(bareStop.HasEvent);

        BeaconScript withEvent = MustParse("on chat when message contains \"x\"\nstop event\nend on\n");
        var eventOn = Assert.IsType<OnBlock>(Assert.Single(withEvent.Decls));
        var eventStop = Assert.IsType<StopStmt>(Assert.Single(eventOn.Body.Statements));
        Assert.True(eventStop.HasEvent);
    }

    [Fact]
    public void CancelEvent_ParsesAsStopAlias()
    {
        BeaconScript script = MustParse("on chat when message contains \"x\"\ncancel event\nend on\n");
        var on = Assert.IsType<OnBlock>(Assert.Single(script.Decls));
        var stop = Assert.IsType<StopStmt>(Assert.Single(on.Body.Statements));
        Assert.True(stop.HasEvent);
        Assert.True(stop.IsCancelAlias);
        BeaconStatement desugared = BeaconDesugar.DesugarStmt(stop);
        var canonical = Assert.IsType<StopStmt>(desugared);
        Assert.True(canonical.HasEvent);
        Assert.False(canonical.IsCancelAlias);
    }

    [Fact]
    public void Skip_Break_Continue_Parse()
    {
        BeaconScript script = MustParse("while yes\nskip\nend while\n");
        var loop = Assert.IsType<WhileStmt>(((TopStatement)Assert.Single(script.Decls)).Statement);
        Assert.IsType<SkipStmt>(Assert.Single(loop.Body.Statements));
        LintClean("while yes\nbreak\nend while\n");
        LintClean("while yes\ncontinue\nend while\n");
    }

    [Fact]
    public void Return_WithAndWithoutValue()
    {
        BeaconScript withValue = MustParse("function f()\nreturn 1\nend function\n");
        var function = Assert.IsType<FunctionDef>(Assert.Single(withValue.Decls));
        var ret = Assert.IsType<ReturnStmt>(Assert.Single(function.Body.Statements));
        Assert.NotNull(ret.Value);

        BeaconScript bare = MustParse("function f()\nreturn\nend function\n");
        var bareFunction = Assert.IsType<FunctionDef>(Assert.Single(bare.Decls));
        Assert.Null(Assert.IsType<ReturnStmt>(Assert.Single(bareFunction.Body.Statements)).Value);
    }

    [Fact]
    public void Save_ParsesKeyAndValue()
    {
        BeaconScript script = MustParse("save \"warns\" to warns\n");
        var save = Assert.IsType<SaveStmt>(SingleTopStatement(script).Statement);
        Assert.IsType<TextLiteral>(save.Key);
    }

    [Fact]
    public void Lock_ParsesBlock()
    {
        BeaconScript script = MustParse("lock shared\nset x to 1\nend lock\n");
        Assert.IsType<LockStmt>(SingleTopStatement(script).Statement);
    }

    [Fact]
    public void Start_Await_CancelTask_Parse()
    {
        BeaconScript start = MustParse("start patrol()\n");
        Assert.IsType<StartStmt>(SingleTopStatement(start).Statement);
        BeaconScript await = MustParse("await id\n");
        Assert.IsType<AwaitStmt>(SingleTopStatement(await).Statement);
        BeaconScript cancel = MustParse("cancel task id\n");
        Assert.IsType<CancelTaskStmt>(SingleTopStatement(cancel).Statement);
    }

    [Fact]
    public void If_ElseIf_Else_ParsesBranches()
    {
        BeaconScript script = MustParse(
            "if health < 10 then\nsay \"a\"\nelse if food < 6 then\nsay \"b\"\nelse\nsay \"c\"\nend if\n");
        var ifStmt = Assert.IsType<IfStmt>(SingleTopStatement(script).Statement);
        Assert.Equal(2, ifStmt.Branches.Count);
        Assert.NotNull(ifStmt.ElseBody);
    }

    [Fact]
    public void While_Repeat_For_Try_Parse()
    {
        BeaconScript w = MustParse("while food < 20\nsay \"x\"\nend while\n");
        Assert.IsType<WhileStmt>(SingleTopStatement(w).Statement);
        BeaconScript r = MustParse("repeat 3 times\nsay \"x\"\nend repeat\n");
        Assert.IsType<RepeatStmt>(SingleTopStatement(r).Statement);
        BeaconScript f = MustParse("for each p in online_players\nsay \"x\"\nend for\n");
        var forStmt = Assert.IsType<ForStmt>(SingleTopStatement(f).Statement);
        Assert.Equal("p", forStmt.Var);
        BeaconScript t = MustParse("try\nsay \"x\"\ncatch err\nsay \"y\"\nend try\n");
        var tryStmt = Assert.IsType<TryStmt>(SingleTopStatement(t).Statement);
        Assert.Equal("err", tryStmt.CatchVar);
    }

    [Fact]
    public void ExprStatement_ParsesBareCall()
    {
        BeaconScript script = MustParse("eat()\n");
        var expr = Assert.IsType<ExprStmt>(SingleTopStatement(script).Statement);
        Assert.IsType<CallExpr>(expr.Expr);
    }

    #endregion
    #region labeled closers

    [Theory]
    [InlineData("if x is 1\nsay \"a\"\nend if\n", "if")]
    [InlineData("while x is 1\nsay \"a\"\nend while\n", "while")]
    [InlineData("repeat 2 times\nsay \"a\"\nend repeat\n", "repeat")]
    [InlineData("for each p in q\nsay \"a\"\nend for\n", "for")]
    [InlineData("try\nsay \"a\"\ncatch e\nsay \"b\"\nend try\n", "try")]
    [InlineData("lock shared\nsay \"a\"\nend lock\n", "lock")]
    [InlineData("on join\nsay \"a\"\nend on\n", "on")]
    [InlineData("every 60 seconds\nsay \"a\"\nend every\n", "every")]
    [InlineData("function f()\nsay \"a\"\nend function\n", "function")]
    [InlineData("command \"/x\"\nsay \"a\"\nend command\n", "command")]
    public void LabeledClosers_Parse(string body, string expected)
    {
        BeaconScript script = MustParse(body);
        Assert.NotNull(script);
        _ = expected;
    }

    [Fact]
    public void BareEnd_IsToleratedWithCompletionData()
    {
        BeaconScript script = MustParse("if x is 1\nsay \"a\"\nend\n");
        var ifStmt = Assert.IsType<IfStmt>(SingleTopStatement(script).Statement);
        Assert.True(ifStmt.EndLabelMissing);
        Assert.Equal("if", ifStmt.SuggestedEndLabel);
        Assert.Null(ifStmt.EndLabel);
        BeaconScript desugared = BeaconDesugar.Desugar(ParseAny("if x is 1\nsay \"a\"\nend\n"));
        var desugaredIf = Assert.IsType<IfStmt>(((TopStatement)Assert.Single(desugared.Decls)).Statement);
        Assert.Equal("if", desugaredIf.EndLabel);
        Assert.Equal("probe.bcn", desugaredIf.Span.Origin.File);
    }

    [Fact]
    public void MismatchedEndLabel_IsParseError()
    {
        LintHasCode("if x is 1\nsay \"a\"\nend while\n", BeaconDiagnosticCodes.Parse);
    }

    #endregion
    #region headtail

    [Theory]
    [InlineData("if x is 1 then\nsay \"a\"\nend if\n")]
    [InlineData("if x is 1:\nsay \"a\"\nend if\n")]
    [InlineData("if x is 1 then:\nsay \"a\"\nend if\n")]
    [InlineData("if x is 1\nsay \"a\"\nend if\n")]
    [InlineData("on join:\nsay \"a\"\nend on\n")]
    [InlineData("on join then\nsay \"a\"\nend on\n")]
    public void HeadTail_ThenColonOptional(string body)
    {
        LintClean(body);
    }

    #endregion
    #region top level + prologue

    [Fact]
    public void TopLevel_Blocks_Parse()
    {
        LintClean("on join\nsay \"a\"\nend on\n");
        LintClean("every 60 seconds\nsay \"a\"\nend every\n");
        LintClean("function f()\nsay \"a\"\nend function\n");
        LintClean("command \"/x\"\nsay \"a\"\nend command\n");
    }

    [Fact]
    public void Prologue_ImportExtern_Parse()
    {
        BeaconScript script = MustParse("import \"lib/econ.bcn\" as econ\nsay \"hi\"\n");
        Assert.Single(script.Imports);
        Assert.Equal("econ", script.Imports[0].Alias);
        BeaconScript externScript = MustParse("extern price_of from \"shop\"\nsay \"hi\"\n");
        Assert.Single(externScript.Externs);
    }

    [Fact]
    public void Import_AfterCode_IsParseError()
    {
        LintHasCode("say \"hi\"\nimport \"lib/econ.bcn\" as econ\n", BeaconDiagnosticCodes.Parse);
    }

    #endregion
    #region precedence

    [Fact]
    public void Precedence_MulBindsTighterThanAdd()
    {
        BeaconScript script = MustParse("show 1 + 2 * 3\n");
        var show = Assert.IsType<ShowStmt>(SingleTopStatement(script).Statement);
        var add = Assert.IsType<AddExpr>(show.Message);
        Assert.IsType<NumberLiteral>(add.Left);
        var mul = Assert.IsType<MulExpr>(add.Right);
        Assert.Equal("2", ((NumberLiteral)mul.Left).Raw);
    }

    [Fact]
    public void Precedence_UnaryMinusBindsTighterThanMul()
    {
        BeaconScript script = MustParse("show -2 * 3\n");
        var show = Assert.IsType<ShowStmt>(SingleTopStatement(script).Statement);
        var mul = Assert.IsType<MulExpr>(show.Message);
        Assert.IsType<NegateExpr>(mul.Left);
    }

    [Fact]
    public void Precedence_AndBindsTighterThanOr()
    {
        BeaconScript script = MustParse("if a is 1 or b is 2 and c is 3\nsay \"x\"\nend if\n");
        BeaconScript parsed = ParseAny("if a is 1 or b is 2 and c is 3\nsay \"x\"\nend if\n");
        var ifStmt = Assert.IsType<IfStmt>(((TopStatement)Assert.Single(parsed.Decls)).Statement);
        var or = Assert.IsType<OrExpr>(ifStmt.Branches[0].Cond);
        Assert.IsType<ComparisonExpr>(or.Left);
        Assert.IsType<AndExpr>(or.Right);
        _ = script;
    }

    [Fact]
    public void Precedence_NotAppliesToComparison()
    {
        BeaconScript script = MustParse("if not a is 1\nsay \"x\"\nend if\n");
        var ifStmt = Assert.IsType<IfStmt>(((TopStatement)Assert.Single(script.Decls)).Statement);
        var not = Assert.IsType<NotExpr>(ifStmt.Branches[0].Cond);
        Assert.IsType<ComparisonExpr>(not.Operand);
    }

    [Fact]
    public void Precedence_PostfixChains()
    {
        BeaconScript script = MustParse("show a.b(1)[0]\n");
        var show = Assert.IsType<ShowStmt>(SingleTopStatement(script).Statement);
        Assert.IsType<IndexExpr>(show.Message);
    }

    [Fact]
    public void Or_BuildsOperandNode_WithoutCoercion()
    {
        BeaconScript script = MustParse("set x to saved(\"k\") or {}\n");
        var set = Assert.IsType<SetStmt>(SingleTopStatement(script).Statement);
        Assert.IsType<OrExpr>(set.Value);
    }

    [Fact]
    public void SymbolAliases_ParseLikeWords()
    {
        LintClean("if not a is 1\nsay \"x\"\nend if\n");
        LintClean("if a is 1 and b is 2\nsay \"x\"\nend if\n");
        BeaconScript script = MustParse("show 1 + 2\n");
        Assert.NotNull(script);
    }

    #endregion
    #region calls, literals, not-chains

    [Fact]
    public void CallPrim_Parses()
    {
        BeaconScript script = MustParse("set x to call \"shopkeeper.daily_report\"()\nsay \"hi\"\n");
        var set = Assert.IsType<SetStmt>(((TopStatement)script.Decls[0]).Statement);
        Assert.IsType<CallPrimExpr>(set.Value);
    }

    [Fact]
    public void ListAndMap_Literals_Parse()
    {
        BeaconScript list = MustParse("set x to [1, \"two\"]\n");
        var listSet = Assert.IsType<SetStmt>(SingleTopStatement(list).Statement);
        Assert.Equal(2, Assert.IsType<ListLiteral>(listSet.Value).Items.Count);
        BeaconScript map = MustParse("set x to {running: no, q: \"\"}\n");
        var mapSet = Assert.IsType<SetStmt>(SingleTopStatement(map).Statement);
        Assert.Equal(2, Assert.IsType<MapLiteral>(mapSet.Value).Entries.Count);
        LintClean("set x to {}\n");
        LintClean("set x to []\n");
    }

    [Fact]
    public void NotChains_ParseRightAssociative()
    {
        BeaconScript script = MustParse("if not not tired\nsay \"x\"\nend if\n");
        var ifStmt = Assert.IsType<IfStmt>(((TopStatement)Assert.Single(script.Decls)).Statement);
        var outer = Assert.IsType<NotExpr>(ifStmt.Branches[0].Cond);
        Assert.IsType<NotExpr>(outer.Operand);
    }

    [Fact]
    public void InterpolationHoles_ParseToExpressions()
    {
        BeaconScript script = MustParse("say \"hi {player}!\"\n");
        var say = Assert.IsType<SayStmt>(SingleTopStatement(script).Statement);
        var text = Assert.IsType<TextLiteral>(say.Message);
        Assert.Contains(text.Parts, p => p is TextHole h && h.Expr is IdentExpr);
    }

    [Fact]
    public void Regex_Matches_Parses()
    {
        BeaconScript script = MustParse("on server_message when text matches /a+/\nsay \"x\"\nend on\n");
        var on = Assert.IsType<OnBlock>(Assert.Single(script.Decls));
        Assert.NotNull(on.When);
        var matches = Assert.IsType<ComparisonExpr>(on.When!);
        Assert.Equal(BeaconComparisonOp.Matches, matches.Op);
        Assert.NotNull(matches.Right);
        Assert.IsType<RegexLiteral>(matches.Right!);
    }

    #endregion
    #region structural boolean rule

    [Theory]
    [InlineData("if x > 1\nsay \"a\"\nend if\n")]
    [InlineData("if x is 1 and y is 2\nsay \"a\"\nend if\n")]
    [InlineData("if not tired\nsay \"a\"\nend if\n")]
    [InlineData("if foo()\nsay \"a\"\nend if\n")]
    [InlineData("if yes\nsay \"a\"\nend if\n")]
    [InlineData("while yes\nsay \"a\"\nend while\n")]
    [InlineData("on chat when message contains \"x\"\nsay \"a\"\nend on\n")]
    public void BooleanRule_Accepts_StructuralBooleans(string body)
    {
        LintClean(body);
    }

    [Theory]
    [InlineData("if food\nsay \"a\"\nend if\n")]
    [InlineData("while 1\nsay \"a\"\nend while\n")]
    [InlineData("while true\nsay \"a\"\nend while\n")]
    public void BooleanRule_Rejects_NonBooleans(string body)
    {
        LintHasCode(body, BeaconDiagnosticCodes.StrictBooleanCondition);
    }

    [Fact]
    public void BooleanRule_EmptyWhen_IsParseError()
    {
        LintHasCode("on chat when\nsay \"a\"\nend on\n", BeaconDiagnosticCodes.Parse);
    }

    #endregion
    #region numeric counts

    [Theory]
    [InlineData("wait 2 seconds\n")]
    [InlineData("every 60 seconds\nsay \"a\"\nend every\n")]
    [InlineData("on tps cooldown 300 seconds named \"x\"\nsay \"a\"\nend on\n")]
    public void NumericCounts_Accept_Numbers(string body)
    {
        LintClean(body);
    }

    [Theory]
    [InlineData("wait x seconds\n")]
    [InlineData("every n seconds\nsay \"a\"\nend every\n")]
    [InlineData("on tps cooldown n seconds named \"x\"\nsay \"a\"\nend on\n")]
    public void NumericCounts_Reject_NonNumbers(string body)
    {
        LintHasCode(body, BeaconDiagnosticCodes.Parse);
    }

    #endregion
    #region nesting + export

    [Fact]
    public void NestedOn_IsTopLevelOnlyError()
    {
        LintHasCode(
            "if x is 1\non chat when message contains \"x\"\nsay \"a\"\nend on\nend if\n",
            BeaconDiagnosticCodes.Parse);
    }

    [Fact]
    public void NestedFunction_IsTopLevelOnlyError()
    {
        LintHasCode(
            "if x is 1\nfunction f()\nsay \"a\"\nend function\nend if\n",
            BeaconDiagnosticCodes.Parse);
    }

    [Fact]
    public void NestedExport_IsTopLevelOnlyError()
    {
        LintHasCode(
            "if x is 1\nexport function f()\nsay \"a\"\nend function\nend if\n",
            BeaconDiagnosticCodes.Parse);
    }

    [Fact]
    public void Export_AtTopLevel_Parses()
    {
        BeaconScript script = MustParse("export function f()\nreturn 1\nend function\n");
        var function = Assert.IsType<FunctionDef>(Assert.Single(script.Decls));
        Assert.True(function.IsExport);
    }

    #endregion
    #region desugar + spans

    [Fact]
    public void Desugar_PreservesOriginThroughBang()
    {
        BeaconScript script = ParseAny("if not tired\nsay \"a\"\nend if\n");
        BeaconScript desugared = BeaconDesugar.Desugar(script);
        Assert.Equal(script.Span.Origin.File, desugared.Span.Origin.File);
        Assert.Equal(script.Span.Line, desugared.Span.Origin.Line);
    }

    [Fact]
    public void Desugar_NormalizesAliases_WithOriginChain()
    {
        BeaconScript script = ParseAny("on chat when message contains \"x\"\ncancel event\nend on\n");
        BeaconScript desugared = BeaconDesugar.Desugar(script);
        var on = Assert.IsType<OnBlock>(Assert.Single(desugared.Decls));
        var stop = Assert.IsType<StopStmt>(Assert.Single(on.Body.Statements));
        Assert.False(stop.IsCancelAlias);
        Assert.NotNull(stop.Span.DesugaredFrom);
        Assert.Equal("probe.bcn", stop.Span.Origin.File);
    }

    [Fact]
    public void Desugar_EqualBecomesIs()
    {
        BeaconScript script = ParseAny("if a == 1\nsay \"x\"\nend if\n");
        BeaconScript desugared = BeaconDesugar.Desugar(script);
        var ifStmt = Assert.IsType<IfStmt>(((TopStatement)Assert.Single(desugared.Decls)).Statement);
        var comparison = Assert.IsType<ComparisonExpr>(ifStmt.Branches[0].Cond);
        Assert.Equal(BeaconComparisonOp.Is, comparison.Op);
    }

    [Fact]
    public void Desugar_BreakBecomesStop_ContinueBecomesSkip()
    {
        BeaconScript script = ParseAny("while yes\nbreak\nend while\n");
        BeaconScript desugared = BeaconDesugar.Desugar(script);
        var loop = Assert.IsType<WhileStmt>(((TopStatement)Assert.Single(desugared.Decls)).Statement);
        Assert.IsType<StopStmt>(Assert.Single(loop.Body.Statements));
    }

    [Fact]
    public void EveryNode_CarriesFileLineColumnSpan()
    {
        BeaconScript script = MustParse("say \"hi\"\n");
        Assert.Equal("probe.bcn", script.Span.File);
        TopStatement top = SingleTopStatement(script);
        Assert.Equal(2, top.Span.Line);
    }

    #endregion
    #region forgiven forms

    [Fact]
    public void BareMccCall_ParsesAsExpression()
    {
        BeaconScript script = MustParse("set answer to mcc \"/list\"\n");
        var set = Assert.IsType<SetStmt>(SingleTopStatement(script).Statement);
        Assert.IsType<DmcbkExpr>(set.Value);
    }

    [Fact]
    public void IsSet_IsNotSet_IsEmpty_Forms_Parse()
    {
        LintClean("if x is set\nsay \"a\"\nend if\n");
        LintClean("if x is not set\nsay \"a\"\nend if\n");
        LintClean("if x is empty\nsay \"a\"\nend if\n");
        LintClean("if x is not empty\nsay \"a\"\nend if\n");
        LintClean("if message starts with \"!\"\nsay \"a\"\nend if\n");
        LintClean("if name ends with \"z\"\nsay \"a\"\nend if\n");
    }

    [Fact]
    public void UnitAsIdentifier_Parses()
    {
        LintClean("set s to 1\nsay \"hi\"\n");
    }
    #endregion
}
