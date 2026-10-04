namespace DMCBK.Core.Beacon;

/// <summary>
/// Structural static checks handed off by the parser: yes/no conditions, numeric counts, end-label match, top-level-only nesting, export placement, unknown hooks, and unit placement.
/// Each carries a beginner-worded message plus a paste-ready suggestion.
/// Runtime type judgments (mixed <c>+</c>, unknown variables, call arity) are runtime errors, judged by the interpreter.
/// </summary>
public static class BeaconStaticCheck
{
    private static readonly HashSet<string> KnownEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "chat", "whisper", "server_message", "raw_chat", "join", "leave", "death", "respawn",
        "health", "hunger", "inventory", "login", "logout", "disconnect", "reconnect",
        "kick", "tps", "start", "player_list", "container_open", "container_close",
        "entity_add", "entity_remove", "dialog",
    };

    private static readonly string HookList =
        "chat, whisper, server_message, raw_chat, join, leave, death, respawn, health, hunger, inventory, login, logout, disconnect, reconnect, kick, tps, start, player_list, container_open, container_close, entity_add, entity_remove, dialog";

    /// <summary>Runs every structural check over <paramref name="script"/>.</summary>
    public static IReadOnlyList<BeaconDiagnostic> Check(BeaconScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var diagnostics = new List<BeaconDiagnostic>();
        foreach (BeaconTopDecl decl in script.Decls)
            CheckTopDecl(decl, diagnostics);

        BeaconPipeline.SortByLocation(diagnostics);
        return diagnostics;
    }

    private static void CheckTopDecl(BeaconTopDecl decl, List<BeaconDiagnostic> diagnostics)
    {
        switch (decl)
        {
            case OnBlock on:
                CheckUnknownHook(on, diagnostics);
                if (on.Cooldown is not null)
                {
                    CheckNumeric(on.Cooldown.Count, "cooldown", diagnostics);
                    CheckCooldownUnit(on.Cooldown, diagnostics);
                }

                if (on.When is not null)
                    CheckBoolean(on.When, "when", diagnostics);

                CheckBlock(on.Body, diagnostics);
                CheckEndLabel(on.EndLabel, "on", on.EndSpan, diagnostics);
                break;
            case EveryBlock every:
                CheckNumeric(every.Interval, "every", diagnostics);
                CheckEveryUnit(every, diagnostics);
                CheckBlock(every.Body, diagnostics);
                CheckEndLabel(every.EndLabel, "every", every.EndSpan, diagnostics);
                break;
            case OnceBlock once:
                CheckNumeric(once.Delay, "in", diagnostics);
                CheckOnceUnit(once, diagnostics);
                CheckBlock(once.Body, diagnostics);
                CheckEndLabel(once.EndLabel, "in", once.EndSpan, diagnostics);
                break;
            case ExportValueDecl exported:
                if (string.IsNullOrWhiteSpace(exported.Name))
                {
                    diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.Parse,
                        BeaconSeverity.Error,
                        "I expected a name after 'export set', but found none.",
                        exported.NameSpan,
                        "Write export set coins to {copper: 5}."));
                }

                break;
            case FunctionDef function:
                CheckBlock(function.Body, diagnostics);
                CheckEndLabel(function.EndLabel, "function", function.EndSpan, diagnostics);
                break;
            case CommandBlock command:
                CheckBlock(command.Body, diagnostics);
                CheckEndLabel(command.EndLabel, "command", command.EndSpan, diagnostics);
                break;
            case TopStatement top:
                CheckStatement(top.Statement, diagnostics);
                break;
            default:
                break;
        }
    }

    private static void CheckBlock(BeaconBlock block, List<BeaconDiagnostic> diagnostics)
    {
        foreach (BeaconStatement statement in block.Statements)
            CheckStatement(statement, diagnostics);
    }

    private static void CheckStatement(BeaconStatement statement, List<BeaconDiagnostic> diagnostics)
    {
        switch (statement)
        {
            case NestedDeclStmt nested:
                diagnostics.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.Parse,
                    BeaconSeverity.Error,
                    $"I expected a statement, but found '{DescribeDecl(nested.Decl)}' nested inside a block. Blocks on/every/function/command live at the top level only.",
                    nested.Span,
                    "Move this block to the top level of the file."));
                if (nested.Decl is FunctionDef nestedFunction && nestedFunction.IsExport)
                {
                    diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.Parse,
                        BeaconSeverity.Error,
                        "I expected plain 'function', but found 'export function' nested inside a block. Export appears only at the top level.",
                        nested.Span,
                        "Move the exported function to the top level of the file."));
                }

                if (nested.Decl is ExportValueDecl)
                {
                    diagnostics.Add(new BeaconDiagnostic(
                        BeaconDiagnosticCodes.Parse,
                        BeaconSeverity.Error,
                        "I expected plain 'set', but found 'export set' nested inside a block. Export appears only at the top level.",
                        nested.Span,
                        "Move the exported value to the top level of the file."));
                }

                CheckTopDecl(nested.Decl, diagnostics);
                break;
            case WaitStmt wait:
                CheckNumeric(wait.Count, "wait", diagnostics);
                CheckWaitUnit(wait, diagnostics);
                break;
            case IfStmt ifStmt:
                foreach (BeaconIfBranch branch in ifStmt.Branches)
                {
                    CheckBoolean(branch.Cond, "if", diagnostics);
                    CheckBlock(branch.Body, diagnostics);
                }

                if (ifStmt.ElseBody is not null)
                    CheckBlock(ifStmt.ElseBody, diagnostics);

                CheckEndLabel(ifStmt.EndLabel, "if", ifStmt.EndSpan, diagnostics);
                break;
            case WhileStmt whileStmt:
                CheckBoolean(whileStmt.Cond, "while", diagnostics);
                CheckBlock(whileStmt.Body, diagnostics);
                CheckEndLabel(whileStmt.EndLabel, "while", whileStmt.EndSpan, diagnostics);
                break;
            case RepeatStmt repeat:
                CheckBlock(repeat.Body, diagnostics);
                CheckEndLabel(repeat.EndLabel, "repeat", repeat.EndSpan, diagnostics);
                break;
            case ForStmt forStmt:
                CheckBlock(forStmt.Body, diagnostics);
                CheckEndLabel(forStmt.EndLabel, "for", forStmt.EndSpan, diagnostics);
                break;
            case TryStmt tryStmt:
                CheckBlock(tryStmt.Body, diagnostics);
                CheckBlock(tryStmt.CatchBody, diagnostics);
                if (tryStmt.FinallyBody is not null)
                    CheckBlock(tryStmt.FinallyBody, diagnostics);

                CheckEndLabel(tryStmt.EndLabel, "try", tryStmt.EndSpan, diagnostics);
                break;
            case LockStmt lockStmt:
                CheckBlock(lockStmt.Body, diagnostics);
                CheckEndLabel(lockStmt.EndLabel, "lock", lockStmt.EndSpan, diagnostics);
                break;
            default:
                break;
        }
    }

    private static string DescribeDecl(BeaconTopDecl decl) => decl switch
    {
        OnBlock => "on",
        EveryBlock => "every",
        OnceBlock => "in",
        FunctionDef => "function",
        ExportValueDecl => "export",
        CommandBlock => "command",
        _ => "a block",
    };

    /// <summary>
    /// Structural boolean rule: a condition passes when it is a comparison, an and/or/not combination, a call, a yes/no literal, or a paren around those.
    /// <c>not x</c> always passes statically (only runtime types can judge it); everything else rejects with B3001.
    /// </summary>
    public static bool IsStructurallyBoolean(BeaconExpr expr)
    {
        ArgumentNullException.ThrowIfNull(expr);
        return expr switch
        {
            ComparisonExpr => true,
            AndExpr and => IsStructurallyBoolean(and.Left) && IsStructurallyBoolean(and.Right),
            OrExpr or => IsStructurallyBoolean(or.Left) && IsStructurallyBoolean(or.Right),
            NotExpr => true,
            YesLiteral => true,
            NoLiteral => true,
            CallExpr => true,
            CallPrimExpr => true,
            DmcbkExpr => true,
            MemberExpr member => IsCallChain(member),
            ParenExpr paren => IsStructurallyBoolean(paren.Inner),
            _ => false,
        };
    }

    private static bool IsCallChain(BeaconExpr expr) => expr switch
    {
        CallExpr => true,
        MemberExpr member => IsCallChain(member.Target),
        IndexExpr index => IsCallChain(index.Target),
        ParenExpr paren => IsCallChain(paren.Inner),
        _ => false,
    };

    private static void CheckBoolean(BeaconExpr cond, string where, List<BeaconDiagnostic> diagnostics)
    {
        if (IsStructurallyBoolean(cond))
            return;

        string hint = cond switch
        {
            IdentExpr ident => $"Did you mean `{where} {ident.Name} > 0`? Or `{where} {ident.Name} is not empty` for lists and text.",
            NumberLiteral => $"Conditions need yes/no: compare the number, e.g. `{where} 1 > 0`.",
            _ => $"Conditions need yes/no: use a comparison (is, <, >, contains, matches), and/or/not, or a call.",
        };
        diagnostics.Add(new BeaconDiagnostic(
            BeaconDiagnosticCodes.StrictBooleanCondition,
            BeaconSeverity.Error,
            $"I expected a yes/no condition after '{where}', but found something else. {hint}",
            cond.Span,
            where == "when"
                ? "Write when message contains \"!help\" or when health < 6."
                : $"Write {where} x > 0 or {where} x is set."));
    }

    private static void CheckNumeric(BeaconExpr count, string where, List<BeaconDiagnostic> diagnostics)
    {
        BeaconExpr unwrapped = count;
        while (unwrapped is ParenExpr paren)
            unwrapped = paren.Inner;

        if (unwrapped is NumberLiteral)
            return;

        diagnostics.Add(new BeaconDiagnostic(
            BeaconDiagnosticCodes.Parse,
            BeaconSeverity.Error,
            $"I expected a number after '{where}', but found something else. Counts are plain numbers.",
            count.Span,
            where switch
            {
                "wait" => "Write wait 2 seconds.",
                "every" => "Write every 60 seconds.",
                "in" => "Write in 30 seconds do.",
                _ => "Write cooldown 300 seconds named \"label\".",
            }));
    }

    /// <summary>Validates end labels; bare <c>end</c> is tolerated (null label, no error).</summary>
    public static void CheckEndLabel(string? label, string expected, SourceSpan? labelSpan, List<BeaconDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        if (label is null)
            return;

        if (!string.Equals(label, expected, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.Parse,
                BeaconSeverity.Error,
                $"I expected 'end {expected}', but found 'end {label}'. End labels must match their opener.",
                labelSpan ?? new SourceSpan("unknown.bcn", 1, 1, 0),
                $"Write 'end {expected}'."));
        }
    }

    private static void CheckWaitUnit(WaitStmt wait, List<BeaconDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(wait.Unit))
            return;

        if (!BeaconUnits.AllowedAfterWait(wait.Unit))
        {
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.UnknownUnit,
                BeaconSeverity.Error,
                $"Unit '{wait.RawUnit}' is not allowed after 'wait'. Use one of: {string.Join(", ", BeaconUnits.WaitUnits)}.",
                wait.UnitSpan,
                "Write 'wait 2 seconds'."));
        }
    }

    private static void CheckEveryUnit(EveryBlock every, List<BeaconDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(every.Unit))
            return;

        if (!BeaconUnits.AllowedAfterEvery(every.Unit))
        {
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.UnknownUnit,
                BeaconSeverity.Error,
                $"Unit '{every.RawUnit}' is not allowed after 'every'. Use one of: {string.Join(", ", BeaconUnits.EveryUnits)}.",
                every.UnitSpan,
                "Write 'every 60 seconds'."));
        }
    }

    private static void CheckOnceUnit(OnceBlock once, List<BeaconDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(once.Unit))
            return;

        if (!BeaconUnits.AllowedAfterOnce(once.Unit))
        {
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.UnknownUnit,
                BeaconSeverity.Error,
                $"Unit '{once.RawUnit}' is not allowed after 'in'. Use one of: {string.Join(", ", BeaconUnits.OnceUnits)}.",
                once.UnitSpan,
                "Write 'in 30 seconds do'."));
        }
    }

    private static void CheckCooldownUnit(BeaconCooldown cooldown, List<BeaconDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(cooldown.Unit))
            return;

        if (!BeaconUnits.AllowedAfterCooldown(cooldown.Unit))
        {
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.UnknownUnit,
                BeaconSeverity.Error,
                $"Unit '{cooldown.RawUnit}' is not allowed after 'cooldown'. Use one of: {string.Join(", ", BeaconUnits.CooldownUnits)}.",
                cooldown.UnitSpan,
                "Write 'cooldown 300 seconds named \"label\"'."));
        }
    }

    private static void CheckUnknownHook(OnBlock on, List<BeaconDiagnostic> diagnostics)
    {
        if (KnownEvents.Contains(on.EventName) || BeaconHookCatalog.IsKnown(on.EventName))
            return;

        diagnostics.Add(new BeaconDiagnostic(
            BeaconDiagnosticCodes.UnknownEvent,
            BeaconSeverity.Warning,
            $"Unknown event '{on.EventName}'. Known hooks: {HookList}. Plugin-registered hooks (like shop_buy) warn until their plugin loads.",
            on.EventSpan,
            $"Use one of: {HookList}."));
    }
}
