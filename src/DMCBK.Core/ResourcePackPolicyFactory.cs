using DMCBK.Core.Configuration;
using DMCBK.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Umpk.Client;

namespace DMCBK.Core;

/// <summary>
/// Builds the UMPK resource-pack policy for a session from MCC's own configuration.
/// The policy is what the server actually sees: <c>Accept</c> (the default) answers SuccessfullyLoaded so a server with <c>require-resource-pack</c> keeps the session, <c>Decline</c> answers Declined, and <c>Prompt</c> asks the host once per push.
/// Accepted packs kick off a background translation load when <c>LoadResourcePackTranslations</c> is on; the wire answer never waits for the download, matching the retired 1.x client which answered first and extracted after.
/// </summary>
internal static class ResourcePackPolicyFactory
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(60);

    internal static ResourcePackPolicy Build(
        MccConfiguration? configuration,
        IHostInterface host,
        HostTranslations translations,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(translations);
        ILogger log = logger ?? NullLogger.Instance;
        ResourcePackPolicyMode mode = configuration?.Localization.ResourcePackPolicy ?? ResourcePackPolicyMode.Accept;
        bool loadTranslations = configuration?.Localization.LoadResourcePackTranslations ?? true;

        return new ResourcePackPolicy(request => Decide(request, mode, loadTranslations, configuration, host, translations, log));
    }

    private static ResourcePackResponse Decide(
        ResourcePackRequest request,
        ResourcePackPolicyMode mode,
        bool loadTranslations,
        MccConfiguration? configuration,
        IHostInterface host,
        HostTranslations translations,
        ILogger logger)
    {
        if (!ResourcePackTranslationLoader.IsDownloadableUrl(request.Url))
            return ResourcePackResponse.InvalidUrl;

        bool accept = mode switch
        {
            ResourcePackPolicyMode.Decline => false,
            ResourcePackPolicyMode.Prompt => AskHost(request, host, logger),
            _ => true,
        };

        if (!accept)
        {
            if (request.Required)
                logger.LogWarning("Declined a required resource pack from the server; the server may disconnect the session.");

            return ResourcePackResponse.Declined;
        }

        if (loadTranslations)
        {
            ResourcePackTranslationLoader loader = CreateLoader(configuration, translations, logger);
            // The server already gets SuccessfullyLoaded below; translations layer in when the download finishes.
            // A slow or failing pack must never stall the session loop.
            _ = Task.Run(() => LoadInBackgroundAsync(loader, request, logger), CancellationToken.None);
        }

        return ResourcePackResponse.SuccessfullyLoaded;
    }

    private static bool AskHost(ResourcePackRequest request, IHostInterface host, ILogger logger)
    {
        IResourcePackPrompt? prompt = host.ResourcePackPrompt;
        if (prompt is null)
        {
            logger.LogWarning("Resource pack policy is 'prompt' but the host offers no prompt; declining the pack.");
            return false;
        }

        var coreRequest = new ResourcePackPromptRequest(request.Id, request.Url, request.Hash, request.Required, PromptText: null);
        try
        {
            // Off the session loop thread so a host that awaits anything session-bound cannot deadlock against this wait; the session still waits for the answer, which is the point: the server holds the login until the client responds.
            Task<bool> task = Task.Run(() => prompt.PromptAsync(coreRequest, CancellationToken.None).AsTask());
            if (!task.Wait(PromptTimeout))
            {
                logger.LogWarning("Resource pack prompt timed out; declining the pack.");
                return false;
            }

            return task.Status == TaskStatus.RanToCompletion && task.Result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Resource pack prompt failed; declining the pack.");
            return false;
        }
    }

    private static async Task LoadInBackgroundAsync(
        ResourcePackTranslationLoader loader, ResourcePackRequest request, ILogger logger)
    {
        try
        {
            await loader.LoadAsync(request.Id, request.Url, request.Hash, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Background resource pack translation load failed.");
        }
    }

    internal static ResourcePackTranslationLoader CreateLoader(
        MccConfiguration? configuration, HostTranslations translations, ILogger logger)
        => new(translations, ResolveCacheDirectory(configuration), ResolveLanguage(configuration), logger);

    internal static string ResolveCacheDirectory(MccConfiguration? configuration)
    {
        if (configuration?.SourceFolder is { Length: > 0 } source)
        {
            string accountsCache = string.IsNullOrWhiteSpace(configuration.Accounts.CacheDirectory)
                ? "cache"
                : configuration.Accounts.CacheDirectory.Trim();
            return Path.Combine(source, accountsCache, "resourcepacks");
        }

        return Path.Combine(Path.GetTempPath(), "mcc-resourcepacks");
    }

    internal static string ResolveLanguage(MccConfiguration? configuration)
    {
        try
        {
            return Localization.UiCulture.ToMinecraftTag(
                Localization.UiCulture.Resolve(configuration?.Localization?.Language));
        }
        catch (Exception)
        {
            return "en_us";
        }
    }

    /// <summary>
    /// Clears pack translations for a received pop packet.
    /// UMPK decodes pops but has no applier for them, so the host watches <c>PacketReceived</c> and calls this.
    /// A pop with no id clears every pack, matching the retired 1.x behavior.
    /// Returns true when the packet was a pop.
    /// </summary>
    internal static bool ClearForPop(object? packet, HostTranslations translations)
    {
        ArgumentNullException.ThrowIfNull(translations);
        switch (packet)
        {
            case Umpk.Protocol.Java.Packets.ClientboundResourcePackPopPacket playPop:
                ClearOne(playPop.Id, translations);
                return true;
            case Umpk.Protocol.Java.Packets.ClientboundConfigResourcePackPopPacket configPop:
                ClearOne(configPop.Id, translations);
                return true;
            default:
                return false;
        }
    }

    private static void ClearOne(Guid? id, HostTranslations translations)
    {
        if (id is { } value && value != Guid.Empty)
            translations.RemovePackTranslations(value.ToString("D"));
        else
            translations.ClearPackTranslations();
    }
}
