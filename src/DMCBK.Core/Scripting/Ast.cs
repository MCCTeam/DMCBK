namespace DMCBK.Core.Beacon;

/// <summary>
/// Beacon abstract syntax: span-tagged nodes for scripts (prologue imports and externs, <c>on</c>/<c>every</c>/<c>function</c>/<c>command</c> blocks, every statement shape, and the full expression tree including <c>call</c>, list/map literals, regex, and interpolation holes).
/// Every node carries a <see cref="SourceSpan"/>; <c>BeaconDesugar</c> rebuilds the tree with chained spans so <see cref="SourceSpan.Origin"/> always points at the user's text (for trace).
/// </summary>
/// <remarks>
/// Structural versus runtime split: the parser and <c>BeaconStaticCheck</c> enforce shape only.
/// A condition must be a comparison, an <c>and</c>/<c>or</c>/<c>not</c> combination, a call, or a yes/no literal; <c>if not tired</c> passes statically and the interpreter judges types at runtime.
/// Mixed <c>+</c> operands and unknown-variable reads are runtime errors, never parse errors.
/// <para/>
/// Forgiven forms (one spelling taught, one forgiven): <c>log expr</c> parses as <see cref="ShowStmt"/> with <c>IsLog</c>; bare <c>mcc expr</c> (no parens) parses as <see cref="MccExpr"/>; <c>&amp;&amp;</c>/<c>||</c>/<c>!</c>, <c>==</c>/<c>!=</c>, <c>break</c>/<c>continue</c>, and <c>cancel event</c> normalize in desugar.
/// </remarks>
public abstract record BeaconNode(SourceSpan Span);

/// <summary>One <c>import "path" as alias</c> prologue entry.</summary>
public sealed record BeaconImport(
    SourceSpan Span,
    string Path,
    SourceSpan PathSpan,
    string Alias,
    SourceSpan AliasSpan) : BeaconNode(Span);

/// <summary>One <c>extern name from "plugin"</c> prologue entry.</summary>
public sealed record BeaconExtern(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    string PluginId,
    SourceSpan PluginSpan) : BeaconNode(Span);

/// <summary>A function parameter name.</summary>
public sealed record BeaconParam(string Name, SourceSpan Span);

/// <summary>A cooldown clause on an <c>on</c> block: <c>cooldown COUNT UNIT named "label"</c>.</summary>
public sealed record BeaconCooldown(
    SourceSpan Span,
    BeaconExpr Count,
    string Unit,
    string RawUnit,
    SourceSpan UnitSpan,
    string Name,
    SourceSpan NameSpan) : BeaconNode(Span);

/// <summary>A whole script: prologue imports/externs plus top-level declarations.</summary>
public sealed record BeaconScript(
    SourceSpan Span,
    int Major,
    IReadOnlyList<BeaconImport> Imports,
    IReadOnlyList<BeaconExtern> Externs,
    IReadOnlyList<BeaconTopDecl> Decls) : BeaconNode(Span);

/// <summary>Anything allowed at the top level: the four blocks or a bare statement.</summary>
public abstract record BeaconTopDecl(SourceSpan Span) : BeaconNode(Span);

/// <summary>An <c>on</c> event block.</summary>
public sealed record OnBlock(
    SourceSpan Span,
    string EventName,
    SourceSpan EventSpan,
    string? Alias,
    SourceSpan? AliasSpan,
    BeaconCooldown? Cooldown,
    BeaconExpr? When,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconTopDecl(Span);

/// <summary>An <c>every</c> scheduler block.</summary>
public sealed record EveryBlock(
    SourceSpan Span,
    BeaconExpr Interval,
    string Unit,
    string RawUnit,
    SourceSpan UnitSpan,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconTopDecl(Span);

/// <summary>
/// A one-shot <c>in count unit do</c> block: runs its body a single time when the delay elapses.
/// Pending one-shots cancel on reconnect, exactly like pending <c>wait</c> sleeps.
/// </summary>
public sealed record OnceBlock(
    SourceSpan Span,
    BeaconExpr Delay,
    string Unit,
    string RawUnit,
    SourceSpan UnitSpan,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconTopDecl(Span);

/// <summary>A read-only value export: <c>export set name to expr</c>, readable via <c>call</c> and C# call-ins.</summary>
public sealed record ExportValueDecl(
    SourceSpan Span,
    string Name,
    SourceSpan NameSpan,
    BeaconExpr Value) : BeaconTopDecl(Span);

/// <summary>A (possibly exported) function definition.</summary>
public sealed record FunctionDef(
    SourceSpan Span,
    bool IsExport,
    SourceSpan? ExportSpan,
    string Name,
    SourceSpan NameSpan,
    IReadOnlyList<BeaconParam> Params,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconTopDecl(Span);

/// <summary>A script-registered Brigadier command block.</summary>
public sealed record CommandBlock(
    SourceSpan Span,
    string Pattern,
    SourceSpan PatternSpan,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconTopDecl(Span);

/// <summary>A bare statement at the top level (also a valid top-level declaration).</summary>
public sealed record TopStatement(
    SourceSpan Span,
    BeaconStatement Statement) : BeaconTopDecl(Span);

/// <summary>A brace-free statement list; terminators (<c>end</c>/<c>else</c>/<c>catch</c>) belong to the caller.</summary>
public sealed record BeaconBlock(
    SourceSpan Span,
    IReadOnlyList<BeaconStatement> Statements) : BeaconNode(Span);

/// <summary>All statement shapes.</summary>
public abstract record BeaconStatement(SourceSpan Span) : BeaconNode(Span);

/// <summary>An assignment target: <c>ident { "." ident | "[" expr "]" }</c>.</summary>
public sealed record BeaconTarget(
    SourceSpan Span,
    string Base,
    SourceSpan BaseSpan,
    IReadOnlyList<BeaconTargetPart> Parts) : BeaconNode(Span);

/// <summary>One trailer on a <see cref="BeaconTarget"/>.</summary>
public abstract record BeaconTargetPart(SourceSpan Span) : BeaconNode(Span);

/// <summary>A <c>.name</c> trailer.</summary>
public sealed record TargetMember(SourceSpan Span, string Name, SourceSpan NameSpan) : BeaconTargetPart(Span);

/// <summary>A <c>[expr]</c> trailer.</summary>
public sealed record TargetIndex(SourceSpan Span, BeaconExpr Index) : BeaconTargetPart(Span);

/// <summary><c>set target to expr</c>.</summary>
public sealed record SetStmt(SourceSpan Span, BeaconTarget Target, BeaconExpr Value) : BeaconStatement(Span);

/// <summary><c>say expr</c>.</summary>
public sealed record SayStmt(SourceSpan Span, BeaconExpr Message) : BeaconStatement(Span);

/// <summary><c>whisper player-expr text-expr</c>.</summary>
public sealed record WhisperStmt(SourceSpan Span, BeaconExpr Player, BeaconExpr Message) : BeaconStatement(Span);

/// <summary><c>server expr</c> (leading slash required at runtime; lexing owns the bare-slash error).</summary>
public sealed record ServerStmt(SourceSpan Span, BeaconExpr Command) : BeaconStatement(Span);

/// <summary><c>disconnect [reason-expr]</c>: leave the server cleanly and stay out.</summary>
public sealed record DisconnectStmt(SourceSpan Span, BeaconExpr? Reason) : BeaconStatement(Span);

/// <summary><c>show expr</c>; <c>IsLog</c> marks the forgiven <c>log expr</c> spelling.</summary>
public sealed record ShowStmt(SourceSpan Span, BeaconExpr Message, bool IsLog = false) : BeaconStatement(Span);

/// <summary><c>wait count unit</c>.</summary>
public sealed record WaitStmt(
    SourceSpan Span,
    BeaconExpr Count,
    string Unit,
    string RawUnit,
    SourceSpan UnitSpan) : BeaconStatement(Span);

/// <summary><c>stop [event]</c>; <c>IsCancelAlias</c> marks the forgiven <c>cancel event</c> spelling.</summary>
public sealed record StopStmt(SourceSpan Span, bool HasEvent, bool IsCancelAlias = false) : BeaconStatement(Span);

/// <summary>Which <c>skip</c>-family word was written; desugar maps break to stop, continue to skip.</summary>
public enum BeaconSkipKind
{
    /// <summary>Canonical continue.</summary>
    Skip,
    /// <summary>Forgiven alias for stop.</summary>
    Break,
    /// <summary>Forgiven alias for skip.</summary>
    Continue,
}

/// <summary><c>skip | break | continue</c>.</summary>
public sealed record SkipStmt(SourceSpan Span, BeaconSkipKind Kind) : BeaconStatement(Span);

/// <summary><c>return [expr]</c>.</summary>
public sealed record ReturnStmt(SourceSpan Span, BeaconExpr? Value) : BeaconStatement(Span);

/// <summary><c>save key-expr to value-expr</c>.</summary>
public sealed record SaveStmt(SourceSpan Span, BeaconExpr Key, BeaconExpr Value) : BeaconStatement(Span);

/// <summary><c>lock shared block end [lock]</c>.</summary>
public sealed record LockStmt(
    SourceSpan Span,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconStatement(Span);

/// <summary><c>start postfix</c> (a task call).</summary>
public sealed record StartStmt(SourceSpan Span, BeaconExpr Task) : BeaconStatement(Span);

/// <summary><c>await expr</c>.</summary>
public sealed record AwaitStmt(SourceSpan Span, BeaconExpr Task) : BeaconStatement(Span);

/// <summary><c>cancel task expr</c>.</summary>
public sealed record CancelTaskStmt(SourceSpan Span, BeaconExpr Task) : BeaconStatement(Span);

/// <summary>One <c>if</c>/<c>else if</c> branch.</summary>
public sealed record BeaconIfBranch(BeaconExpr Cond, BeaconBlock Body, SourceSpan Span) : BeaconNode(Span);

/// <summary><c>if</c> with else-if chains and an optional else; dangling else binds nearest by construction.</summary>
public sealed record IfStmt(
    SourceSpan Span,
    IReadOnlyList<BeaconIfBranch> Branches,
    BeaconBlock? ElseBody,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconStatement(Span);

/// <summary><c>while cond block end [while]</c>.</summary>
public sealed record WhileStmt(
    SourceSpan Span,
    BeaconExpr Cond,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconStatement(Span);

/// <summary><c>repeat count times block end [repeat]</c>.</summary>
public sealed record RepeatStmt(
    SourceSpan Span,
    BeaconExpr Count,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconStatement(Span);

/// <summary><c>for each var in expr block end [for]</c>.</summary>
public sealed record ForStmt(
    SourceSpan Span,
    string Var,
    SourceSpan VarSpan,
    BeaconExpr Iterable,
    BeaconBlock Body,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel) : BeaconStatement(Span);

/// <summary><c>try block catch var block [finally block] end [try]</c>.</summary>
public sealed record TryStmt(
    SourceSpan Span,
    BeaconBlock Body,
    string CatchVar,
    SourceSpan CatchVarSpan,
    BeaconBlock CatchBody,
    string? EndLabel,
    SourceSpan? EndSpan,
    bool EndLabelMissing,
    string SuggestedEndLabel,
    BeaconBlock? FinallyBody = null) : BeaconStatement(Span);

/// <summary>A bare expression used as a statement (calls, arithmetic, member chains).</summary>
public sealed record ExprStmt(SourceSpan Span, BeaconExpr Expr) : BeaconStatement(Span);

/// <summary>All expression shapes, lowest precedence first.</summary>
public abstract record BeaconExpr(SourceSpan Span) : BeaconNode(Span);

/// <summary><c>a or b</c> (also <c>||</c>); returns operands, never coerced; coercion is a runtime concern.</summary>
public sealed record OrExpr(SourceSpan Span, BeaconExpr Left, BeaconExpr Right) : BeaconExpr(Span);

/// <summary><c>a and b</c> (also <c>&amp;&amp;</c>); returns operands.</summary>
public sealed record AndExpr(SourceSpan Span, BeaconExpr Left, BeaconExpr Right) : BeaconExpr(Span);

/// <summary><c>not x</c> (also <c>!x</c>); chains right-associatively.</summary>
public sealed record NotExpr(SourceSpan Span, BeaconExpr Operand, bool IsBang = false) : BeaconExpr(Span);

/// <summary>Comparison operators, including the word forms and emptiness/presence tests.</summary>
public enum BeaconComparisonOp
{
    /// <summary><c>is</c>.</summary>
    Is,
    /// <summary><c>is not</c>.</summary>
    IsNot,
    /// <summary><c>==</c> (alias, desugars to <see cref="Is"/>).</summary>
    Equal,
    /// <summary><c>!=</c> (alias, desugars to <see cref="IsNot"/>).</summary>
    NotEqual,
    /// <summary><c>&lt;</c>.</summary>
    Less,
    /// <summary><c>&gt;</c>.</summary>
    Greater,
    /// <summary><c>&lt;=</c>.</summary>
    LessEqual,
    /// <summary><c>&gt;=</c>.</summary>
    GreaterEqual,
    /// <summary><c>contains</c>.</summary>
    Contains,
    /// <summary><c>matches</c>.</summary>
    Matches,
    /// <summary><c>starts with</c>.</summary>
    StartsWith,
    /// <summary><c>ends with</c>.</summary>
    EndsWith,
    /// <summary><c>is empty</c> (no right operand).</summary>
    IsEmpty,
    /// <summary><c>is not empty</c> (no right operand).</summary>
    IsNotEmpty,
    /// <summary><c>is set</c> (no right operand).</summary>
    IsSet,
    /// <summary><c>is not set</c> (no right operand).</summary>
    IsNotSet,
}

/// <summary>A comparison; <see cref="Right"/> is null for the four nullary presence tests.</summary>
public sealed record ComparisonExpr(
    SourceSpan Span,
    BeaconComparisonOp Op,
    BeaconExpr Left,
    BeaconExpr? Right) : BeaconExpr(Span);

/// <summary><c>a + b | a - b</c>.</summary>
public sealed record AddExpr(SourceSpan Span, string Op, BeaconExpr Left, BeaconExpr Right) : BeaconExpr(Span);

/// <summary><c>a * b | a / b | a % b</c>.</summary>
public sealed record MulExpr(SourceSpan Span, string Op, BeaconExpr Left, BeaconExpr Right) : BeaconExpr(Span);

/// <summary>Unary <c>-x</c>.</summary>
public sealed record NegateExpr(SourceSpan Span, BeaconExpr Operand) : BeaconExpr(Span);

/// <summary><c>target.name</c>.</summary>
public sealed record MemberExpr(
    SourceSpan Span,
    BeaconExpr Target,
    string Member,
    SourceSpan MemberSpan) : BeaconExpr(Span);

/// <summary><c>target[index]</c>.</summary>
public sealed record IndexExpr(SourceSpan Span, BeaconExpr Target, BeaconExpr Index) : BeaconExpr(Span);

/// <summary><c>target(args)</c>.</summary>
public sealed record CallExpr(
    SourceSpan Span,
    BeaconExpr Target,
    IReadOnlyList<BeaconExpr> Args) : BeaconExpr(Span);

/// <summary>A number literal.</summary>
public sealed record NumberLiteral(SourceSpan Span, double Value, string Raw) : BeaconExpr(Span);

/// <summary>One part of a text literal: literal characters or a parsed hole.</summary>
public abstract record BeaconTextNode(SourceSpan Span) : BeaconNode(Span);

/// <summary>Literal characters inside text.</summary>
public sealed record TextChunk(SourceSpan Span, string Value) : BeaconTextNode(Span);

/// <summary>An interpolation hole with its parsed expression.</summary>
public sealed record TextHole(SourceSpan Span, BeaconExpr Expr) : BeaconTextNode(Span);

/// <summary>Double- or triple-quoted text with holes.</summary>
public sealed record TextLiteral(
    SourceSpan Span,
    IReadOnlyList<BeaconTextNode> Parts,
    bool IsTriple) : BeaconExpr(Span);

/// <summary>A <c>/pattern/</c> literal.</summary>
public sealed record RegexLiteral(SourceSpan Span, string Pattern, string Raw) : BeaconExpr(Span);

/// <summary><c>yes</c>.</summary>
public sealed record YesLiteral(SourceSpan Span) : BeaconExpr(Span);

/// <summary><c>no</c>.</summary>
public sealed record NoLiteral(SourceSpan Span) : BeaconExpr(Span);

/// <summary><c>none</c>.</summary>
public sealed record NoneLiteral(SourceSpan Span) : BeaconExpr(Span);

/// <summary><c>call "script.fn"(args)</c>.</summary>
public sealed record CallPrimExpr(
    SourceSpan Span,
    string Target,
    SourceSpan TargetSpan,
    IReadOnlyList<BeaconExpr> Args) : BeaconExpr(Span);

/// <summary>A variable or builtin name (including Unit spellings used as names).</summary>
public sealed record IdentExpr(SourceSpan Span, string Name) : BeaconExpr(Span);

/// <summary><c>[a, b]</c>.</summary>
public sealed record ListLiteral(SourceSpan Span, IReadOnlyList<BeaconExpr> Items) : BeaconExpr(Span);

/// <summary>One map entry: ident or string key plus value.</summary>
public sealed record BeaconMapEntry(string Key, SourceSpan KeySpan, BeaconExpr Value, SourceSpan Span)
    : BeaconNode(Span);

/// <summary><c>{k: v, ...}</c>.</summary>
public sealed record MapLiteral(SourceSpan Span, IReadOnlyList<BeaconMapEntry> Entries) : BeaconExpr(Span);

/// <summary>Parenthesized expression (transparent to the boolean rule).</summary>
public sealed record ParenExpr(SourceSpan Span, BeaconExpr Inner) : BeaconExpr(Span);

/// <summary>Bare <c>mcc expr</c> (forgiven paren-less form of the internal-command call).</summary>
public sealed record MccExpr(SourceSpan Span, BeaconExpr Argument) : BeaconExpr(Span);

/// <summary>Placeholder where a hole or operand failed to parse; never evaluated (diagnostics fail first).</summary>
public sealed record ErrorExpr(SourceSpan Span) : BeaconExpr(Span);

/// <summary>
/// Canonicalizes forgiven spellings with chained spans: <c>!</c> to <c>not</c>, <c>==</c> to <c>is</c>, <c>!=</c> to <c>is not</c>, <c>cancel event</c> to <c>stop event</c>, <c>break</c> to <c>stop</c>, <c>continue</c> to <c>skip</c>, and fills missing end labels from the recorded completion data.
/// Every rebuilt span chains <c>DesugaredFrom</c> at the original so trace renders the user's own text.
/// </summary>
public static class BeaconDesugar
{
    /// <summary>Desugars a whole script, preserving <see cref="SourceSpan.Origin"/> throughout.</summary>
    public static BeaconScript Desugar(BeaconScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return new BeaconScript(
            Respan(script.Span),
            script.Major,
            script.Imports,
            script.Externs,
            script.Decls.Select(DesugarTop).ToList());
    }

    private static SourceSpan Respan(SourceSpan span) =>
        new(span.File, span.Line, span.Column, span.Length, desugaredFrom: span);

    private static BeaconTopDecl DesugarTop(BeaconTopDecl decl) => decl switch
    {
        OnBlock b => b with
        {
            Span = Respan(b.Span),
            Cooldown = b.Cooldown is null ? null : DesugarCooldown(b.Cooldown),
            When = b.When is null ? null : DesugarExpr(b.When),
            Body = DesugarBlock(b.Body),
            EndLabel = b.EndLabel ?? b.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        EveryBlock b => b with
        {
            Span = Respan(b.Span),
            Interval = DesugarExpr(b.Interval),
            Body = DesugarBlock(b.Body),
            EndLabel = b.EndLabel ?? b.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        OnceBlock b => b with
        {
            Span = Respan(b.Span),
            Delay = DesugarExpr(b.Delay),
            Body = DesugarBlock(b.Body),
            EndLabel = b.EndLabel ?? b.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        ExportValueDecl b => b with
        {
            Span = Respan(b.Span),
            Value = DesugarExpr(b.Value),
        },
        FunctionDef b => b with
        {
            Span = Respan(b.Span),
            Body = DesugarBlock(b.Body),
            EndLabel = b.EndLabel ?? b.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        CommandBlock b => b with
        {
            Span = Respan(b.Span),
            Body = DesugarBlock(b.Body),
            EndLabel = b.EndLabel ?? b.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        TopStatement t => t with { Span = Respan(t.Span), Statement = DesugarStmt(t.Statement) },
        _ => decl,
    };

    private static BeaconCooldown DesugarCooldown(BeaconCooldown cooldown) => cooldown with
    {
        Span = Respan(cooldown.Span),
        Count = DesugarExpr(cooldown.Count),
    };

    private static BeaconBlock DesugarBlock(BeaconBlock block) => block with
    {
        Span = Respan(block.Span),
        Statements = block.Statements.Select(DesugarStmt).ToList(),
    };

    private static BeaconTarget DesugarTarget(BeaconTarget target) => target with
    {
        Span = Respan(target.Span),
        Parts = target.Parts.Select<BeaconTargetPart, BeaconTargetPart>(part => part switch
        {
            TargetMember m => m with { Span = Respan(m.Span) },
            TargetIndex i => i with { Span = Respan(i.Span), Index = DesugarExpr(i.Index) },
            _ => part,
        }).ToList(),
    };

    /// <summary>Desugars one statement (also used by evaluator tests).</summary>
    public static BeaconStatement DesugarStmt(BeaconStatement stmt) => stmt switch
    {
        SetStmt s => s with { Span = Respan(s.Span), Target = DesugarTarget(s.Target), Value = DesugarExpr(s.Value) },
        SayStmt s => s with { Span = Respan(s.Span), Message = DesugarExpr(s.Message) },
        WhisperStmt s => s with { Span = Respan(s.Span), Player = DesugarExpr(s.Player), Message = DesugarExpr(s.Message) },
        ServerStmt s => s with { Span = Respan(s.Span), Command = DesugarExpr(s.Command) },
        DisconnectStmt s => s with { Span = Respan(s.Span), Reason = s.Reason is null ? null : DesugarExpr(s.Reason) },
        ShowStmt s => s with { Span = Respan(s.Span), Message = DesugarExpr(s.Message) },
        WaitStmt s => s with { Span = Respan(s.Span), Count = DesugarExpr(s.Count) },
        StopStmt s => s with { Span = Respan(s.Span), IsCancelAlias = false },
        SkipStmt s when s.Kind == BeaconSkipKind.Break =>
            new StopStmt(Respan(s.Span), HasEvent: false),
        SkipStmt s when s.Kind == BeaconSkipKind.Continue =>
            s with { Span = Respan(s.Span), Kind = BeaconSkipKind.Skip },
        SkipStmt s => s with { Span = Respan(s.Span) },
        ReturnStmt s => s with { Span = Respan(s.Span), Value = s.Value is null ? null : DesugarExpr(s.Value) },
        SaveStmt s => s with { Span = Respan(s.Span), Key = DesugarExpr(s.Key), Value = DesugarExpr(s.Value) },
        LockStmt s => s with
        {
            Span = Respan(s.Span),
            Body = DesugarBlock(s.Body),
            EndLabel = s.EndLabel ?? s.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        StartStmt s => s with { Span = Respan(s.Span), Task = DesugarExpr(s.Task) },
        AwaitStmt s => s with { Span = Respan(s.Span), Task = DesugarExpr(s.Task) },
        CancelTaskStmt s => s with { Span = Respan(s.Span), Task = DesugarExpr(s.Task) },
        IfStmt s => s with
        {
            Span = Respan(s.Span),
            Branches = s.Branches.Select(b => b with
            {
                Span = Respan(b.Span),
                Cond = DesugarExpr(b.Cond),
                Body = DesugarBlock(b.Body),
            }).ToList(),
            ElseBody = s.ElseBody is null ? null : DesugarBlock(s.ElseBody),
            EndLabel = s.EndLabel ?? s.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        WhileStmt s => s with
        {
            Span = Respan(s.Span),
            Cond = DesugarExpr(s.Cond),
            Body = DesugarBlock(s.Body),
            EndLabel = s.EndLabel ?? s.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        RepeatStmt s => s with
        {
            Span = Respan(s.Span),
            Count = DesugarExpr(s.Count),
            Body = DesugarBlock(s.Body),
            EndLabel = s.EndLabel ?? s.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        ForStmt s => s with
        {
            Span = Respan(s.Span),
            Iterable = DesugarExpr(s.Iterable),
            Body = DesugarBlock(s.Body),
            EndLabel = s.EndLabel ?? s.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        TryStmt s => s with
        {
            Span = Respan(s.Span),
            Body = DesugarBlock(s.Body),
            CatchBody = DesugarBlock(s.CatchBody),
            FinallyBody = s.FinallyBody is null ? null : DesugarBlock(s.FinallyBody),
            EndLabel = s.EndLabel ?? s.SuggestedEndLabel,
            EndLabelMissing = false,
        },
        ExprStmt s => s with { Span = Respan(s.Span), Expr = DesugarExpr(s.Expr) },
        _ => stmt,
    };

    /// <summary>Desugars one expression (also used by evaluator tests).</summary>
    public static BeaconExpr DesugarExpr(BeaconExpr expr) => expr switch
    {
        OrExpr e => e with { Span = Respan(e.Span), Left = DesugarExpr(e.Left), Right = DesugarExpr(e.Right) },
        AndExpr e => e with { Span = Respan(e.Span), Left = DesugarExpr(e.Left), Right = DesugarExpr(e.Right) },
        NotExpr e => e with { Span = Respan(e.Span), Operand = DesugarExpr(e.Operand), IsBang = false },
        ComparisonExpr e when e.Op == BeaconComparisonOp.Equal => e with
        {
            Span = Respan(e.Span),
            Op = BeaconComparisonOp.Is,
            Right = e.Right is null ? null : DesugarExpr(e.Right),
            Left = DesugarExpr(e.Left),
        },
        ComparisonExpr e when e.Op == BeaconComparisonOp.NotEqual => e with
        {
            Span = Respan(e.Span),
            Op = BeaconComparisonOp.IsNot,
            Right = e.Right is null ? null : DesugarExpr(e.Right),
            Left = DesugarExpr(e.Left),
        },
        ComparisonExpr e => e with
        {
            Span = Respan(e.Span),
            Left = DesugarExpr(e.Left),
            Right = e.Right is null ? null : DesugarExpr(e.Right),
        },
        AddExpr e => e with { Span = Respan(e.Span), Left = DesugarExpr(e.Left), Right = DesugarExpr(e.Right) },
        MulExpr e => e with { Span = Respan(e.Span), Left = DesugarExpr(e.Left), Right = DesugarExpr(e.Right) },
        NegateExpr e => e with { Span = Respan(e.Span), Operand = DesugarExpr(e.Operand) },
        MemberExpr e => e with { Span = Respan(e.Span), Target = DesugarExpr(e.Target) },
        IndexExpr e => e with { Span = Respan(e.Span), Target = DesugarExpr(e.Target), Index = DesugarExpr(e.Index) },
        CallExpr e => e with
        {
            Span = Respan(e.Span),
            Target = DesugarExpr(e.Target),
            Args = e.Args.Select(DesugarExpr).ToList(),
        },
        TextLiteral e => e with
        {
            Span = Respan(e.Span),
            Parts = e.Parts.Select<BeaconTextNode, BeaconTextNode>(part => part switch
            {
                TextChunk c => c with { Span = Respan(c.Span) },
                TextHole h => h with { Span = Respan(h.Span), Expr = DesugarExpr(h.Expr) },
                _ => part,
            }).ToList(),
        },
        CallPrimExpr e => e with { Span = Respan(e.Span), Args = e.Args.Select(DesugarExpr).ToList() },
        ListLiteral e => e with { Span = Respan(e.Span), Items = e.Items.Select(DesugarExpr).ToList() },
        MapLiteral e => e with
        {
            Span = Respan(e.Span),
            Entries = e.Entries.Select(en => en with
            {
                Span = Respan(en.Span),
                Value = DesugarExpr(en.Value),
            }).ToList(),
        },
        ParenExpr e => e with { Span = Respan(e.Span), Inner = DesugarExpr(e.Inner) },
        MccExpr e => e with { Span = Respan(e.Span), Argument = DesugarExpr(e.Argument) },
        _ => expr,
    };
}
