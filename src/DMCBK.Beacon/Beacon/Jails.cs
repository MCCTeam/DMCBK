using System.Net.Http;
using System.Text;

namespace DMCBK.Core.Beacon;

/// <summary>
/// Capability vocabulary for manifest enforcement.
/// Built-in verbs resolve to these strings; scripts list them in <c># needs:</c> (exact cover, fail closed) or
/// <c># wants:</c> (warn-only degradation).
/// </summary>
public static class BeaconCapabilities
{
    /// <summary>Public and private chat (<c>say</c>, <c>whisper</c>).</summary>
    public const string ChatSend = "chat.send";

    /// <summary>Server commands (<c>server</c>).</summary>
    public const string ServerSend = "server.send";

    /// <summary>MCC internal commands (<c>mcc</c>).</summary>
    public const string MccRun = "mcc.run";

    /// <summary>Script file reads (<c>file_read</c>).</summary>
    public const string FsRead = "fs.read";

    /// <summary>Script file writes (<c>file_write</c>).</summary>
    public const string FsWrite = "fs.write";

    /// <summary>Outbound fetches (<c>http_get</c>, <c>http_post</c>).</summary>
    public const string NetFetch = "net.fetch";

    /// <summary>World block reads (<c>world.block_at</c>, <c>world.light_at</c>, <c>world.biome_at</c>, <c>world.sign_text</c>).</summary>
    public const string WorldRead = "world.read";

    /// <summary>Block search (<c>world.find_blocks</c>, <c>world.find_signs</c>).</summary>
    public const string WorldSearch = "world.search";

    /// <summary>World mutations and targeting reads (<c>world.dig</c>, <c>world.place</c>, <c>world.use</c>, <c>world.looking_at</c>).</summary>
    public const string WorldWrite = "world.write";

    /// <summary>Steering (<c>move_goto</c>, <c>move_follow</c>; recognized early).</summary>
    public const string Movement = "movement";

    /// <summary>Nearby-entity reads (<c>entities.*</c>, <c>on entity_add</c>/<c>on entity_remove</c>).</summary>
    public const string EntityRead = "entity.read";

    /// <summary>Entity actions (<c>attack</c>, <c>interact</c>).</summary>
    public const string EntityWrite = "entity.write";

    /// <summary>Leaving the server (<c>disconnect</c>).</summary>
    public const string ServerDisconnect = "server.disconnect";

    /// <summary>Inventory reads (<c>inv.list</c>, <c>inv.has</c>, ...).</summary>
    public const string InventoryRead = "inventory.read";

    /// <summary>Inventory writes (<c>inv.select</c>, <c>inv.move</c>, ...).</summary>
    public const string InventoryWrite = "inventory.write";

    /// <summary>Dialog reads (<c>dialog.show</c>, <c>on dialog</c>).</summary>
    public const string DialogRead = "dialog.read";

    /// <summary>Dialog writes (<c>dialog.set</c>, <c>dialog.click</c>, <c>dialog.answer</c>, <c>dialog.close</c>).</summary>
    public const string DialogWrite = "dialog.write";

    /// <summary>
    /// Forward-declared bridge capability for economy reads; lint accepts it without verifying a provider.
    /// </summary>
    public const string EconRead = "econ.read";

    /// <summary>Capabilities a provider is known to offer (lint warns, never refuses, otherwise).</summary>
    public static IReadOnlySet<string> KnownProviders { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ChatSend, ServerSend, MccRun, FsRead, FsWrite, NetFetch, WorldRead, WorldSearch, WorldWrite,
        Movement, EntityRead, EntityWrite, ServerDisconnect, InventoryRead, InventoryWrite, EconRead,
        DialogRead, DialogWrite,
    };
}

/// <summary>
/// Infers the capabilities a script uses by walking the whole AST: top-level statements, <c>on</c>/<c>every</c> bodies plus filters, every function body, and every <c>command</c> body.
/// Deliberately an over-approximation (dead code counts): for a fail-closed refusal, missing a use is worse than flagging an unused one.
/// <c>import</c>/<c>extern</c>/<c>call</c> targets contribute nothing here.
/// </summary>
public static class BeaconCapabilityInference
{
    private static readonly HashSet<string> InventoryWriters = new(StringComparer.Ordinal)
    {
        "select", "drop", "drop_stack", "move", "click", "take", "put",
    };

    /// <summary>Returns the sorted capability set the script uses.</summary>
    public static IReadOnlySet<string> Infer(BeaconScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (BeaconTopDecl decl in script.Decls)
            InferTopDecl(decl, found);

        return found;
    }

    private static void InferTopDecl(BeaconTopDecl decl, HashSet<string> found)
    {
        switch (decl)
        {
            case OnBlock on:
                if (BeaconProviders.TryGetEvent(on.EventName, out _, out string? eventCapability)
                    && !string.IsNullOrWhiteSpace(eventCapability))
                    found.Add(eventCapability);

                if (string.Equals(on.EventName, "entity_add", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(on.EventName, "entity_remove", StringComparison.OrdinalIgnoreCase))
                    found.Add(BeaconCapabilities.EntityRead);

                if (string.Equals(on.EventName, "dialog", StringComparison.OrdinalIgnoreCase))
                    found.Add(BeaconCapabilities.DialogRead);

                if (on.When is not null)
                    InferExpr(on.When, found);

                InferBlock(on.Body, found);
                break;
            case EveryBlock every:
                InferBlock(every.Body, found);
                break;
            case OnceBlock once:
                InferExpr(once.Delay, found);
                InferBlock(once.Body, found);
                break;
            case ExportValueDecl exported:
                InferExpr(exported.Value, found);
                break;
            case FunctionDef function:
                InferBlock(function.Body, found);
                break;
            case CommandBlock command:
                InferBlock(command.Body, found);
                break;
            case TopStatement top:
                InferStatement(top.Statement, found);
                break;
            default:
                break;
        }
    }

    private static void InferBlock(BeaconBlock block, HashSet<string> found)
    {
        foreach (BeaconStatement statement in block.Statements)
            InferStatement(statement, found);
    }

    private static void InferStatement(BeaconStatement statement, HashSet<string> found)
    {
        switch (statement)
        {
            case SetStmt set:
                InferExpr(set.Value, found);
                foreach (BeaconTargetPart part in set.Target.Parts)
                {
                    if (part is TargetIndex index)
                        InferExpr(index.Index, found);
                }

                break;
            case SayStmt say:
                found.Add(BeaconCapabilities.ChatSend);
                InferExpr(say.Message, found);
                break;
            case WhisperStmt whisper:
                found.Add(BeaconCapabilities.ChatSend);
                InferExpr(whisper.Player, found);
                InferExpr(whisper.Message, found);
                break;
            case ServerStmt server:
                found.Add(BeaconCapabilities.ServerSend);
                InferExpr(server.Command, found);
                break;
            case DisconnectStmt disconnect:
                found.Add(BeaconCapabilities.ServerDisconnect);
                if (disconnect.Reason is not null)
                    InferExpr(disconnect.Reason, found);

                break;
            case ShowStmt show:
                InferExpr(show.Message, found);
                break;
            case WaitStmt wait:
                InferExpr(wait.Count, found);
                break;
            case ReturnStmt ret:
                if (ret.Value is not null)
                    InferExpr(ret.Value, found);

                break;
            case SaveStmt save:
                InferExpr(save.Key, found);
                InferExpr(save.Value, found);
                break;
            case LockStmt lockStmt:
                InferBlock(lockStmt.Body, found);
                break;
            case StartStmt start:
                InferExpr(start.Task, found);
                break;
            case AwaitStmt awaitStmt:
                InferExpr(awaitStmt.Task, found);
                break;
            case CancelTaskStmt cancel:
                InferExpr(cancel.Task, found);
                break;
            case IfStmt ifStmt:
                foreach (BeaconIfBranch branch in ifStmt.Branches)
                {
                    InferExpr(branch.Cond, found);
                    InferBlock(branch.Body, found);
                }

                if (ifStmt.ElseBody is not null)
                    InferBlock(ifStmt.ElseBody, found);

                break;
            case WhileStmt whileStmt:
                InferExpr(whileStmt.Cond, found);
                InferBlock(whileStmt.Body, found);
                break;
            case RepeatStmt repeat:
                InferExpr(repeat.Count, found);
                InferBlock(repeat.Body, found);
                break;
            case ForStmt forStmt:
                InferExpr(forStmt.Iterable, found);
                InferBlock(forStmt.Body, found);
                break;
            case TryStmt tryStmt:
                InferBlock(tryStmt.Body, found);
                InferBlock(tryStmt.CatchBody, found);
                if (tryStmt.FinallyBody is not null)
                    InferBlock(tryStmt.FinallyBody, found);

                break;
            case ExprStmt exprStmt:
                InferExpr(exprStmt.Expr, found);
                break;
            case NestedDeclStmt nested:
                InferTopDecl(nested.Decl, found);
                break;
            default:
                break;
        }
    }

    private static void InferExpr(BeaconExpr expr, HashSet<string> found)
    {
        switch (expr)
        {
            case OrExpr or:
                InferExpr(or.Left, found);
                InferExpr(or.Right, found);
                break;
            case AndExpr and:
                InferExpr(and.Left, found);
                InferExpr(and.Right, found);
                break;
            case NotExpr not:
                InferExpr(not.Operand, found);
                break;
            case ComparisonExpr cmp:
                InferExpr(cmp.Left, found);
                if (cmp.Right is not null)
                    InferExpr(cmp.Right, found);

                break;
            case AddExpr add:
                InferExpr(add.Left, found);
                InferExpr(add.Right, found);
                break;
            case MulExpr mul:
                InferExpr(mul.Left, found);
                InferExpr(mul.Right, found);
                break;
            case NegateExpr neg:
                InferExpr(neg.Operand, found);
                break;
            case MemberExpr member:
                InferExpr(member.Target, found);
                if (member.Target is IdentExpr ns
                    && BeaconProviders.TryGetVariable(ns.Name, out _, out string variableCapability))
                    found.Add(variableCapability);

                break;
            case IndexExpr index:
                InferExpr(index.Target, found);
                InferExpr(index.Index, found);
                break;
            case CallExpr call:
                InferCall(call, found);
                break;
            case TextLiteral text:
                foreach (BeaconTextNode part in text.Parts)
                {
                    if (part is TextHole hole)
                        InferExpr(hole.Expr, found);
                }

                break;
            case ListLiteral list:
                foreach (BeaconExpr item in list.Items)
                    InferExpr(item, found);

                break;
            case MapLiteral map:
                foreach (BeaconMapEntry entry in map.Entries)
                    InferExpr(entry.Value, found);

                break;
            case ParenExpr paren:
                InferExpr(paren.Inner, found);
                break;
            case MccExpr mcc:
                found.Add(BeaconCapabilities.MccRun);
                InferExpr(mcc.Argument, found);
                break;
            default:
                break;
        }
    }

    private static void InferCall(CallExpr call, HashSet<string> found)
    {
        if (call.Target is IdentExpr ident)
            InferNamedCall(ident.Name, found);
        else if (call.Target is MemberExpr member && member.Target is IdentExpr ns)
        {
            InferNamespacedCall(ns.Name, member.Member, found);
            InferExpr(member.Target, found);
        }
        else
            InferExpr(call.Target, found);

        foreach (BeaconExpr arg in call.Args)
            InferExpr(arg, found);
    }

    private static void InferNamedCall(string name, HashSet<string> found)
    {
        switch (name)
        {
            case "mcc":
                found.Add(BeaconCapabilities.MccRun);
                break;
            case "file_read":
                found.Add(BeaconCapabilities.FsRead);
                break;
            case "file_write":
                found.Add(BeaconCapabilities.FsWrite);
                break;
            case "http_get":
            case "http_post":
                found.Add(BeaconCapabilities.NetFetch);
                break;
            case "move_goto":
            case "move_follow":
                found.Add(BeaconCapabilities.Movement);
                break;
            case "attack":
            case "interact":
                found.Add(BeaconCapabilities.EntityWrite);
                break;
            // look_at, use_in_hand, stop_moving, eat, and saved infer nothing: the movement capability covers steering only (move_goto, move_follow).
            // Scripts such as the totem guard (use_in_hand) and the bedtime farmer declare exactly that, so widening the mapping would refuse scripts that pass clean today.
            case "craft_one":
                found.Add(BeaconCapabilities.InventoryWrite);
                break;
            case "craft_list":
                found.Add(BeaconCapabilities.InventoryRead);
                break;
            default:
                if (BeaconProviders.TryGetFunction(name, out _, out string capability))
                    found.Add(capability);

                break;
        }
    }

    private static void InferNamespacedCall(string ns, string member, HashSet<string> found)
    {
        if (string.Equals(ns, "world", StringComparison.Ordinal))
        {
            found.Add(member switch
            {
                "find_blocks" or "find_signs" => BeaconCapabilities.WorldSearch,
                "dig" or "place" or "use" or "looking_at" => BeaconCapabilities.WorldWrite,
                _ => BeaconCapabilities.WorldRead,
            });
        }
        else if (string.Equals(ns, "inv", StringComparison.Ordinal))
        {
            found.Add(InventoryWriters.Contains(member)
                ? BeaconCapabilities.InventoryWrite
                : BeaconCapabilities.InventoryRead);
        }
        else if (string.Equals(ns, "entities", StringComparison.Ordinal))
            found.Add(BeaconCapabilities.EntityRead);
        else if (string.Equals(ns, "trade", StringComparison.Ordinal))
        {
            found.Add(member switch
            {
                "select" or "buy" => BeaconCapabilities.InventoryWrite,
                _ => BeaconCapabilities.InventoryRead,
            });
        }
        else if (string.Equals(ns, "enchant", StringComparison.Ordinal))
        {
            found.Add(string.Equals(member, "choose", StringComparison.Ordinal)
                ? BeaconCapabilities.InventoryWrite
                : BeaconCapabilities.InventoryRead);
        }
        else if (string.Equals(ns, "dialog", StringComparison.Ordinal))
        {
            found.Add(string.Equals(member, "show", StringComparison.Ordinal)
                ? BeaconCapabilities.DialogRead
                : BeaconCapabilities.DialogWrite);
        }
    }
}

/// <summary>
/// Manifest enforcement, fail closed: when a script declares any <c># needs:</c> entry, the inferred set must be covered or loading refuses with the exact line to paste (B1001).
/// Entries no loaded provider offers (needs or wants) warn and load (B1002): there is no plugin registry yet, so the enforcer cannot tell "provider not loaded" from "nothing provides it"; the refusal half of that distinction arrives with the registry.
/// Scripts that declare no manifest at all load unenforced (zero-config start).
/// </summary>
public static class BeaconManifestEnforcer
{
    /// <summary>
    /// Checks coverage plus provider availability.
    /// An empty diagnostic list means load.
    /// </summary>
    public static IReadOnlyList<BeaconDiagnostic> Check(
        string fileName,
        BeaconScript script,
        IReadOnlyList<BeaconManifestEntry> needs,
        IReadOnlyList<BeaconManifestEntry> wants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(wants);

        IReadOnlySet<string> used = BeaconCapabilityInference.Infer(script);
        return CheckUsed(fileName, used, needs, wants);
    }

    /// <summary>
    /// Checks a precomputed capability set (the import/call union, which is wider than one file's own inference) against a manifest.
    /// Same messages as <see cref="Check"/>; the only difference is where <paramref name="used"/> came from.
    /// </summary>
    public static IReadOnlyList<BeaconDiagnostic> CheckUsed(
        string fileName,
        IReadOnlySet<string> used,
        IReadOnlyList<BeaconManifestEntry> needs,
        IReadOnlyList<BeaconManifestEntry> wants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(used);
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(wants);

        var diagnostics = new List<BeaconDiagnostic>();
        if (needs.Count == 0 && wants.Count == 0)
            return diagnostics;

        var declared = new HashSet<string>(needs.Select(n => n.Capability), StringComparer.Ordinal);

        List<string> missing = used.Where(cap => !declared.Contains(cap)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (needs.Count > 0 && missing.Count > 0)
        {
            List<string> cover = declared.Union(missing).OrderBy(c => c, StringComparer.Ordinal).ToList();
            string paste = "# needs: " + string.Join(" ", cover);
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.ManifestNeedsMismatch,
                BeaconSeverity.Error,
                $"Script uses {string.Join(", ", missing)} but the manifest lacks "
                + (missing.Count == 1 ? "it" : "them") + ". Paste this line above the first declaration:",
                new SourceSpan(fileName, 1, 1, 0),
                paste));
        }

        var unknown = new List<string>();
        foreach (string cap in declared)
        {
            if (!BeaconCapabilities.KnownProviders.Contains(cap) && !unknown.Contains(cap))
                unknown.Add(cap);
        }

        foreach (BeaconManifestEntry want in wants)
        {
            if (!BeaconCapabilities.KnownProviders.Contains(want.Capability) && !unknown.Contains(want.Capability))
                unknown.Add(want.Capability);
        }

        unknown.Sort(StringComparer.Ordinal);
        foreach (string cap in unknown)
        {
            IReadOnlyList<string> providers = BeaconProviders.ProvidersOf(cap);
            string advice = providers.Count == 0
                ? "Install the providing plugin or drop the entry."
                : $"Install the '{providers[0]}' plugin or drop the entry.";
            diagnostics.Add(new BeaconDiagnostic(
                BeaconDiagnosticCodes.ManifestWantsUnavailable,
                BeaconSeverity.Warning,
                $"Capability '{cap}' has no loaded provider; the script loads anyway and calls needing it will fail gracefully. "
                + advice,
                new SourceSpan(fileName, 1, 1, 0),
                $"Remove '{cap}' from the manifest until its provider is installed."));
        }

        diagnostics.Sort((a, b) =>
        {
            int line = a.Span.Line.CompareTo(b.Span.Line);
            return line != 0 ? line : string.Compare(a.Code, b.Code, StringComparison.Ordinal);
        });
        return diagnostics;
    }
}

/// <summary>
/// The fs jail: scripts read and write text files only under <c>&lt;scripts-dir&gt;/data/</c>, 1 MB per file in either direction.
/// Paths resolve plus normalize (so <c>a/../b</c> stays legal) plus symlink refusal (the <see cref="BeaconSavedState"/> precedent): anything that would leave the jail, or pass through a link, throws catchable B4009 naming the jail.
/// </summary>
public sealed class BeaconFileJail
{
    /// <summary>Maximum bytes per file read or written.</summary>
    public const long MaxFileBytes = 1024L * 1024L;

    /// <summary>Builds a jail over a data directory (created on first write, never on resolve).</summary>
    public BeaconFileJail(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        if (Directory.Exists(DataDirectory) && new DirectoryInfo(DataDirectory).LinkTarget is not null)
        {
            throw new ArgumentException(
                $"Beacon file jail '{DataDirectory}' is a symbolic link; it must be a regular directory.",
                nameof(dataDirectory));
        }
    }

    /// <summary>The resolved jail root.</summary>
    public string DataDirectory { get; }

    /// <summary>The conventional jail for a configurations folder: <c>&lt;scripts-dir&gt;/data/</c>.</summary>
    public static string ScriptsDataDir(string configurationsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationsFolder);
        return Path.Combine(
            DMCBK.Core.Configuration.ConfigurationPaths.ScriptsDir(configurationsFolder), "data");
    }

    /// <summary>Resolves a script-relative path, proving it stays inside the jail (pure).</summary>
    public string Resolve(string relativePath, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(span);
        if (string.IsNullOrWhiteSpace(relativePath))
            throw Refused(span, "an empty file path", "Write file_read(\"notes.txt\") with a relative path.");

        if (Path.IsPathRooted(relativePath))
        {
            throw Refused(
                span,
                $"the absolute path '{relativePath}'",
                $"Write file_read(\"notes.txt\") with a path relative to the jail (the jail is '{DataDirectory}').");
        }

        string combined = Path.GetFullPath(Path.Combine(DataDirectory, relativePath));
        string root = DataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.Ordinal))
        {
            throw Refused(
                span,
                $"the path '{relativePath}' (which resolves to '{combined}')",
                $"Keep file paths inside the jail (the jail is '{root}').");
        }

        if ((File.Exists(combined) && new FileInfo(combined).LinkTarget is not null)
            || (Directory.Exists(combined) && new DirectoryInfo(combined).LinkTarget is not null))
        {
            throw Refused(
                span,
                $"the path '{relativePath}' (a symbolic link)",
                "Point file paths at regular files inside the jail.");
        }

        return combined;
    }

    /// <summary>Reads a jailed text file (1 MB cap, missing files raise catchably).</summary>
    public string ReadText(string relativePath, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(span);
        string path = Resolve(relativePath, span);
        if (!File.Exists(path))
        {
            throw Refused(
                span,
                $"the path '{relativePath}' (no such file in the jail)",
                $"Write the file first with file_write(\"{relativePath}\", ...) or check the name.");
        }

        long length = new FileInfo(path).Length;
        if (length > MaxFileBytes)
        {
            throw Refused(
                span,
                $"the file '{relativePath}' ({length} bytes, over the {MaxFileBytes}-byte cap)",
                "Keep script files small; larger data lives outside Beacon IO.");
        }

        return File.ReadAllText(path);
    }

    /// <summary>Writes a jailed text file atomically-ish (1 MB cap, parent dirs created).</summary>
    public void WriteText(string relativePath, string text, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(span);
        long size = Encoding.UTF8.GetByteCount(text);
        if (size > MaxFileBytes)
        {
            throw Refused(
                span,
                $"the write to '{relativePath}' ({size} bytes, over the {MaxFileBytes}-byte cap)",
                "Keep script files small; larger data lives outside Beacon IO.");
        }

        string path = Resolve(relativePath, span);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static BeaconRuntimeException Refused(SourceSpan span, string what, string suggestion) =>
        BeaconErrors.FileJailRefused(
            span,
            $"Refusing Beacon file access for {what}: scripts reach only their '<script-dir>/data/' jail. "
            + "There is no path out, through '..', absolute paths, or symlinks.",
            suggestion);
}

/// <summary>
/// The net gate: <c>http_get</c>/<c>http_post</c> against <c>beacon.toml</c>-allowlisted HTTPS hosts only, 5 s timeout, 1 MB response cap.
/// Plain HTTP is refused outright; a host outside the allowlist is refused with the exact <c>beacon.toml</c> lines to add (that refusal is the feature).
/// All refusals are catchable B4010.
/// </summary>
public sealed class BeaconNetGate
{
    /// <summary>Maximum response bytes buffered.</summary>
    public const long MaxResponseBytes = 1024L * 1024L;

    /// <summary>Per-request timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Shared = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly BeaconNetConfig _config;
    private readonly Func<Uri, string, string?, CancellationToken, Task<byte[]>> _fetcher;

    /// <summary>
    /// Builds a gate over an allowlist.
    /// The optional <paramref name="fetcher"/> replaces the transport (tests inject a fake; production streams through a shared client with the cap).
    /// </summary>
    public BeaconNetGate(
        BeaconNetConfig config,
        Func<Uri, string, string?, CancellationToken, Task<byte[]>>? fetcher = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _fetcher = fetcher ?? DefaultFetchAsync;
        Timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Per-request timeout (settable for tests; default <see cref="DefaultTimeout"/>).</summary>
    public TimeSpan Timeout { get; set; }

    /// <summary>GETs <paramref name="url"/> and returns its body as text.</summary>
    public Task<string> GetAsync(string url, SourceSpan span, CancellationToken ct = default)
        => FetchAsync(url, "GET", null, span, ct);

    /// <summary>POSTs <paramref name="body"/> (plain text) to <paramref name="url"/> and returns the reply as text.</summary>
    public Task<string> PostAsync(string url, string body, SourceSpan span, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FetchAsync(url, "POST", body, span, ct);
    }

    private async Task<string> FetchAsync(
        string url, string method, string? body, SourceSpan span, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(span);
        if (string.IsNullOrWhiteSpace(url))
            throw Refused(span, "an empty URL.", "Write http_get(\"https://example.com/path\").");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri is null)
            throw Refused(span, $"the URL '{url}' (not an absolute URL).", "Write http_get(\"https://example.com/path\").");

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw Refused(
                span,
                $"the URL '{url}' (plain HTTP is refused; Beacon fetches HTTPS only).",
                $"Write the URL as 'https://{uri.Host}{uri.PathAndQuery}' instead.");
        }

        string host = uri.Host.ToLowerInvariant();
        if (!_config.AllowedHosts.Contains(host, StringComparer.Ordinal))
        {
            List<string> merged = _config.AllowedHosts.Concat([host]).OrderBy(h => h, StringComparer.Ordinal).ToList();
            string toml = "[Net]\nAllowedHosts = [" + string.Join(", ", merged.Select(h => $"\"{h}\"")) + "]";
            throw Refused(
                span,
                $"the host '{host}' (not allowlisted for net.fetch).",
                $"Add it to beacon.toml:\n{toml}");
        }

        byte[] bytes;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(Timeout);
            try
            {
                bytes = await _fetcher(uri, method, body, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Refused(
                    span,
                    $"the fetch of '{url}' (timed out after {Timeout.TotalSeconds:F1}s).",
                    "Retry with a smaller response, or check the host from outside the client.");
            }
            catch (BeaconQuotaExceededException ex)
            {
                throw Refused(span, $"the reply from '{host}' ({ex.Message}).", "Fetch a smaller resource; paged endpoints beat bulk dumps.");
            }
        }

        bytes ??= [];
        if (bytes.LongLength > MaxResponseBytes)
        {
            throw Refused(
                span,
                $"the reply from '{host}' ({bytes.LongLength} bytes, over the {MaxResponseBytes}-byte cap).",
                "Fetch a smaller resource; paged endpoints beat bulk dumps.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static BeaconRuntimeException Refused(SourceSpan span, string what, string suggestion) =>
        BeaconErrors.NetGateRefused(span, $"Refusing Beacon network access for {what}", suggestion);

    private static async Task<byte[]> DefaultFetchAsync(
        Uri uri, string method, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            string.Equals(method, "POST", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "MCC-Beacon/2.0");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "text/plain");

        using HttpResponseMessage response = await Shared
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var sink = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new BeaconQuotaExceededException(
                    $"Beacon net gate refused the reply from '{uri.Host}': over the {MaxResponseBytes}-byte cap.");
            }

            sink.Write(chunk, 0, read);
        }

        return sink.ToArray();
    }
}

/// <summary>
/// Read bounds: <c>online_players</c> paged (default 50, max 100 per read, so a 500-player hub cannot become a self-DDoS), <c>chat_history</c> capped at 200, block reads one coordinate at a time with out-of-range raising catchable B4011.
/// </summary>
public static class BeaconReadBounds
{
    /// <summary>Default <c>online_players</c> page size.</summary>
    public const int OnlinePlayersDefaultPage = 50;

    /// <summary>Maximum <c>online_players</c> page size per read (explicit requests clamp here).</summary>
    public const int OnlinePlayersMaxPage = 100;

    /// <summary>Maximum <c>chat_history</c> entries per read.</summary>
    public const int ChatHistoryMax = 200;

    /// <summary>Maximum <c>world.find_blocks</c> results per read.</summary>
    public const int FindBlocksMaxResults = 64;

    /// <summary>Default <c>entities.*</c> search radius (what the client tracks nearby).</summary>
    public const int EntitiesDefaultRadius = 64;

    /// <summary>Maximum <c>entities.*</c> search radius per read.</summary>
    public const int EntitiesMaxRadius = 128;

    /// <summary>Maximum <c>entities.*</c> rows per read.</summary>
    public const int EntitiesMaxResults = 64;

    /// <summary>Maximum <c>trade.buy</c> units per call.</summary>
    public const int TradeBuyMaxCount = 64;

    /// <summary>Maximum <c>world.find_blocks</c> cubic radius (a wider scan would stall the session loop).</summary>
    public const int FindBlocksMaxRadius = 32;

    /// <summary>Maximum |x|/|z| for a block read (B4011 past it).</summary>
    public const double MaxBlockHorizontal = 30_000_000;

    /// <summary>Minimum y for a block read (modern build envelope; UMPK's dataset owns version-exact bounds).</summary>
    public const double MinBlockY = -64;

    /// <summary>Maximum y for a block read (modern build envelope; UMPK's dataset owns version-exact bounds).</summary>
    public const double MaxBlockY = 320;

    /// <summary>Clamps an <c>online_players(n)</c> argument (null means bare); negatives and NaN refuse.</summary>
    public static int ClampOnlinePlayersPage(BeaconValue? arg, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (arg is null)
            return OnlinePlayersDefaultPage;

        long page = RequireWholeCount(arg, span, "online_players", "Write online_players(50).");
        if (page < 0)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected a non-negative count for 'online_players', but found {page}.",
                "Write online_players(50).");
        }

        return (int)Math.Min(page, OnlinePlayersMaxPage);
    }

    /// <summary>Clamps a <c>chat_history(n)</c> argument (null means bare); negatives and NaN refuse.</summary>
    public static int ClampChatHistory(BeaconValue? arg, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (arg is null)
            return ChatHistoryMax;

        long count = RequireWholeCount(arg, span, "chat_history", "Write chat_history(50).");
        if (count < 0)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected a non-negative count for 'chat_history', but found {count}.",
                "Write chat_history(50).");
        }

        return (int)Math.Min(count, ChatHistoryMax);
    }

    /// <summary>Clamps a <c>world.find_blocks</c> radius/max pair; negatives and NaN refuse.</summary>
    public static (int Radius, int MaxResults) ClampFindBlocks(BeaconValue radius, BeaconValue max, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(radius);
        ArgumentNullException.ThrowIfNull(max);
        ArgumentNullException.ThrowIfNull(span);
        long r = RequireWholeCount(radius, span, "world.find_blocks radius", "Write world.find_blocks(\"chest\", 16, 10).");
        long m = RequireWholeCount(max, span, "world.find_blocks max", "Write world.find_blocks(\"chest\", 16, 10).");
        if (r < 0 || m <= 0)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected a non-negative radius and a positive max for 'world.find_blocks', but found radius {r} max {m}.",
                "Write world.find_blocks(\"chest\", 16, 10).");
        }

        return ((int)Math.Min(r, FindBlocksMaxRadius), (int)Math.Min(m, FindBlocksMaxResults));
    }

    /// <summary>Clamps a <c>world.find_signs</c> radius/max pair; negatives and NaN refuse.</summary>
    public static (int Radius, int MaxResults) ClampFindSigns(BeaconValue radius, BeaconValue max, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(radius);
        ArgumentNullException.ThrowIfNull(max);
        ArgumentNullException.ThrowIfNull(span);
        long r = RequireWholeCount(radius, span, "world.find_signs radius", "Write world.find_signs(\"Storage\", 16, 10).");
        long m = RequireWholeCount(max, span, "world.find_signs max", "Write world.find_signs(\"Storage\", 16, 10).");
        if (r < 0 || m <= 0)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected a non-negative radius and a positive max for 'world.find_signs', but found radius {r} max {m}.",
                "Write world.find_signs(\"Storage\", 16, 10).");
        }

        return ((int)Math.Min(r, FindBlocksMaxRadius), (int)Math.Min(m, FindBlocksMaxResults));
    }

    /// <summary>Clamps an <c>entities.*</c> radius argument (null means bare); negatives and NaN refuse.</summary>
    public static int ClampEntityRadius(BeaconValue? arg, SourceSpan span, string what)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        if (arg is null)
            return EntitiesDefaultRadius;

        long radius = RequireWholeCount(arg, span, what, "Write entities.near(32, 10).");
        if (radius < 0)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected a non-negative radius for '{what}', but found {radius}.",
                "Write entities.near(32, 10).");
        }

        return (int)Math.Min(radius, EntitiesMaxRadius);
    }

    /// <summary>Clamps an <c>entities.*</c> max-results argument; non-positive counts and NaN refuse.</summary>
    public static int ClampEntityMax(BeaconValue arg, SourceSpan span, string what)
    {
        ArgumentNullException.ThrowIfNull(arg);
        ArgumentNullException.ThrowIfNull(span);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        long max = RequireWholeCount(arg, span, what, "Write entities.near(32, 10).");
        if (max <= 0)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected a positive max for '{what}', but found {max}.",
                "Write entities.near(32, 10).");
        }

        return (int)Math.Min(max, EntitiesMaxResults);
    }

    /// <summary>Clamps a <c>trade.buy</c> count argument to 1..64; anything else refuses.</summary>
    public static int ClampTradeBuyCount(BeaconValue arg, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(arg);
        ArgumentNullException.ThrowIfNull(span);
        long count = RequireWholeCount(arg, span, "trade.buy count", "Write trade.buy(0, 3).");
        if (count <= 0 || count > TradeBuyMaxCount)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected a count from 1 to {TradeBuyMaxCount} for 'trade.buy', but found {count}.",
                "Write trade.buy(0, 3).");
        }

        return (int)count;
    }

    /// <summary>Validates one block coordinate triple (whole finite numbers inside the envelope).</summary>
    public static (int X, int Y, int Z) ValidateBlockCoords(BeaconValue x, BeaconValue y, BeaconValue z, SourceSpan span)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        ArgumentNullException.ThrowIfNull(z);
        ArgumentNullException.ThrowIfNull(span);
        return (
            RequireBlockAxis(x, "x", span),
            RequireBlockAxis(y, "y", span),
            RequireBlockAxis(z, "z", span));
    }

    private static int RequireBlockAxis(BeaconValue value, string axis, SourceSpan span)
    {
        double number = RequireWholeNumber(
            value, span,
            $"I expected a whole number for the {axis} of 'world.block_at', but found {BeaconInterpreter.DescribeValueKind(value)}.",
            "Write world.block_at(100, 64, -30) with whole numbers.");

        bool inRange = axis switch
        {
            "y" => number >= MinBlockY && number <= MaxBlockY,
            _ => Math.Abs(number) <= MaxBlockHorizontal,
        };
        if (!inRange)
        {
            throw BeaconErrors.ReadBoundsRefused(
                span,
                $"I expected the {axis} {number:G} for 'world.block_at' to sit inside every version's build envelope, but it does not.",
                "Read blocks near the world you are actually in.");
        }

        return (int)number;
    }

    private static long RequireWholeCount(BeaconValue value, SourceSpan span, string what, string suggestion)
        => (long)RequireWholeNumber(
            value, span,
            $"I expected a whole number for '{what}', but found {BeaconInterpreter.DescribeValueKind(value)}.",
            suggestion);

    private static double RequireWholeNumber(BeaconValue value, SourceSpan span, string message, string suggestion)
    {
        if (value is not BeaconNumberValue number
            || double.IsNaN(number.Value) || double.IsInfinity(number.Value)
            || number.Value != Math.Floor(number.Value))
            throw BeaconErrors.ReadBoundsRefused(span, message, suggestion);

        return number.Value;
    }
}
