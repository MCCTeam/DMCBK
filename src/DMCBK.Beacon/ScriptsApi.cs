using System.Security.Cryptography;
using System.Text;
using DMCBK.Core.Beacon;
using DMCBK.Core.Commands.Impl;
using DMCBK.Core.Configuration;

namespace DMCBK.Core;

/// <summary>
/// The per-client Beacon script surface shared by commands and rich hosts.
/// It owns the single live runtime lazily, exposes typed lifecycle/tooling operations, and keeps source writes lint-gated and atomic.
/// </summary>
public sealed class ScriptsApi : IDisposable
{
    private readonly Client _client;
    private readonly Lazy<ScriptsCommand.ScriptsRuntime> _runtime;

    internal ScriptsApi(Client client)
    {
        _client = client;
        _runtime = new Lazy<ScriptsCommand.ScriptsRuntime>(
            () => new ScriptsCommand.ScriptsRuntime(client),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal ScriptsCommand.ScriptsRuntime Runtime => _runtime.Value;

    internal IDisposable? CommandRegistration { get; set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        CommandRegistration?.Dispose();
        CommandRegistration = null;
        if (_runtime.IsValueCreated)
            _runtime.Value.Dispose();
    }

    /// <summary>The top-level scripts directory, or null when the client has no configuration folder.</summary>
    public string? ScriptsDirectory => _client.Configuration?.SourceFolder is { } folder
        ? ConfigurationPaths.ScriptsDir(folder)
        : null;

    /// <summary>Whether live script chat is muted.</summary>
    public bool IsMuted => Runtime.IsMuted;

    /// <summary>How many chat writes have been held while muted.</summary>
    public int HeldChatCount => Runtime.Held;

    /// <summary>Whether the top-level script watcher is currently active.</summary>
    public bool WatchEnabled => Runtime.WatchEnabled;

    /// <summary>Discovers every top-level <c>.mcc</c> file and marks the scripts currently running.</summary>
    public IReadOnlyList<ScriptInfo> Discover()
    {
        string? directory = ScriptsDirectory;
        if (directory is null || !Directory.Exists(directory))
            return [];

        HashSet<string> running = Runtime.Engine.ScriptIds.ToHashSet(StringComparer.Ordinal);
        return Directory.EnumerateFiles(directory, "*" + BeaconScriptDiscovery.ScriptExtension, SearchOption.TopDirectoryOnly)
            .Select(path => new ScriptInfo(
                Path.GetFileNameWithoutExtension(path),
                path,
                running.Contains(Path.GetFileNameWithoutExtension(path))))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Loads one top-level script source and the content hash used for conflict detection.</summary>
    public async Task<ScriptDocument> LoadDocumentAsync(string id, CancellationToken ct = default)
    {
        string path = ResolvePath(id);
        string source = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return new ScriptDocument(Path.GetFileNameWithoutExtension(path), path, source, ContentHash(source));
    }

    /// <summary>Runs the supplied source in the client's single live Beacon runtime.</summary>
    public Task<BeaconRunResult> RunAsync(
        string id, string source, string? fileName = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(source);
        return Runtime.Engine.RunScriptAsync(
            id.Trim(),
            source,
            hostServices: null,
            configFolder: _client.Configuration?.SourceFolder,
            fileName: fileName ?? id.Trim() + BeaconScriptDiscovery.ScriptExtension,
            ct: ct);
    }

    /// <summary>Loads and runs one top-level script file.</summary>
    public async Task<BeaconRunResult> RunFileAsync(string id, CancellationToken ct = default)
    {
        ScriptDocument document = await LoadDocumentAsync(id, ct).ConfigureAwait(false);
        return await RunAsync(document.Id, document.Source, Path.GetFileName(document.Path), ct).ConfigureAwait(false);
    }

    /// <summary>Stops one running script.</summary>
    public bool Stop(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Runtime.Engine.RemoveScript(id.Trim());
    }

    /// <summary>Stops every running script and returns the number removed.</summary>
    public int StopAll()
    {
        int count = 0;
        foreach (string id in Runtime.Engine.ScriptIds.ToArray())
        {
            if (Runtime.Engine.RemoveScript(id))
                count++;
        }

        return count;
    }

    /// <summary>Reloads one running script from disk through the same run pipeline.</summary>
    public Task<BeaconRunResult> ReloadAsync(string id, CancellationToken ct = default)
        => RunFileAsync(id, ct);

    /// <summary>Lints an in-memory editor buffer. Warnings remain non-blocking.</summary>
    public BeaconLintReport Lint(string fileName, string source)
        => BeaconLint.LintSource(fileName, source);

    /// <summary>Formats an in-memory editor buffer without writing it.</summary>
    public BeaconFormatResult Format(string fileName, string source)
        => BeaconFormat.FormatSource(fileName, source);

    /// <summary>Creates a new top-level script from one of the built-in templates.</summary>
    public ScriptCreateResult Create(string template, string id)
    {
        string? directory = ScriptsDirectory;
        if (directory is null)
            return new ScriptCreateResult(false, null, "configuration-folder-unavailable");

        bool created = BeaconTemplates.TryWriteNew(directory, template, id, out string path, out string error);
        return new ScriptCreateResult(created, created ? path : null, error);
    }

    /// <summary>
    /// Deletes one top-level script source.
    /// A loaded instance is stopped after the source is removed; script settings and saved state are intentionally retained.
    /// </summary>
    public ScriptDeleteResult Delete(string id)
    {
        string path = ResolvePath(id);
        if (!File.Exists(path))
            return new ScriptDeleteResult(ScriptDeleteOutcome.NotFound, false);

        File.Delete(path);
        string scriptId = Path.GetFileNameWithoutExtension(path);
        bool stoppedRunningScript = Runtime.Engine.ScriptIds.Contains(scriptId)
            && Runtime.Engine.RemoveScript(scriptId);
        return new ScriptDeleteResult(ScriptDeleteOutcome.Deleted, stoppedRunningScript);
    }

    /// <summary>Changes the shared mute state.</summary>
    public void SetMuted(bool muted) => Runtime.SetMuted(muted);

    /// <summary>Enables or disables hot reload for running top-level scripts.</summary>
    public bool SetWatch(bool enabled)
    {
        string? directory = ScriptsDirectory;
        if (enabled && directory is null)
            return false;

        Runtime.SetWatch(enabled, enabled ? directory : null);
        return Runtime.WatchEnabled == enabled;
    }

    /// <summary>Evaluates one REPL line while preserving locals and history in this client runtime.</summary>
    public Task<BeaconReplResult> EvaluateAsync(string line, int? seed = null, CancellationToken ct = default)
        => Runtime.Repl.EvalLineAsync(line, seed, ct);

    /// <summary>Returns the REPL's persistent locals display.</summary>
    public string ReplLocals() => Runtime.Repl.LocalsText();

    /// <summary>Reads one script's declared settings schema and resolved current scalar values.</summary>
    public async Task<ScriptSettingsSnapshot> GetSettingsAsync(string id, CancellationToken ct = default)
    {
        ScriptDocument document = await LoadDocumentAsync(id, ct).ConfigureAwait(false);
        BeaconHeaderResult header = BeaconHeader.Parse(Path.GetFileName(document.Path), document.Source);
        IReadOnlyDictionary<string, BeaconValue> values = header.Ok
            ? BeaconScriptSettings.Resolve(document.Id, header.Settings, _client.Configuration?.SourceFolder)
            : new Dictionary<string, BeaconValue>(StringComparer.Ordinal);
        return new ScriptSettingsSnapshot(document.Id, header.Settings, values, header.Diagnostics);
    }

    /// <summary>Writes one declared scalar setting and updates the running script when it is loaded.</summary>
    public async Task<ScriptSettingSaveResult> SaveSettingAsync(
        string id, string key, BeaconValue value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ScriptSettingsSnapshot settings = await GetSettingsAsync(id, ct).ConfigureAwait(false);
        if (!settings.Declarations.Any(decl => string.Equals(decl.Name, key, StringComparison.Ordinal)))
            return new ScriptSettingSaveResult(false, false, "unknown-setting");

        string? configFolder = _client.Configuration?.SourceFolder;
        if (configFolder is null)
            return new ScriptSettingSaveResult(false, false, "configuration-folder-unavailable");

        string path = ConfigurationPaths.BeaconSettingsFile(configFolder, settings.ScriptId);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(
                    path,
                    BeaconScriptSettings.RenderDefaults(settings.ScriptId, settings.Declarations),
                    ct).ConfigureAwait(false);
            }

            string[] lines = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
                .Split(["\r\n", "\n"], StringSplitOptions.None);
            string rendered = key + " = " + RenderSettingScalar(value);
            bool replaced = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string stripped = lines[i].TrimStart();
                if (stripped.StartsWith('#'))
                    continue;

                int equals = stripped.IndexOf('=');
                if (equals >= 0 && string.Equals(stripped[..equals].Trim(), key, StringComparison.Ordinal))
                {
                    lines[i] = rendered;
                    replaced = true;
                    break;
                }
            }

            string source = replaced
                ? string.Join("\n", lines)
                : string.Join("\n", lines) + "\n" + rendered + "\n";
            await File.WriteAllTextAsync(path, source, ct).ConfigureAwait(false);
            bool appliedLive = Runtime.Engine.SetRuntimeSetting(settings.ScriptId, key, value);
            return new ScriptSettingSaveResult(true, appliedLive, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScriptSettingSaveResult(false, false, ex.Message);
        }
    }

    /// <summary>
    /// Lints then atomically saves an editor buffer.
    /// Errors block the write.
    /// A changed on-disk hash returns a conflict unless overwrite was explicitly requested.
    /// </summary>
    public async Task<ScriptSaveResult> SaveDocumentAsync(
        ScriptDocument document,
        string source,
        bool overwriteConflict = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);

        BeaconLintReport lint = Lint(Path.GetFileName(document.Path), source);
        if (!lint.Ok)
            return new ScriptSaveResult(ScriptSaveOutcome.ValidationErrors, document, lint.Diagnostics);

        string current = await File.ReadAllTextAsync(document.Path, ct).ConfigureAwait(false);
        if (!overwriteConflict && !string.Equals(ContentHash(current), document.ContentHash, StringComparison.Ordinal))
            return new ScriptSaveResult(ScriptSaveOutcome.ExternalConflict, document, lint.Diagnostics);

        string directory = Path.GetDirectoryName(document.Path)
            ?? throw new InvalidOperationException("A script path must have a parent directory.");
        string temporary = Path.Combine(directory, "." + Path.GetFileName(document.Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, source, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temporary, document.Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        ScriptDocument saved = document with { Source = source, ContentHash = ContentHash(source) };
        return new ScriptSaveResult(ScriptSaveOutcome.Saved, saved, lint.Diagnostics);
    }

    private string ResolvePath(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string? directory = ScriptsDirectory
            ?? throw new InvalidOperationException("This client has no configuration folder.");
        string name = Path.GetFileNameWithoutExtension(id.Trim());
        if (!string.Equals(name, id.Trim(), StringComparison.Ordinal)
            && !string.Equals(id.Trim(), name + BeaconScriptDiscovery.ScriptExtension, StringComparison.Ordinal))
            throw new ArgumentException("Only top-level script ids are accepted.", nameof(id));

        return Path.Combine(directory, name + BeaconScriptDiscovery.ScriptExtension);
    }

    private static string ContentHash(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static string RenderSettingScalar(BeaconValue value) => value switch
    {
        BeaconTextValue text => "\"" + text.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        BeaconNumberValue number => number.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
        BeaconYesNoValue yesNo => yesNo.Value ? "true" : "false",
        _ => "\"\"",
    };
}

/// <summary>One discovered top-level script.</summary>
public sealed record ScriptInfo(string Id, string Path, bool Running);

/// <summary>An editor document plus the hash of the version read from disk.</summary>
public sealed record ScriptDocument(string Id, string Path, string Source, string ContentHash);

/// <summary>The result of creating a script from a template.</summary>
public sealed record ScriptCreateResult(bool Created, string? Path, string Error);

/// <summary>The result category of deleting one top-level script source.</summary>
public enum ScriptDeleteOutcome
{
    /// <summary>The source file was deleted.</summary>
    Deleted,

    /// <summary>The source file no longer existed.</summary>
    NotFound,
}

/// <summary>The result of deleting one top-level script source.</summary>
public sealed record ScriptDeleteResult(ScriptDeleteOutcome Outcome, bool StoppedRunningScript);

/// <summary>One script's declared settings and resolved current values.</summary>
public sealed record ScriptSettingsSnapshot(
    string ScriptId,
    IReadOnlyList<BeaconSettingDecl> Declarations,
    IReadOnlyDictionary<string, BeaconValue> Values,
    IReadOnlyList<BeaconDiagnostic> Diagnostics);

/// <summary>The result of writing one script setting.</summary>
public sealed record ScriptSettingSaveResult(bool Saved, bool AppliedLive, string Error);

/// <summary>The result category of a lint-gated atomic save.</summary>
public enum ScriptSaveOutcome
{
    /// <summary>The source was written atomically.</summary>
    Saved,

    /// <summary>Lint errors blocked the write.</summary>
    ValidationErrors,

    /// <summary>The on-disk file changed after the editor loaded it.</summary>
    ExternalConflict,
}

/// <summary>A script save result with the current document and all lint diagnostics.</summary>
public sealed record ScriptSaveResult(
    ScriptSaveOutcome Outcome,
    ScriptDocument Document,
    IReadOnlyList<BeaconDiagnostic> Diagnostics);
