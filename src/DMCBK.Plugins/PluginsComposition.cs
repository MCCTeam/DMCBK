using System.Globalization;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DMCBK.Core;

/// <summary>Host-selected plugin paths, references and diagnostics.</summary>
public sealed record PluginOptions(string Root)
{
    /// <summary>Host logger factory; the host retains ownership.</summary>
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;
    /// <summary>Diagnostic and plugin resource culture.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentUICulture;
    /// <summary>Explicit reference assets for embedded or single-file hosts.</summary>
    public ICompilationReferenceProvider? CompilationReferences { get; init; }
    /// <summary>Explicit local development packages, separate from managed installations.</summary>
    public IReadOnlyList<string> DevelopmentFolders { get; init; } = [];
}

/// <summary>Composes the optional plugin runtime.</summary>
public static class PluginsComposition
{
    /// <summary>Adds an independently owned host. Commands must be attached first.</summary>
    public static ClientBuilder UsePlugins(this ClientBuilder builder, PluginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseModule(client =>
        {
            var host = new PluginHost(client, options.Root, options.LoggerFactory, client.Translations,
                client.Variables, options.Culture, client.Configuration?.Plugins);
            if (options.CompilationReferences is not null) host.CompilationReferences = options.CompilationReferences;
            foreach (string folder in options.DevelopmentFolders) host.DevelopmentPluginFolders.Add(folder);
            return host;
        }, "plugins");
    }
}
