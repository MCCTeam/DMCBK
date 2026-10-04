using System.Globalization;

namespace DMCBK.Core.Commands;

/// <summary>
/// The single home for internal-command user-facing text.
/// Centralized (like the CLI host's <c>Strings</c>) so a future localization sweep can replace it wholesale; the command files never hold hardcoded literals.
/// No em dashes.
/// This fixes the catalogued hardcoded-string offenders (set, entity fields, chunk debug) by routing their text through here.
/// </summary>
internal static class CommandStrings
{
    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, format, args);

    public static string GeneralFail => TextResources.Get("command.text.literal_794548f29d31");
    public static string NeedTerrain => TextResources.Get("command.text.literal_bc7fe3c02244");
    public static string NeedInventory => TextResources.Get("command.text.literal_dbc5224cea5d");
    public static string NeedEntity => TextResources.Get("command.text.literal_9438940e3471");
    public static string ChunkNotLoaded => TextResources.Get("command.text.literal_96effa5f94d8");
    public static string UiRequiresTui =>
        TextResources.Get("command.text.literal_7706d1531fc7");
    public static string BrowserUsage => TextResources.Get("command.text.literal_fb9596501fe0");
    public static string ScriptsUiUsage => TextResources.Get("command.text.literal_43d38d8a9409");
    public static string AdvancementsUnavailable =>
        TextResources.Get("command.text.literal_a26455adbeb2");
    public static string RecipeUiUsage => TextResources.Get("command.text.literal_1c7a3b6401f7");
    public static string EntityUiUsage => TextResources.Get("command.text.literal_4cc5447fd458");
    public static string AdvancementUiUsage => TextResources.Get("command.text.literal_11fafabe45f6");
    public static string MccMenuDesc => TextResources.Get("command.text.literal_9d24d6363570");
    public static string MccMenuUsage => TextResources.Get("command.text.literal_d7c7b594923a");
    public static string ChunkUiUsage => TextResources.Get("command.text.literal_0b9f703b4283");

    public static string Error(string message) => F(TextResources.Get("command.text.literal_43f78154e1d7"), message);

    /// <summary>
    /// The one "there is nothing to show" line, so the six commands that can be empty stop phrasing it six ways: <c>/list</c> answered with a dangling <c>PlayerList:</c>, <c>/tab</c> with a full sentence, and the rest with their own variants.
    /// </summary>
    /// <param name="glyphs">The resolved glyph vocabulary.</param>
    /// <param name="what">What there is none of, already localized (for example "players online").</param>
    public static string NothingToShow(Presentation.GlyphSet glyphs, string what)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        return F(TextResources.Get("command.text.literal_17b9cb5fb152"), glyphs.Empty, what);
    }

    // Disconnect description.
    // One home for every host (see DisconnectDescription.Describe): the CLI, the TUI and any embedding host render the same fragment and none of them holds a literal.
    // Written as fragments, not sentences, because callers embed them ("Disconnected: {0}").
    public static string DisconnectClosed => TextResources.Get("command.text.literal_19cd24becb63");
    public static string DisconnectKicked => TextResources.Get("command.text.literal_65494f5dd924");
    public static string DisconnectLocal => TextResources.Get("command.text.literal_3e77e2654dd5");
    public static string DisconnectSocketEof => TextResources.Get("command.text.literal_d557278f559f");
    public static string DisconnectProtocolViolation => TextResources.Get("command.text.literal_54c2b7fe256c");
    public static string DisconnectCancelled => TextResources.Get("command.text.literal_e05452e831be");
    public static string DisconnectTransferred => TextResources.Get("command.text.literal_1f38cf190d0c");
    public static string DisconnectIdleTimeout => TextResources.Get("command.text.literal_b716183fb3a1");

    public static string DisconnectKickedWith(string message) => F(TextResources.Get("command.text.literal_1a79775aadd3"), message);

    public static string DisconnectWithFault(string reason, string fault) => F("{0} ({1})", reason, fault);

    /// <summary>
    /// A send that failed because the transport is gone.
    /// Deliberately worded as a lost connection rather than as a failed command: the raw transport text a broken pipe produces ("Unable to write data to the transport connection") names a symptom the user cannot act on and hides the fact that the session is over.
    /// </summary>
    public static string ConnectionLost(string reason) => F(TextResources.Get("command.text.literal_cf131526baa3"), reason);

    // Session-loop guard (see Client.WaitForCommandWork).
    public static string SessionLoopBlocked =>
        TextResources.Get("command.text.literal_4a420edf0530");

    public static string TpsCurrent(double tps) => F(TextResources.Get("command.text.literal_5c6efcbd9d9e"), tps);

    // Deliberately not "0 TPS" and not a stale reading.
    // The rate is derived from the game time the server broadcasts every 20 ticks; a server paused by 1.21.2+ pause-when-empty, or held by /tick freeze, stops broadcasting entirely, which a client cannot tell apart from an unreachable server.
    // Unknown is the only honest answer, so it is spelled out rather than dressed up as a number.
    public static string TpsUnknown =>
        TextResources.Get("command.text.literal_162f0a1184b5");

    /// <summary>
    /// The one-word form of <see cref="TpsUnknown"/>, for a column-aligned cell.
    /// The full sentence is a paragraph and broke the alignment of every row after it in <c>/debug state</c>; that paragraph is now a footnote under the table and this is what sits in the cell.
    /// </summary>
    public static string TpsUnknownShort => "unknown";

    /// <summary>
    /// Legacy's <c>Entitycmd.CmdDesc</c> was the empty string and the corpus has no key for it, so <c>/help entity</c> was the one page in 44 whose header stated no purpose at all.
    /// This is a new string for the new client, which is what CommandStrings is for.
    /// </summary>
    public static string EntityDesc => TextResources.Get("command.text.literal_d2d2e9a3ce51");

    public static string RespawnUnconfirmed => TextResources.Get("command.text.literal_de28ba30d2e3");
    public static string RespawnNotDead =>
        TextResources.Get("command.text.literal_624696b85623");

    public static string MoveArrived => TextResources.Get("command.text.literal_4efcb9e07a2a");
    // The catch-all in MoveCommand.DoMove used to swallow ex.Message entirely (formerly a bare "Could not reach the destination." const with no call site left once this landed), so every failure read identically no matter what Navigator actually threw ("No path to the goal was found.", "Navigation failed after exhausting replans.", a held-lease InvalidOperationException, ...).
    // Surfacing the reason is diagnostic, not cosmetic: it is the only way a transcript can tell "the planner found nothing" apart from "the planner found a path and running it failed".
    public static string MoveCannotReachWithReason(string reason) => F(TextResources.Get("command.text.literal_1c90a9d88900"), reason);

    /// <summary>
    /// The line for <c>MoveOutcome.StoppedNear</c>: the destination block is not a place a body fits, so the walk finished in the nearest block that is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not a legacy corpus string.</b> The 1.x client could not tell "I cannot stand there" apart from "I cannot get there"; it printed <c>cmd.move.fail</c> for both.
    /// So there is no legacy wording to keep parity with.
    /// The wording-parity constraint recorded in <c>MoveFailureReasonTests</c> binds the reasonless failure line, which is untouched.
    /// This is a new outcome and it gets a new sentence, in <c>CommandStrings</c> per AGENTS.md's rule for text with no 1.x counterpart.
    /// </para>
    /// <para>
    /// <b>It deliberately does not mention <c>-f</c>.</b> The suggestion attached to the ordinary failure points at a flag that relaxes the fall, fire and lava gates.
    /// No flag makes a cell occupiable, so repeating that advice here would send the user to re-run a command that cannot behave any differently.
    /// </para>
    /// <para>
    /// <b>It says plainly that this is not an arrival.</b> The body did move and it did get as close as a body can get, and the temptation is to let that read as success; the command still reports a failure status and the sentence has to agree with it.
    /// </para>
    /// <para>The leading clause is a stable prefix on purpose.</para>
    /// <para>Tooling greps client transcripts for the outcome lines.</para>
    /// </remarks>
    /// <param name="destination">The block the user asked for, already rendered.</param>
    /// <param name="stoppedAt">Where the body actually came to rest, already rendered.</param>
    public static string MoveStoppedNextToIt(string destination, string stoppedAt)
        => F(
            TextResources.Get("command.text.literal_48c0bc8bb800"),
            destination,
            stoppedAt);

    // useblock / useitem.
    // A use-on-block is whatever the target block decides it is and nothing comes back saying which, so these report the send and say so, rather than claiming the world changed.
    public static string HandMain => "main";
    public static string HandOff => "off";

    public static string UseBlockSent(int x, int y, int z)
        => F(TextResources.Get("command.text.literal_589523754169"), x, y, z);

    public static string UseItemSent(string hand)
        => F(TextResources.Get("command.text.literal_3862f115ebc7"), hand);
    public static string UseItemAtSent(string hand, int x, int y, int z)
        => F(TextResources.Get("command.text.literal_23805fffd6a4"),
            hand, x, y, z);

    public static string ChunkSetLoading(int x, int z) => F(TextResources.Get("command.text.literal_8b8a66009721"), x, z);
    public static string ChunkSetLoaded(int x, int z) => F(TextResources.Get("command.text.literal_90492758410f"), x, z);
    public static string ChunkDeleted(int x, int z) => F(TextResources.Get("command.text.literal_5b13652afd44"), x, z);
    public static string ChunkNotPresent => TextResources.Get("command.text.literal_dff5454ba5fb");

    // changeslot
    public static string ChangeSlotDesc => TextResources.Get("command.text.literal_23f78c400c79");
    public static string ChangeSlotChanged(int slot) => F(TextResources.Get("command.text.literal_98e281b9af62"), slot);
    public static string ChangeSlotOutOfRange => TextResources.Get("command.text.literal_a6bfd8c3b8df");

    /// <summary>Selecting by item names the slot AND what is in it, so the answer confirms both.</summary>
    public static string ChangeSlotChangedTo(int slot, string item) => F(TextResources.Get("command.text.literal_46617f70d094"), slot, item);

    /// <summary>
    /// Deliberately says HOTBAR rather than "inventory": the item may well be in the inventory, and saying only "not found" would send the reader looking for a bug that is not there.
    /// </summary>
    public static string ChangeSlotNoSuchItem(string item) => F(TextResources.Get("command.text.literal_211c582283ee"), item);

    public static string DropItemNone(string item) => F(TextResources.Get("command.text.literal_10106dad2539"), item);

    // enchant
    public static string EnchantDesc => TextResources.Get("command.text.literal_78f5decaefe3");
    public static string EnchantNoTable => TextResources.Get("command.text.literal_540b09915707");
    public static string EnchantDone => TextResources.Get("command.text.literal_8954159d65a4");

    // nameitem
    public static string NameItemDesc => TextResources.Get("command.text.literal_fa46b6b226cd");
    public static string NameItemEmpty => TextResources.Get("command.text.literal_de9591fa55fd");
    public static string NameItemNoAnvil => TextResources.Get("command.text.literal_1323a294314d");
    public static string NameItemFirstSlotEmpty => TextResources.Get("command.text.literal_f1ad454480e0");
    public static string NameItemDone(string name) => F(TextResources.Get("command.text.literal_47b77cf4999d"), name);
    // 1.8-1.12.2 rename over the MC|ItemName plugin channel and 1.13+ over the dedicated rename_item packet, so every supported version can carry one of the two routes and this refusal is now unreachable in practice.
    // It stays because the capability, not this command, decides: naming a version that genuinely cannot rename beats printing a success the server never received.
    public static string NameItemUnsupported =>
        TextResources.Get("command.text.literal_7c6fe7a4de33");

    // recipebook
    public static string RecipeBookDesc => TextResources.Get("command.text.literal_72610fdcffe0");
    public static string RecipeBookNone => TextResources.Get("command.text.literal_1815a75a610a");
    public static string RecipeBookEntry(string id) => F("  {0}", id);

    /// <summary>
    /// One listing row: the name a player recognises, then the identifier they actually type.
    /// </summary>
    /// <remarks>
    /// Both halves earn their place.
    /// The name is what the recipe IS ("Acacia Boat"); the identifier is what <c>recipebook craft</c> takes, so a listing that showed only the name would leave nothing on screen that could be typed back.
    /// </remarks>
    public static string RecipeBookNamedEntry(string name, string id, int pad)
        => F(TextResources.Get("command.text.literal_e99b2280dc91"), name.PadRight(pad), id);
    public static string RecipeBookNumericEntry(int id) => F("  #{0}", id);
    public static string RecipeBookHeader(int count) => F(TextResources.Get("command.text.literal_72399ea47e64"), count);
    // The opaque count is a count of UNDECODED UPDATES, not of recipes: UMPK's RecipeState increments it once per recipe_book_add frame that carried a non-empty payload, and that payload is a nested recipe-display tree it does not decode.
    // A 1.21.2+ join typically arrives as a SINGLE such frame carrying every unlocked recipe, so wording this as "N addition(s)" would tell the reader their book holds one recipe when it may hold hundreds.
    // The honest report is that the number of recipes is unknown.
    public static string RecipeBookOpaqueOnly(int updates)
        => F(
            TextResources.Get("command.text.literal_1b3ebfc4be00"),
            updates);

    public static string RecipeBookOpaqueNote(int updates)
        => F(
            TextResources.Get("command.text.literal_42770597e748"),
            updates);

    // Recipe placement has ONE live wire form per version and the VERSION decides which, so these refusals name the version that refused.
    // Picking the form from the shape of what the user typed reported "Placed recipe X." over a request that could not leave the client at all.
    public static string RecipeBookUnsupported(string version)
        => F(
            TextResources.Get("command.text.literal_14132dd4b88e"),
            version);

    public static string RecipeBookNeedsResourceName(string version, string typed)
        => F(
            TextResources.Get("command.text.literal_47be97b4e5be"),
            version, typed);

    public static string RecipeBookNeedsNetworkId(string version, string typed)
        => F(
            TextResources.Get("command.text.literal_a67ff6649412"),
            version, typed);

    public static string RecipeBookNoMenu => TextResources.Get("command.text.literal_e17449d1eda2");
    public static string RecipeBookPlaced(string id) => F(TextResources.Get("command.text.literal_b0216d912b58"), id);
    public static string RecipeBookFail => TextResources.Get("command.text.literal_682abf6769b8");

    public static string EntityDetail(
        int id, string type, string name, double x, double y, double z, double vx, double vy, double vz)
        => F(
            TextResources.Get("command.text.literal_6ff5a3ed436f"),
            id, type, name, x, y, z, vx, vy, vz);

    public static string DialogUnsupported => TextResources.Get("command.text.literal_c998038fdb23");

    // debug state: server facts.
    // The two latency figures are labelled apart on purpose.
    // "latency" is the server-measured round trip published in the tab list and is the only real ping a Java client has; "keepalive-turnaround" is our own responder-side turnaround, which is NOT a round trip (the client is only ever the responder and the keep-alive id is the server's own clock), so it is never called ping.
    public static string DebugStateBrand(string brand) => F(TextResources.Get("command.text.literal_99957960a498"), brand);
    public static string DebugStateBrandUnknown => TextResources.Get("command.text.literal_4dd337217759");
    public static string DebugStateLatency(int milliseconds) => F(TextResources.Get("command.text.literal_a3435427a1f2"), milliseconds);
    public static string DebugStateLatencyUnknown => TextResources.Get("command.text.literal_b98a549b8409");
    public static string DebugStateTps(double tps) => F(TextResources.Get("command.text.literal_ad6edf3eed4d"), tps);
    public static string DebugStateTpsUnknown => TextResources.Get("command.text.literal_7f4ed44c6d2f");
    public static string DebugStateKeepAlive(double turnaroundMs, double intervalSeconds)
        => F(TextResources.Get("command.text.literal_18a67142988b"), turnaroundMs, intervalSeconds);
    public static string DebugStateKeepAliveTurnaroundOnly(double turnaroundMs)
        => F(TextResources.Get("command.text.literal_5c4b1fe2bbfc"), turnaroundMs);

    // connect: realm resolution
    public static string ConnectRealmResolving(string world) => F(TextResources.Get("command.text.literal_967b33add709"), world);
    public static string ConnectRealmResolved(string world, string host, int port)
        => F(TextResources.Get("command.text.literal_d6cab298695a"), world, host, port);
    public static string ConnectRealmRequiresMicrosoft =>
        TextResources.Get("command.text.literal_0167d4168af1");
    public static string ConnectRealmTermsNotAgreed =>
        TextResources.Get("command.text.literal_6b4f83f7816f");
    public static string ConnectRealmAccessDenied =>
        TextResources.Get("command.text.literal_1d8d4a0bad20");
    public static string ConnectRealmWorldNotFound(string world)
        => F(TextResources.Get("command.text.literal_28d809a4be80"), world);
    public static string ConnectRealmServiceBusy =>
        TextResources.Get("command.text.literal_a7e527827766");
    public static string ConnectRealmClientOutdated =>
        TextResources.Get("command.text.literal_bf872a4b7a92");
    public static string ConnectRealmServiceError => TextResources.Get("command.text.literal_ef17a38f2056");
    public static string ConnectRealmNoVersion =>
        TextResources.Get("command.text.literal_51be764e1ffc");

    public static string ReloadNoConfig => TextResources.Get("command.text.literal_e03f692a118a");
    public static string ReloadFailed(string message) => F(TextResources.Get("command.text.literal_a398927ccb95"), message);

    public static string SetDone(string name, string value) => F(TextResources.Get("command.text.literal_baa0edc1be03"), name, value);

    public static string PluginsNoHost => TextResources.Get("command.text.literal_ad1a5d44cf74");

    // plugin load
    public static string PluginsHeader(int count) => F(TextResources.Get("command.text.literal_58129c8b2e59"), count);
    public static string PluginsNoneDiscovered => TextResources.Get("command.text.literal_59c733469208");

    /// <summary>One list row: state glyph, id, version, and the entry kind.</summary>
    public static string PluginsRow(string glyph, string id, string version, string entry)
        => F("  {0} {1} {2} ({3})", glyph, id, version, entry);

    /// <summary>Appended to a row whose plugin is enabled but is not currently loaded.</summary>
    public static string PluginsNotLoaded => TextResources.Get("command.text.literal_df5c0f506268");

    public static string PluginsEntryCompiled => "dll";
    public static string PluginsEntrySource => "source";

    // gutted commands
    public static string RemovedUpgrade => TextResources.Get("command.text.literal_9a1ff46ff229");
    public static string RemovedScript => TextResources.Get("command.text.literal_7213e30b6403");

    // scripts (Beacon).
    // New-client text with no 1.x counterpart, so it lives here rather than the corpus: the corpus is generated from the archived resx and these lines never existed there.
    // Wording is pinned by Beacon.ParityTests (mute visibility, stop counts, list shape): change a sentence here and update the tests in the same commit.
    public static string ScriptsDesc => TextResources.Get("command.text.literal_580451fe3b14");
    public static string ScriptsRunUsage => TextResources.Get("command.text.literal_d1c5559807c6");
    public static string ScriptsUnknownFlag(string token)
        => F(TextResources.Get("command.text.literal_baefaf4e16ad"), token, ScriptsRunUsage);
    public static string ScriptsCouldNotRead(string path, string reason)
        => F(TextResources.Get("command.text.literal_e3f21c981717"), path, reason);
    public static string ScriptsRan(string scriptId) => F(TextResources.Get("command.text.literal_7ba91f1ac3c7"), scriptId);
    public static string ScriptsMoreErrors(int rest) => F(TextResources.Get("command.text.literal_4f4d29326201"), rest);
    public static string ScriptsTraceSeparator => TextResources.Get("command.text.literal_42f5e7431595");
    public static string ScriptsFailedNoDiagnostic(string scriptId)
        => F(TextResources.Get("command.text.literal_6a64794c5f67"), scriptId);
    public static string ScriptsDiagnosticLine(string code, string message) => F("[{0}] {1}", code, message);
    public static string ScriptsTryThis(string suggestion) => F(TextResources.Get("command.text.literal_c2ac0a41bb80"), suggestion);
    public static string ScriptsStopUsage => TextResources.Get("command.text.literal_dbaf0a71b8e0");
    public static string ScriptsStopped(int count) => F(TextResources.Get("command.text.literal_520b5b4cca43"), count);
    public static string ScriptsStoppedOne(string target) => F(TextResources.Get("command.text.literal_e382221ae5fc"), target);
    public static string ScriptsUnknownScript(string target) => F(TextResources.Get("command.text.literal_22494eff59e6"), target);
    public static string ScriptsEventHandlerFailed(string eventName, string reason)
        => F(TextResources.Get("command.text.literal_e11562bff638"), eventName, reason);
    public static string ScriptsSelfNameFallback => "you";
    public static string ScriptsUnknownDeathCause => "unknown";
    public static string ScriptsReloadNothing => TextResources.Get("command.text.literal_7eb0372d4826");
    public static string ScriptsReloadNotFound(string target) => F(TextResources.Get("command.text.literal_e5b432311c00"), target);
    public static string ScriptsReloadUnreadable(string target, string path, string reason)
        => F(TextResources.Get("command.text.literal_e3a35a17417f"), target, path, reason);
    public static string ScriptsReloadNoDiagnostic(string target) => F(TextResources.Get("command.text.literal_8e6e90761d76"), target);
    public static string ScriptsReloadFailure(string target, string code, string message)
        => F("'{0}': [{1}] {2}", target, code, message);
    public static string ScriptsReloaded(int ok, int total) => F(TextResources.Get("command.text.literal_136114444fb8"), ok, total);
    public static string ScriptsReloadedWithFailures(int ok, int total, string failures)
        => F(TextResources.Get("command.text.literal_de7e870d7e08"), ok, total, failures);

    // Beacon world sign and container verbs.
    // New-client text with no 1.x counterpart, so it lives here rather than the corpus.
    // Every world.sign_text, world.find_signs, and world.use message a script author can see comes from here, never from a literal at the call site.
    public static string ScriptsWorldCatchSuggestion => TextResources.Get("command.text.literal_fc65e3fe5480");
    public static string ScriptsWorldArity(string verb, int got) => F(TextResources.Get("command.text.literal_b0fa233657d9"), verb, got);
    public static string ScriptsWorldSignTextUsage => TextResources.Get("command.text.literal_0ab6b8f6c450");
    public static string ScriptsWorldFindSignsUsage => TextResources.Get("command.text.literal_9a460854e191");
    public static string ScriptsWorldUseUsage => TextResources.Get("command.text.literal_931019866793");
    public static string ScriptsWorldFindSignsNeedle => TextResources.Get("command.text.literal_d21a923866ad");
    public static string ScriptsWorldSignTextFailed(int x, int y, int z, string reason)
        => F(TextResources.Get("command.text.literal_b0bee51326c2"), x, y, z, reason);
    public static string ScriptsWorldFindSignsFailed(string needle, int radius, int max, string reason)
        => F(TextResources.Get("command.text.literal_c9f615b11a6a"), needle, radius, max, reason);
    public static string ScriptsWorldUseFailed(int x, int y, int z, string reason)
        => F(TextResources.Get("command.text.literal_e87790615388"), x, y, z, reason);

    // Beacon movement verbs.
    // New-client text with no 1.x counterpart, so it lives here rather than the corpus.
    // Every move_goto timeout message a script author can see comes from here, never from a literal at the call site.
    public static string ScriptsMovementTimedOut(string owner, string target, double seconds)
        => F(TextResources.Get("command.text.literal_1dbc204e36c3"), owner, target, seconds);
    public static string ScriptsLintUsage =>
        TextResources.Get("command.text.literal_361c1148d84f");
    public static string ScriptsLintFailure(string detail) => F(TextResources.Get("command.text.literal_086a8440080d"), detail);
    public static string ScriptsLintUsageError => TextResources.Get("command.text.literal_d23ed0481702");
    public static string ScriptsNewUsage(string templates)
        => F(TextResources.Get("command.text.literal_cc150daad175"), templates);
    public static string ScriptsNewNoFolder => TextResources.Get("command.text.literal_6b76d2a8ef07");
    public static string ScriptsCreated(string path, string template)
        => F(TextResources.Get("command.text.literal_a7b5a7a2caed"), path, template);
    public static string ScriptsReplUsage => TextResources.Get("command.text.literal_b6dfc4b395ea");
    public static string ScriptsFileNotFound(string given) => F(TextResources.Get("command.text.literal_b2064cf35a2a"), given);
    public static string ScriptsIdNotFound(string given, string candidate)
        => F(TextResources.Get("command.text.literal_0f6d77d80a8d"), given, candidate);
    public static string ScriptsIdNotFoundNoFolder(string given)
        => F(TextResources.Get("command.text.literal_6af493561ec7"), given);
    public static string ScriptsListEmpty => TextResources.Get("command.text.literal_5a73d20af0dc");
    public static string ScriptsListHeader(int count) => F(TextResources.Get("command.text.literal_79501a809556"), count);
    public static string ScriptsListMuted(int held) => F(TextResources.Get("command.text.literal_8aa60c0f14f6"), held);
    public static string ScriptsMuteStatus(bool muted, int held)
        => F(TextResources.Get("command.text.literal_fed85da6109b"), muted ? "on" : "off", held);
    public static string ScriptsEventHandlerFailed(
        string scriptId, string hook, string message, string code, string file, int line)
        => F(TextResources.Get("command.text.literal_1b74d4c1ce67"),
            scriptId, hook, message, code, file, line);
    public static string ScriptsCommandDesc(string pattern, string scriptId)
        => F(TextResources.Get("command.text.literal_f5009d0e8cda"), pattern, scriptId);
    /// <summary>Explains why a script command did not replace an existing command.</summary>
    public static string ScriptsCommandAlreadyRegistered(string name)
        => F(TextResources.Get("cmd.scripts.command_already_registered"), name);
    public static string ScriptsCommandUsage(string usage)
        => F(TextResources.Get("command.text.literal_a2ba5f3aeb93"), usage);
    public static string ScriptsWatchOn(string dir) => F(TextResources.Get("command.text.literal_7afb74c89abe"), dir);
    public static string ScriptsWatchOff => TextResources.Get("command.text.literal_ccdf973745e7");
    public static string ScriptsWatchNoFolder => TextResources.Get("command.text.literal_5525fb0a2317");
    public static string ScriptsWatchStatusOff => TextResources.Get("command.text.literal_5e1254546ecc");
    public static string ScriptsWatchStatusOn(string dir) => F(TextResources.Get("command.text.literal_7afb74c89abe"), dir);
    public static string ScriptsConfigUsage => TextResources.Get("command.text.literal_615983577fd3");
    public static string ScriptsFormatUsage => TextResources.Get("command.text.literal_19182be544a7");
    public static string ScriptsFormatUnknownFlag(string token)
        => F(TextResources.Get("command.text.literal_baefaf4e16ad"), token, ScriptsFormatUsage);
    public static string ScriptsFormatClean(string path) => F(TextResources.Get("command.text.literal_5d57d184b64c"), path);
    public static string ScriptsFormatWouldChange(string path)
        => F(TextResources.Get("command.text.literal_f08e42afb061"), path);
    public static string ScriptsFormatted(string path) => F(TextResources.Get("command.text.literal_f764fcfbf32f"), path);
    public static string ScriptsFormatWriteFailed(string path, string reason)
        => F(TextResources.Get("command.text.literal_4d8eca55c1d2"), path, reason);
    public static string ScriptsFormatHasErrors =>
        TextResources.Get("command.text.literal_28dfa8aa40da");
    public static string ScriptsConfigNoSchema(string scriptId)
        => F(TextResources.Get("command.text.literal_0dd23322cec3"), scriptId);
    public static string ScriptsConfigHeader(string scriptId) => F(TextResources.Get("command.text.literal_264a31fc57cf"), scriptId);
    public static string ScriptsConfigRow(string key, string value, string comment)
        => string.IsNullOrWhiteSpace(comment) ? F("{0} = {1}", key, value) : F("{0} = {1} ; {2}", key, value, comment.Trim());
    public static string ScriptsConfigUnknownKey(string scriptId, string key)
        => F(TextResources.Get("command.text.literal_5d06cee79245"), scriptId, key);
    public static string ScriptsConfigNoFolder => TextResources.Get("command.text.literal_1b4078a381d2");
    public static string ScriptsConfigWriteFailed(string path, string reason)
        => F(TextResources.Get("command.text.literal_fc7ef1828115"), path, reason);
    public static string ScriptsConfigSet(string scriptId, string key, string value, bool live)
        => live
            ? F(TextResources.Get("command.text.literal_5236ea994691"), scriptId, key, value)
            : F(TextResources.Get("command.text.literal_62ce8fa3db3d"), scriptId, key, value);

    // scoreboard.
    // New-client text with no 1.x counterpart: the old client tracked objectives and scores for bots but never printed them (only `teams` listed anything), so these lines live here rather than in the corpus.
    public static string ScoreboardDesc => TextResources.Get("command.text.literal_e2d732929471");
    public static string ScoreboardUiUsage => TextResources.Get("command.text.literal_0b8c4fd4f940");
    public static string ScoreboardHeader(int count) => F(TextResources.Get("command.text.literal_3a5ba40c05d0"), count);
    public static string ScoreboardEntries(int count) => count == 1 ? TextResources.Get("command.text.literal_17a2552ef847") : F(TextResources.Get("command.text.literal_12dafdfd0547"), count);
    public static string ScoreboardHearts => "hearts";
    public static string ScoreboardMore(int rest) => F(TextResources.Get("command.text.literal_3d49a27eb311"), rest);
    public static string ScoreboardUnknown(string name) => F(TextResources.Get("command.text.literal_8b10ef81353a"), name);
    public static string ScoreboardKnown(string names) => F(TextResources.Get("command.text.literal_57978472b342"), names);
    public static string ScoreboardNoScores(string name) => F(TextResources.Get("command.text.literal_b129e451418b"), name);
}
