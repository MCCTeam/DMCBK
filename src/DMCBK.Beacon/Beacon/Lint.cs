using System.Text.Json;

namespace DMCBK.Core.Beacon;

/// <summary>Extended lint options: strict escalation and library targeting.</summary>
/// <param name="Strict">Escalate unresolvable <c>extern</c>/<c>call</c> and missing providers to errors.</param>
/// <param name="TargetLib">Flag builtins newer than this <c>beacon.lib</c> version; null targets current.</param>
public sealed record BeaconLintOptions(bool Strict = false, int? TargetLib = null);

/// <summary>
/// One file's extended lint result: the shared-engine diagnostics both frontends render, the transitive permission union, and the safe-fix preview.
/// </summary>
/// <param name="Path">The file path as given.</param>
/// <param name="Ok">True when no error diagnostic was reported (warnings and notes allowed).</param>
/// <param name="Diagnostics">All diagnostics, sorted by line then column.</param>
/// <param name="Permissions">The transitive capability union (this file plus chased imports/calls).</param>
/// <param name="FixPreview">The safe-fix preview (never null for a parsed file).</param>
public sealed record BeaconLintReport(
    string Path,
    bool Ok,
    IReadOnlyList<BeaconDiagnostic> Diagnostics,
    IReadOnlySet<string> Permissions,
    BeaconFixPreview? FixPreview);

/// <summary>One applied headless fix, for the JSON <c>fixes</c> array.</summary>
/// <param name="Path">The file that was rewritten.</param>
/// <param name="Applied">True when the preview applied and the file was rewritten.</param>
/// <param name="Diff">The preview diff that was printed before rewriting.</param>
public sealed record BeaconLintFixRecord(string Path, bool Applied, string Diff);

/// <summary>
/// The shared lint engine: BOTH frontends (in-client <c>/scripts lint</c> and headless <c>lint</c>) call this, so interactive and headless output can never disagree.
/// Base diagnostics come out of a scratch <see cref="BeaconEngine"/> (the one pipeline), then the extended passes add import/<c>call</c> chasing with permission union, <c>--target-lib</c> gating, <c>--strict</c> escalation, and forgiven-form notes.
/// <para/>
/// Fail-closed throughout: an unreadable import, a cycle, or a manifest gap is an error, never a shrug.
/// What the engine cannot verify offline (<c>extern</c>/<c>call</c> providers have no registry yet) warns, and <c>--strict</c> escalates it for CI gates.
/// </summary>
public static class BeaconLint
{
    /// <summary>The headless exit-code contract: 0 clean (warnings allowed), 1 errors, 2 usage.</summary>
    public static class ExitCodes
    {
        /// <summary>Clean (warnings and notes allowed).</summary>
        public const int Clean = 0;

        /// <summary>At least one error diagnostic.</summary>
        public const int Errors = 1;

        /// <summary>CLI misuse (bad flags, no inputs, unreadable input file).</summary>
        public const int Usage = 2;
    }

    /// <summary>The current <c>beacon.lib</c> version (the interop surface is lib 2).</summary>
    public const int CurrentLibVersion = 2;

    private const int LibWithInterop = 2;
    private const int MaxClosureFiles = 32;

    /// <summary>Lints one in-memory source; imports resolve against the path's directory on disk.</summary>
    public static BeaconLintReport LintSource(
        string path, string source, BeaconLintOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new BeaconLintOptions();

        var engine = new BeaconEngine(BeaconOfflineHost.Shared);
        var closure = new Closure(engine, options);
        return closure.LintRoot(path, source);
    }

    /// <summary>Lints files from disk.</summary>
    /// <exception cref="FileNotFoundException">Thrown for an unreadable input file (usage error).</exception>
    public static IReadOnlyList<BeaconLintReport> LintFiles(
        IReadOnlyList<string> paths, BeaconLintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        options ??= new BeaconLintOptions();

        var engine = new BeaconEngine(BeaconOfflineHost.Shared);
        var closure = new Closure(engine, options);
        var reports = new List<BeaconLintReport>(paths.Count);
        foreach (string path in paths)
        {
            string source;
            try
            {
                source = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new FileNotFoundException($"lint: cannot read '{path}': {ex.Message}", path, ex);
            }

            reports.Add(closure.LintRoot(path, source));
        }

        return reports;
    }

    /// <summary>Renders reports as the headless JSON document (stdout).</summary>
    public static string ToJson(
        IReadOnlyList<BeaconLintReport> reports, IReadOnlyList<BeaconLintFixRecord>? fixes = null)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var files = new List<object>(reports.Count);
        var diagnostics = new List<object>();
        int errors = 0;
        int warnings = 0;
        int notes = 0;
        foreach (BeaconLintReport report in reports)
        {
            files.Add(new
            {
                path = report.Path,
                ok = report.Ok,
                permissions = report.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            });

            foreach (BeaconDiagnostic diagnostic in report.Diagnostics)
            {
                diagnostics.Add(BeaconReportJson.ToDiagnosticJson(diagnostic));
                BeaconReportJson.Tally(diagnostic.Severity, ref errors, ref warnings, ref notes);
            }
        }

        object document = fixes is null
            ? (object)new
            {
                files,
                diagnostics,
                summary = new { errors, warnings, notes },
            }
            : new
            {
                files,
                diagnostics,
                summary = new { errors, warnings, notes },
                fixes = fixes.Select(f => new { path = f.Path, applied = f.Applied, diff = f.Diff }).ToArray(),
            };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed class Closure(BeaconEngine engine, BeaconLintOptions options)
    {
        private readonly Dictionary<string, FileEntry> _files = new(StringComparer.Ordinal);

        public BeaconLintReport LintRoot(string path, string source)
        {
            string rootDir = RootDirOf(path);
            var stack = new List<string>();
            FileEntry root = Visit(path, source, rootDir, stack);
            ComputeEffective(root);

            var diagnostics = new List<BeaconDiagnostic>();
            CollectReports(root, diagnostics, new HashSet<string>(StringComparer.Ordinal));

            diagnostics.Sort(static (a, b) =>
            {
                int file = string.Compare(a.Span.Origin.File, b.Span.Origin.File, StringComparison.Ordinal);
                if (file != 0)
                    return file;

                int line = a.Span.Origin.Line.CompareTo(b.Span.Origin.Line);
                return line != 0 ? line : a.Span.Origin.Column.CompareTo(b.Span.Origin.Column);
            });

            if (options.Strict)
                diagnostics = diagnostics.Select(BeaconLint.Escalate).ToList();

            var permissions = new SortedSet<string>(
                root.Effective ?? new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            bool ok = diagnostics.All(d => d.Severity != BeaconSeverity.Error);
            BeaconFixPreview? preview = root.Script is null ? null : BeaconFix.Preview(root.Display, root.Source);
            return new BeaconLintReport(path, ok, diagnostics, permissions, preview);
        }

        private static string RootDirOf(string path)
        {
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                full = path;
            }

            return Path.GetDirectoryName(full) ?? ".";
        }

        private FileEntry Visit(string path, string source, string rootDir, List<string> stack)
        {
            string abs = AbsoluteOf(path);
            if (_files.TryGetValue(abs, out FileEntry? known) && known is not null)
                return known;

            // Diagnostic spans carry the file NAME (not the as-given path): the headless JSON contract pins `"file": "quiz.mcc"` for a file linted by any path, and the closure keys identity by absolute path, so same-name files in different folders still lint independently while each reports its own short name.
            // Report.Path keeps the as-given path for the files[] array.
            string display = Path.GetFileName(path);
            if (string.IsNullOrEmpty(display))
                display = path;
            string id = "lint-" + Guid.NewGuid().ToString("N");
            engine.LoadSource(id, display, source);
            IReadOnlyList<BeaconDiagnostic> baseDiagnostics;
            try
            {
                baseDiagnostics = engine.Lint(id);
            }
            finally
            {
                engine.RemoveScript(id);
            }

            var entry = new FileEntry(abs, display, source, null, null, baseDiagnostics);
            _files[abs] = entry;

            BeaconParseData? parsed = TryParse(display, source);
            if (parsed is null)
                return entry;

            entry = entry with { Script = parsed.Script, Header = parsed.Header };
            _files[abs] = entry;

            stack.Add(abs);
            try
            {
                foreach (BeaconImport import in parsed.Script.Imports)
                    VisitImport(entry, import, rootDir, stack);

                foreach (CallPrimExpr call in WalkCalls(parsed.Script))
                    VisitCall(entry, call, rootDir, stack);

                foreach (BeaconExtern ext in parsed.Script.Externs)
                    entry.Extras.Add(DescribeExtern(ext));

                AddTargetLibNotes(entry, parsed.Script);
                AddForgivenNotes(entry, parsed.Script);
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }

            return entry;
        }

        private static BeaconDiagnostic DescribeExtern(BeaconExtern ext)
        {
            if (BeaconProviders.TryGetFunction(ext.Name, out string owner, out string capability))
            {
                if (string.Equals(owner, ext.PluginId, StringComparison.Ordinal))
                {
                    return new BeaconDiagnostic(
                        BeaconDiagnosticCodes.UnresolvedBridge,
                        BeaconSeverity.Warning,
                        $"Extern '{ext.Name}' from plugin '{ext.PluginId}' (capability '{capability}') cannot be verified offline: " +
                        "the provider is registered, but lint runs with no session, so the call is unchecked until load.",
                        ext.NameSpan.Origin,
                        $"Install the '{ext.PluginId}' plugin, or drop the extern until it exists.");
                }

                return new BeaconDiagnostic(
                    BeaconDiagnosticCodes.UnresolvedBridge,
                    BeaconSeverity.Warning,
                    $"Extern '{ext.Name}' names plugin '{ext.PluginId}', but loaded plugin '{owner}' offers it. " +
                    "The call fails at runtime until the names agree.",
                    ext.NameSpan.Origin,
                    $"Write extern {ext.Name} from \"{owner}\".");
            }

            if (BeaconProviders.TryGetVariable(ext.Name, out string variableOwner, out string variableCapability)
                && string.Equals(variableOwner, ext.PluginId, StringComparison.Ordinal))
            {
                return new BeaconDiagnostic(
                    BeaconDiagnosticCodes.UnresolvedBridge,
                    BeaconSeverity.Warning,
                    $"Extern '{ext.Name}' from plugin '{ext.PluginId}' (capability '{variableCapability}') names a variable namespace, not a function: " +
                        "read it as a map instead of declaring it.",
                    ext.NameSpan.Origin,
                    $"Drop the extern line and read {ext.Name}.<field> directly.");
            }

            return new BeaconDiagnostic(
                BeaconDiagnosticCodes.UnresolvedBridge,
                BeaconSeverity.Warning,
                $"Cannot verify extern '{ext.Name}' from plugin '{ext.PluginId}' offline: " +
                "no loaded provider offers it, so the call fails at runtime until one is installed.",
                ext.NameSpan.Origin,
                $"Install the '{ext.PluginId}' plugin, or drop the extern until it exists.");
        }

        private void VisitImport(FileEntry entry, BeaconImport import, string rootDir, List<string> stack)
        {
            string? abs = ResolveInside(import.Path, entry, rootDir);
            if (abs is null)
            {
                entry.Extras.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.ImportNotFound,
                    BeaconSeverity.Error,
                    $"Import '{import.Path}' leaves the script folder '{rootDir}'. "
                    + "Import paths resolve relative to the importing file and stay inside its folder.",
                    import.PathSpan.Origin,
                    "Move the library under the importing file's folder and import it by relative path."));
                return;
            }

            int cycleAt = stack.IndexOf(abs);
            if (cycleAt >= 0)
            {
                var chain = stack.Skip(cycleAt).Concat([abs]).ToList();
                entry.Extras.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.ImportCycle,
                    BeaconSeverity.Error,
                    "Circular import: " + string.Join(" -> ", chain) + ".",
                    import.PathSpan.Origin,
                    "Break the cycle by moving the shared code into a third file both import."));
                return;
            }

            string source;
            try
            {
                source = File.ReadAllText(abs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Extras.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.ImportNotFound,
                    BeaconSeverity.Error,
                    $"Import '{import.Path}' cannot be read ('{abs}': {ex.Message}). "
                    + "Lint fails closed: without the file its permissions cannot join the union.",
                    import.PathSpan.Origin,
                    $"Check that '{import.Path}' exists next to '{Path.GetFileName(entry.Display)}'."));
                return;
            }

            if (_files.Count >= MaxClosureFiles)
            {
                entry.Extras.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.ImportNotFound,
                    BeaconSeverity.Error,
                    $"Import '{import.Path}' exceeds the {MaxClosureFiles}-file lint closure. "
                    + "Split the library so the closure stays small.",
                    import.PathSpan.Origin,
                    "Import fewer files from this script."));
                return;
            }

            FileEntry child = Visit(abs, source, rootDir, stack);
            entry.Children.Add(child.AbsolutePath);
        }

        private void VisitCall(FileEntry entry, CallPrimExpr call, string rootDir, List<string> stack)
        {
            string target = call.Target;
            int dot = target.IndexOf('.');
            string scriptId = dot < 0 ? target : target[..dot];
            if (scriptId.Length == 0 || scriptId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || scriptId.Contains('/') || scriptId.Contains('\\'))
            {
                entry.Extras.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.UnresolvedBridge,
                    BeaconSeverity.Warning,
                    $"Cannot resolve call target '{target}' offline: it names no script file.",
                    call.TargetSpan.Origin,
                    "Write call \"script.function\"() with the owning script's file name first."));
                return;
            }

            // The id cannot hold separators (validated above), so the candidate always sits directly inside the root folder: inside the jail by construction.
            string candidate = Path.Combine(rootDir, scriptId + BeaconScriptDiscovery.ScriptExtension);
            string abs = AbsoluteOf(candidate);
            if (!File.Exists(abs))
            {
                entry.Extras.Add(UnresolvedCall(call, target, scriptId, entry));
                return;
            }

            if (_files.ContainsKey(abs) || stack.Contains(abs))
            {
                // Call edges join the closure without cycle errors: mutual calls are legal at runtime (fuel-bounded), so only import edges report B1005.
                if (!_files.ContainsKey(abs))
                    return;

                entry.Children.Add(abs);
                return;
            }

            string source;
            try
            {
                source = File.ReadAllText(abs);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Extras.Add(UnresolvedCall(call, target, scriptId, entry));
                return;
            }

            FileEntry child = Visit(abs, source, rootDir, stack);
            entry.Children.Add(child.AbsolutePath);
        }

        private static BeaconDiagnostic UnresolvedCall(
            CallPrimExpr call, string target, string scriptId, FileEntry entry) =>
            new(
                BeaconDiagnosticCodes.UnresolvedBridge,
                BeaconSeverity.Warning,
                $"Cannot resolve call target '{target}' offline: no '{scriptId}.mcc' sits beside "
                + $"'{Path.GetFileName(entry.Display)}' and there is no provider registry in this build.",
                call.TargetSpan.Origin,
                $"Add '{scriptId}.mcc' next to '{Path.GetFileName(entry.Display)}' or install the owning script.");

        private static string? ResolveInside(string rel, FileEntry entry, string rootDir)
        {
            string abs;
            try
            {
                if (Path.IsPathRooted(rel))
                    return null;

                string baseDir = Path.GetDirectoryName(entry.AbsolutePath) ?? rootDir;
                abs = Path.GetFullPath(Path.Combine(baseDir, rel));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }

            string fenced = rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!abs.StartsWith(fenced, StringComparison.Ordinal) && !string.Equals(abs, rootDir, StringComparison.Ordinal))
                return null;

            return abs;
        }

        private static string AbsoluteOf(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return path;
            }
        }

        private void AddTargetLibNotes(FileEntry entry, BeaconScript script)
        {
            if (options.TargetLib is not { } target || target >= LibWithInterop)
                return;

            if (script.Decls.OfType<CommandBlock>().FirstOrDefault() is { } commandBlock)
                entry.Extras.Add(LibNovelty(entry, target, "command blocks", commandBlock.Span));

            if (WalkCalls(script).FirstOrDefault() is { } call)
                entry.Extras.Add(LibNovelty(entry, target, "call targets", call.Span));

            if (script.Externs.FirstOrDefault() is { } ext)
                entry.Extras.Add(LibNovelty(entry, target, "extern declarations", ext.Span));
        }

        private static BeaconDiagnostic LibNovelty(FileEntry entry, int target, string feature, SourceSpan span) =>
            new(
                BeaconDiagnosticCodes.LibNovelty,
                BeaconSeverity.Error,
                $"'{feature}' needs beacon.lib {LibWithInterop} but --target-lib {target} was given "
                + $"in '{Path.GetFileName(entry.Display)}'. The interop surface stabilizes in lib 2.",
                span.Origin,
                $"Raise --target-lib to {LibWithInterop} or avoid '{feature}' on lib {target} runtimes.");

        private static void AddForgivenNotes(FileEntry entry, BeaconScript script)
        {
            foreach (BeaconForgivenForm form in BeaconFix.FindForgivenForms(script))
            {
                entry.Extras.Add(form.Kind switch
                {
                    BeaconForgivenKind.CancelEvent => new BeaconDiagnostic(
                        BeaconDiagnosticCodes.ForgivenForm,
                        BeaconSeverity.Note,
                        "Spelling 'cancel event' is forgiven; the docs teach 'stop event'.",
                        form.Span.Origin,
                        "Write 'stop event'."),
                    _ => new BeaconDiagnostic(
                        BeaconDiagnosticCodes.ForgivenForm,
                        BeaconSeverity.Note,
                        $"Block ends with bare 'end'; the formatter writes 'end {form.Label}'.",
                        form.Span.Origin,
                        $"Write 'end {form.Label}'."),
                });
            }

            var functions = new HashSet<string>(
                script.Decls.OfType<FunctionDef>().Select(f => f.Name), StringComparer.Ordinal);
            foreach (BeaconStatement statement in WalkTopStatements(script))
            {
                if (statement is not ExprStmt expr)
                    continue;

                string? name = DiscardedCallName(expr.Expr, functions);
                if (name is null)
                    continue;

                entry.Extras.Add(new BeaconDiagnostic(
                    BeaconDiagnosticCodes.ForgivenForm,
                    BeaconSeverity.Note,
                    $"Call '{name}' discards its return value; nine times out of ten that means "
                    + "a missing say, show, or set.",
                    expr.Span.Origin,
                    $"Write say {name}(...) or set answer to {name}(...)."));
            }
        }

        private static string? DiscardedCallName(BeaconExpr expr, HashSet<string> functions) => expr switch
        {
            CallExpr call when call.Target is IdentExpr ident
                && (functions.Contains(ident.Name) || string.Equals(ident.Name, "mcc", StringComparison.Ordinal))
                => ident.Name + "(...)",
            CallPrimExpr prim => $"call \"{prim.Target}\"()",
            MccExpr => "mcc ...",
            _ => null,
        };

        private static IEnumerable<BeaconStatement> WalkTopStatements(BeaconScript script)
        {
            foreach (BeaconTopDecl decl in script.Decls)
            {
                switch (decl)
                {
                    case OnBlock on:
                        foreach (BeaconStatement statement in WalkBlockStatements(on.Body))
                            yield return statement;

                        break;
                    case EveryBlock every:
                        foreach (BeaconStatement statement in WalkBlockStatements(every.Body))
                            yield return statement;

                        break;
                    case OnceBlock once:
                        foreach (BeaconStatement statement in WalkBlockStatements(once.Body))
                            yield return statement;

                        break;
                    case FunctionDef function:
                        foreach (BeaconStatement statement in WalkBlockStatements(function.Body))
                            yield return statement;

                        break;
                    case CommandBlock command:
                        foreach (BeaconStatement statement in WalkBlockStatements(command.Body))
                            yield return statement;

                        break;
                    case TopStatement top:
                        yield return top.Statement;
                        break;
                    default:
                        break;
                }
            }
        }

        private static IEnumerable<BeaconStatement> WalkBlockStatements(BeaconBlock block)
        {
            foreach (BeaconStatement statement in block.Statements)
            {
                yield return statement;
                switch (statement)
                {
                    case IfStmt ifStmt:
                        foreach (BeaconIfBranch branch in ifStmt.Branches)
                        {
                            foreach (BeaconStatement nested in WalkBlockStatements(branch.Body))
                                yield return nested;
                        }

                        if (ifStmt.ElseBody is not null)
                        {
                            foreach (BeaconStatement nested in WalkBlockStatements(ifStmt.ElseBody))
                                yield return nested;
                        }

                        break;
                    case WhileStmt whileStmt:
                        foreach (BeaconStatement nested in WalkBlockStatements(whileStmt.Body))
                            yield return nested;

                        break;
                    case RepeatStmt repeat:
                        foreach (BeaconStatement nested in WalkBlockStatements(repeat.Body))
                            yield return nested;

                        break;
                    case ForStmt forStmt:
                        foreach (BeaconStatement nested in WalkBlockStatements(forStmt.Body))
                            yield return nested;

                        break;
                    case TryStmt tryStmt:
                        foreach (BeaconStatement nested in WalkBlockStatements(tryStmt.Body))
                            yield return nested;

                        foreach (BeaconStatement nested in WalkBlockStatements(tryStmt.CatchBody))
                            yield return nested;

                        if (tryStmt.FinallyBody is not null)
                        {
                            foreach (BeaconStatement nested in WalkBlockStatements(tryStmt.FinallyBody))
                                yield return nested;
                        }

                        break;
                    case LockStmt lockStmt:
                        foreach (BeaconStatement nested in WalkBlockStatements(lockStmt.Body))
                            yield return nested;

                        break;
                    default:
                        break;
                }
            }
        }

        private static IEnumerable<CallPrimExpr> WalkCalls(BeaconScript script)
        {
            var found = new List<CallPrimExpr>();
            foreach (BeaconTopDecl decl in script.Decls)
                CollectCallDecl(decl, found);

            return found;
        }

        private static void CollectCallDecl(BeaconTopDecl decl, List<CallPrimExpr> into)
        {
            switch (decl)
            {
                case OnBlock on:
                    if (on.When is not null)
                        CollectCallExpr(on.When, into);

                    CollectCallBlock(on.Body, into);
                    break;
                case EveryBlock every:
                    CollectCallBlock(every.Body, into);
                    break;
                case OnceBlock once:
                    CollectCallExpr(once.Delay, into);
                    CollectCallBlock(once.Body, into);
                    break;
                case ExportValueDecl exported:
                    CollectCallExpr(exported.Value, into);
                    break;
                case FunctionDef function:
                    CollectCallBlock(function.Body, into);
                    break;
                case CommandBlock command:
                    CollectCallBlock(command.Body, into);
                    break;
                case TopStatement top:
                    CollectCallStmt(top.Statement, into);
                    break;
                default:
                    break;
            }
        }

        private static void CollectCallBlock(BeaconBlock block, List<CallPrimExpr> into)
        {
            foreach (BeaconStatement statement in block.Statements)
                CollectCallStmt(statement, into);
        }

        private static void CollectCallStmt(BeaconStatement statement, List<CallPrimExpr> into)
        {
            switch (statement)
            {
                case SetStmt set:
                    CollectCallExpr(set.Value, into);
                    break;
                case SayStmt say:
                    CollectCallExpr(say.Message, into);
                    break;
                case WhisperStmt whisper:
                    CollectCallExpr(whisper.Player, into);
                    CollectCallExpr(whisper.Message, into);
                    break;
                case ServerStmt server:
                    CollectCallExpr(server.Command, into);
                    break;
                case DisconnectStmt disconnect:
                    if (disconnect.Reason is not null)
                        CollectCallExpr(disconnect.Reason, into);

                    break;
                case ShowStmt show:
                    CollectCallExpr(show.Message, into);
                    break;
                case IfStmt ifStmt:
                    foreach (BeaconIfBranch branch in ifStmt.Branches)
                    {
                        CollectCallExpr(branch.Cond, into);
                        CollectCallBlock(branch.Body, into);
                    }

                    if (ifStmt.ElseBody is not null)
                        CollectCallBlock(ifStmt.ElseBody, into);

                    break;
                case WhileStmt whileStmt:
                    CollectCallExpr(whileStmt.Cond, into);
                    CollectCallBlock(whileStmt.Body, into);
                    break;
                case RepeatStmt repeat:
                    CollectCallExpr(repeat.Count, into);
                    CollectCallBlock(repeat.Body, into);
                    break;
                case ForStmt forStmt:
                    CollectCallExpr(forStmt.Iterable, into);
                    CollectCallBlock(forStmt.Body, into);
                    break;
                case TryStmt tryStmt:
                    CollectCallBlock(tryStmt.Body, into);
                    CollectCallBlock(tryStmt.CatchBody, into);
                    if (tryStmt.FinallyBody is not null)
                        CollectCallBlock(tryStmt.FinallyBody, into);

                    break;
                case LockStmt lockStmt:
                    CollectCallBlock(lockStmt.Body, into);
                    break;
                case ExprStmt exprStmt:
                    CollectCallExpr(exprStmt.Expr, into);
                    break;
                default:
                    break;
            }
        }

        private static void CollectCallExpr(BeaconExpr expr, List<CallPrimExpr> into)
        {
            switch (expr)
            {
                case CallPrimExpr prim:
                    into.Add(prim);
                    break;
                case OrExpr or:
                    CollectCallExpr(or.Left, into);
                    CollectCallExpr(or.Right, into);
                    break;
                case AndExpr and:
                    CollectCallExpr(and.Left, into);
                    CollectCallExpr(and.Right, into);
                    break;
                case NotExpr not:
                    CollectCallExpr(not.Operand, into);
                    break;
                case ComparisonExpr cmp:
                    CollectCallExpr(cmp.Left, into);
                    if (cmp.Right is not null)
                        CollectCallExpr(cmp.Right, into);

                    break;
                case AddExpr add:
                    CollectCallExpr(add.Left, into);
                    CollectCallExpr(add.Right, into);
                    break;
                case MulExpr mul:
                    CollectCallExpr(mul.Left, into);
                    CollectCallExpr(mul.Right, into);
                    break;
                case NegateExpr neg:
                    CollectCallExpr(neg.Operand, into);
                    break;
                case MemberExpr member:
                    CollectCallExpr(member.Target, into);
                    break;
                case IndexExpr index:
                    CollectCallExpr(index.Target, into);
                    CollectCallExpr(index.Index, into);
                    break;
                case CallExpr call:
                    CollectCallExpr(call.Target, into);
                    foreach (BeaconExpr arg in call.Args)
                        CollectCallExpr(arg, into);

                    break;
                case TextLiteral text:
                    foreach (BeaconTextNode part in text.Parts)
                    {
                        if (part is TextHole hole)
                            CollectCallExpr(hole.Expr, into);
                    }

                    break;
                case ListLiteral list:
                    foreach (BeaconExpr item in list.Items)
                        CollectCallExpr(item, into);

                    break;
                case MapLiteral map:
                    foreach (BeaconMapEntry mapEntry in map.Entries)
                        CollectCallExpr(mapEntry.Value, into);

                    break;
                case ParenExpr paren:
                    CollectCallExpr(paren.Inner, into);
                    break;
                case MccExpr mcc:
                    CollectCallExpr(mcc.Argument, into);
                    break;
                default:
                    break;
            }
        }

        private static BeaconParseData? TryParse(string display, string source)
        {
            try
            {
                BeaconHeaderResult header = BeaconPipeline.LexAndParseHeader(
                    display, source, out BeaconLexResult lexed, out _);
                if (!header.Ok)
                    return null;

                BeaconParseResult parsed = BeaconParser.Parse(display, lexed.Tokens, header.Major);
                return parsed.Script is null ? null : new BeaconParseData(parsed.Script, header);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        private void ComputeEffective(FileEntry entry)
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            void Fill(FileEntry current)
            {
                if (current.Effective is not null || !visiting.Add(current.AbsolutePath))
                    return;

                var union = new HashSet<string>(StringComparer.Ordinal);
                if (current.Script is not null)
                    union.UnionWith(BeaconCapabilityInference.Infer(current.Script));

                foreach (string childAbs in current.Children)
                {
                    if (_files.TryGetValue(childAbs, out FileEntry? child) && child is not null)
                    {
                        Fill(child);
                        if (child.Effective is not null)
                            union.UnionWith(child.Effective);
                    }
                }

                visiting.Remove(current.AbsolutePath);
                current.Effective = union;
            }

            Fill(entry);
        }

        private void CollectReports(
            FileEntry entry,
            List<BeaconDiagnostic> diagnostics,
            HashSet<string> seen)
        {
            if (!seen.Add(entry.AbsolutePath))
                return;

            IReadOnlySet<string> effective = entry.Effective ?? new HashSet<string>(StringComparer.Ordinal);

            foreach (BeaconDiagnostic diagnostic in entry.Base)
            {
                // Manifest enforcement is recomputed below against the transitive union, so the single-file verdicts from the base pipeline are replaced, never doubled.
                if (diagnostic.Code is BeaconDiagnosticCodes.ManifestNeedsMismatch
                    or BeaconDiagnosticCodes.ManifestWantsUnavailable)
                    continue;

                diagnostics.Add(diagnostic);
            }

            diagnostics.AddRange(entry.Extras);

            if (entry is { Script: not null, Header: not null })
            {
                diagnostics.AddRange(BeaconManifestEnforcer.CheckUsed(
                    entry.Display, effective, entry.Header.Needs, entry.Header.Wants));
            }

            foreach (string child in entry.Children)
            {
                if (_files.TryGetValue(child, out FileEntry? found) && found is not null)
                    CollectReports(found, diagnostics, seen);
            }
        }
    }

    private static BeaconDiagnostic Escalate(BeaconDiagnostic diagnostic)
    {
        if (diagnostic.Severity != BeaconSeverity.Warning
            || diagnostic.Code is not (BeaconDiagnosticCodes.ManifestWantsUnavailable
                or BeaconDiagnosticCodes.UnknownEvent
                or BeaconDiagnosticCodes.UnresolvedBridge))
            return diagnostic;

        return diagnostic with
        {
            Severity = BeaconSeverity.Error,
            Message = diagnostic.Message + " (--strict escalates this warning to an error).",
        };
    }

    private sealed record BeaconParseData(BeaconScript Script, BeaconHeaderResult Header);

    private sealed record FileEntry(
        string AbsolutePath,
        string Display,
        string Source,
        BeaconScript? Script,
        BeaconHeaderResult? Header,
        IReadOnlyList<BeaconDiagnostic> Base)
    {
        public List<string> Children { get; } = [];
        public List<BeaconDiagnostic> Extras { get; } = [];
        public IReadOnlySet<string>? Effective { get; set; }
    }
}
