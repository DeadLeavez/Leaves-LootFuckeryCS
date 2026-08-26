using System.Text;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_LootFuckery;

// Records EVERY real raid's generated loot to disk, full fidelity, kept forever. Fed from a Harmony
// postfix on LocationLifecycleService.GenerateLocationAndLoot (see GenerateLocationAndLootPatch) - the
// one method both vanilla SPT and Fika call to build a raid's loot. Storage is the source data for the
// web-UI run list now, and a per-raid loot MAP later, so the whole loot layout (positions + item trees)
// is written untouched rather than the summarised tally the dataDump produces.
//
// Singleton so the postfix (which reaches us through the DI ServiceLocator) always hits the one
// initialised instance. LootFuckery.OnLoad calls Init once before enabling the patch.
[Injectable(InjectionType.Singleton)]
public class RealRunCapture(
    ProfileHelper profileHelper,
    JsonUtil jsonUtil)
{
    private LootFuckeryConfig config = null!;
    private string historyFolder = null!;
    private string indexFile = null!;
    private string extractionsFile = null!;
    private string gearFolder = null!;
    private string gearIndexFile = null!;
    private bool ready;

    // In-memory id-set for the most recent captured raids, so an extract can be matched back to its
    // raid without re-reading the (large) run files. A raid's spawned item ids are unique to that one
    // generation, so whichever recent run's id-set a player's carried items intersect IS the raid they
    // were in (self-linking - session and ServerId don't bridge start→end here; see the mod HANDOFF).
    // Bounded to the last few raids; a raid never outlives a server restart, so losing this on restart
    // is fine.
    private readonly List<RecentRun> recentRuns = new();
    private const int RecentRunCap = 30;

    // Serialises disk writes. Real raids start one at a time, but a Fika host and a transit could in
    // principle overlap, and we're appending to a shared index file - cheap insurance either way.
    private readonly object writeLock = new();

    // Set true by LootFuckery while its OWN simulated dump loop runs. That loop calls the very method we
    // patched, so without this the postfix would try to record 250 fake generations as real raids. This
    // is the belt to the real-profile gate's suspenders (a dump uses a random fake session id that won't
    // resolve to a profile) - either one alone is enough; both together make it impossible to slip.
    public bool SuppressCapture { get; set; }

    public void Init(LootFuckeryConfig lootFuckeryConfig, string modPath)
    {
        config = lootFuckeryConfig;
        historyFolder = System.IO.Path.Combine(modPath, "output", "history");
        indexFile = System.IO.Path.Combine(historyFolder, "index.jsonl");
        extractionsFile = System.IO.Path.Combine(historyFolder, "extractions.jsonl");
        gearFolder = System.IO.Path.Combine(historyFolder, "gear");
        gearIndexFile = System.IO.Path.Combine(gearFolder, "index.jsonl");
        ready = true;
    }

    // Called from the postfix for EVERY GenerateLocationAndLoot call. Filters down to real raids, then
    // writes the run. Never throws into the game's raid-start path - any failure is logged and swallowed.
    public void Capture(MongoId sessionId, string mapName, LocationBase? generated)
    {
        if (!ready || !config.CaptureRealRuns || SuppressCapture)
        {
            return;
        }
        if (generated?.Loot == null)
        {
            return;
        }

        // The real-profile gate: a genuine raid carries the player's session id, which resolves to a PMC.
        // Our simulated dumps (and any other synthetic call) use a session with no profile behind it.
        var pmc = profileHelper.GetPmcProfile(sessionId);
        string? nickname = pmc?.Info?.Nickname;
        if (string.IsNullOrEmpty(nickname))
        {
            return;
        }

        try
        {
            WriteRun(sessionId, nickname, mapName, generated);
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[LootFuckery] Failed to capture real run for '{mapName}': {ex.Message}", LogTextColor.Red);
        }
    }

    private void WriteRun(MongoId sessionId, string nickname, string mapName, LocationBase generated)
    {
        // Count points + items and collect every spawned item id in one pass. The counts feed the index
        // line; the id-set is kept in memory (recentRuns) so an extract can be matched back to this raid.
        int lootPoints = 0;
        int itemCount = 0;
        var spawnedIds = new HashSet<string>();
        foreach (var point in generated.Loot!)
        {
            lootPoints++;
            if (point.Items != null)
            {
                foreach (var item in point.Items)
                {
                    itemCount++;
                    if (item?.Id != null)
                    {
                        spawnedIds.Add(item.Id.ToString());
                    }
                }
            }
        }

        var now = DateTime.UtcNow;
        string capturedUtc = now.ToString("o");
        string safeMap = Sanitize(mapName);
        string safePlayer = Sanitize(nickname);
        string fileName = $"{now:yyyyMMdd_HHmmss}_{safePlayer}.json";
        string relativePath = $"{safeMap}/{fileName}";

        // The full-fidelity run file: metadata header + the ENTIRE generated loot array untouched, so a
        // later map view reads positions straight out of it. Minified - these are big and machine-read.
        var run = new RunFile(mapName, capturedUtc, sessionId.ToString(), nickname, lootPoints, itemCount, generated.Loot);

        lock (writeLock)
        {
            string mapDir = System.IO.Path.Combine(historyFolder, safeMap);
            System.IO.Directory.CreateDirectory(mapDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(mapDir, fileName), jsonUtil.Serialize(run, false) ?? "");

            // One compact line appended to the index the web UI lists from - cheap to read without
            // opening every multi-megabyte run file. Kept in sync with the files by always writing both.
            var indexEntry = new IndexEntry(mapName, relativePath, capturedUtc, nickname, sessionId.ToString(), lootPoints, itemCount);
            System.IO.File.AppendAllText(indexFile, (jsonUtil.Serialize(indexEntry, false) ?? "") + "\n", new UTF8Encoding(false));

            // Remember this raid's spawn ids for extract-matching; drop the oldest once over the cap.
            recentRuns.Add(new RecentRun(relativePath, mapName, spawnedIds));
            while (recentRuns.Count > RecentRunCap)
            {
                recentRuns.RemoveAt(0);
            }
        }

        Leaves.Utils.Instance.printColor($"[LootFuckery] Captured real run: {relativePath} ({lootPoints} points, {itemCount} items)");
    }

    // Called from the EndLocalRaid postfix for every player's extract. Matches what they carried out
    // against the recent raids' spawn ids and, if any belong to a captured raid, appends one line to
    // extractions.jsonl. Players who brought nothing captured out (died, empty-handed, or a raid that
    // predates this server start) are simply skipped - the design only records loot that came out.
    // Never throws into the raid-end path.
    public void RecordExtraction(MongoId sessionId, EndLocalRaidRequestData? request)
    {
        if (!ready || !config.CaptureRealRuns)
        {
            return;
        }

        var profile = request?.Results?.Profile;
        var items = profile?.Inventory?.Items;
        if (items == null)
        {
            return;
        }

        // Prefer the live profile's nickname, fall back to the one in the submitted profile.
        var pmc = profileHelper.GetPmcProfile(sessionId);
        string? nickname = pmc?.Info?.Nickname;
        if (string.IsNullOrEmpty(nickname))
        {
            nickname = profile?.Info?.Nickname;
        }
        if (string.IsNullOrEmpty(nickname))
        {
            return;
        }

        // Every item id the player brought out (their whole submitted inventory; the intersection below
        // narrows it to just what this raid spawned, so pre-existing stash items never match).
        var playerIds = new HashSet<string>();
        foreach (var item in items)
        {
            if (item?.Id != null)
            {
                playerIds.Add(item.Id.ToString());
            }
        }
        if (playerIds.Count == 0)
        {
            return;
        }

        try
        {
            WriteExtraction(sessionId, nickname, request!, items, playerIds);
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[LootFuckery] Failed to record extraction for '{nickname}': {ex.Message}", LogTextColor.Red);
        }
    }

    private void WriteExtraction(MongoId sessionId, string nickname, EndLocalRaidRequestData request, List<Item> items, HashSet<string> playerIds)
    {
        // Pick the recent run whose spawn ids the player's carried items intersect most - that's their
        // raid. recentRuns is the only shared state touched under lock; a RecentRun's id-set is never
        // mutated after creation, so reading best.SpawnedIds outside the lock (below) is safe.
        RecentRun? best = null;
        int bestCount = 0;
        lock (writeLock)
        {
            for (int i = recentRuns.Count - 1; i >= 0; i--)
            {
                var run = recentRuns[i];
                int count = 0;
                foreach (var id in playerIds)
                {
                    if (run.SpawnedIds.Contains(id))
                    {
                        count++;
                    }
                }
                if (count > bestCount)
                {
                    bestCount = count;
                    best = run;
                }
            }
        }

        if (best == null || bestCount == 0)
        {
            return;
        }

        // The actual brought-out items that belong to this raid (id present in the run's spawn set).
        // stack = StackObjectsCount so ammo/money stacks carry their real quantity (a stack of 60 rounds
        // is one item instance but 60 rounds); absent upd (most gear) means a single item → 1.
        var broughtOut = new List<BroughtOutItem>();
        foreach (var item in items)
        {
            if (item?.Id != null && best.SpawnedIds.Contains(item.Id.ToString()))
            {
                int stack = (int)(item.Upd?.StackObjectsCount ?? 1);
                broughtOut.Add(new BroughtOutItem(item.Id.ToString(), item.Template.ToString(), stack));
            }
        }

        string exitStatus = request.Results?.Result?.ToString() ?? "";
        var entry = new ExtractionEntry(
            best.RelativePath, best.Map, nickname, sessionId.ToString(), exitStatus,
            DateTime.UtcNow.ToString("o"), broughtOut.Count, broughtOut);

        lock (writeLock)
        {
            System.IO.File.AppendAllText(extractionsFile, (jsonUtil.Serialize(entry, false) ?? "") + "\n", new UTF8Encoding(false));
        }

        Leaves.Utils.Instance.printColor($"[LootFuckery] Recorded extraction: {nickname} brought {broughtOut.Count} item(s) out of {best.Map} ({exitStatus})");
    }

    // ============================================================
    // Gear snapshots (captureGearSnapshots) - separate from the loot capture above.
    // ============================================================
    //
    // The forensic side: dump a player's ENTIRE profile inventory tree (equipment + every container's
    // contents, raw and untouched) at raid start and raid end, so a vanished item can be pinned to WHEN
    // it disappeared. Three snapshots per raid: entry (server profile before deploy), exit-submitted
    // (what the client sent back), exit-server (server profile after SPT applied raid results). Diff
    // entry→exit-submitted for an in-raid loss, exit-submitted→exit-server for a server-processing loss.
    // All three carry the same session id, which is the pairing key (each real player's own start and end
    // share it - unlike the loot capture, where a headless generates once for the whole group).

    // Called from the StartLocalRaid postfix for every real player deploying. Reads the server's stored
    // PMC profile - by this point the menu's item-moving calls have synced the loadout - and writes it
    // as the "entry" snapshot. Never throws into the raid-start path.
    public void RecordEntry(MongoId sessionId)
    {
        if (!ready || !config.CaptureGearSnapshots)
        {
            return;
        }

        // One outer catch over EVERYTHING - the profile read included - so nothing this method does can
        // ever propagate back into the raid-start path. This runs on the LIVE server; capture is strictly
        // best-effort observation and must never be able to break a real raid.
        try
        {
            var pmc = profileHelper.GetPmcProfile(sessionId);
            var items = pmc?.Inventory?.Items;
            string? nickname = pmc?.Info?.Nickname;
            if (items == null || string.IsNullOrEmpty(nickname))
            {
                return;
            }
            WriteGearSnapshot(sessionId, nickname, "entry", "", items);
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[LootFuckery] Failed to record entry gear: {ex.Message}", LogTextColor.Red);
        }
    }

    // Called from the EndLocalRaid postfix for every player's extract. Writes TWO snapshots: the client's
    // submitted inventory (exit-submitted) and the server's stored profile after SPT processed the raid
    // (exit-server - the postfix runs after the original EndLocalRaid, so the live profile here is the
    // post-processed state). Either source being absent just skips that one snapshot. Never throws.
    public void RecordExit(MongoId sessionId, EndLocalRaidRequestData? request)
    {
        if (!ready || !config.CaptureGearSnapshots)
        {
            return;
        }

        // Outer catch over the whole method (profile/request reads included) - as with RecordEntry, this
        // must never throw back into the raid-END path on the live server. The two snapshots keep their
        // OWN inner try/catch as well, so a failure writing one still lets the other be attempted; the
        // outer catch is the final backstop for anything the inner ones don't cover (e.g. the reads).
        try
        {
            var submittedProfile = request?.Results?.Profile;
            string exitStatus = request?.Results?.Result?.ToString() ?? "";

            // Prefer the live profile's nickname, fall back to the submitted one (as in RecordExtraction).
            var pmc = profileHelper.GetPmcProfile(sessionId);
            string? nickname = pmc?.Info?.Nickname;
            if (string.IsNullOrEmpty(nickname))
            {
                nickname = submittedProfile?.Info?.Nickname;
            }
            if (string.IsNullOrEmpty(nickname))
            {
                return;
            }

            var submittedItems = submittedProfile?.Inventory?.Items;
            if (submittedItems != null)
            {
                try
                {
                    WriteGearSnapshot(sessionId, nickname, "exit-submitted", exitStatus, submittedItems);
                }
                catch (Exception ex)
                {
                    Leaves.Utils.Instance.printColor($"[LootFuckery] Failed to record exit-submitted gear for '{nickname}': {ex.Message}", LogTextColor.Red);
                }
            }

            var serverItems = pmc?.Inventory?.Items;
            if (serverItems != null)
            {
                try
                {
                    WriteGearSnapshot(sessionId, nickname, "exit-server", exitStatus, serverItems);
                }
                catch (Exception ex)
                {
                    Leaves.Utils.Instance.printColor($"[LootFuckery] Failed to record exit-server gear for '{nickname}': {ex.Message}", LogTextColor.Red);
                }
            }
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[LootFuckery] Failed to record exit gear: {ex.Message}", LogTextColor.Red);
        }
    }

    // Writes one full-fidelity inventory snapshot: gear/<player>/<timestamp>_<phase>_<session8>.json plus
    // one line in gear/index.jsonl. The whole Item list is serialised untouched (parentId/slotId/upd all
    // intact), so the tree - and thus every container's contents - is reconstructable offline. Minified.
    private void WriteGearSnapshot(MongoId sessionId, string nickname, string phase, string exitStatus, List<Item> items)
    {
        var now = DateTime.UtcNow;
        string capturedUtc = now.ToString("o");
        string session = sessionId.ToString();
        string sessionShort = session.Length >= 8 ? session.Substring(0, 8) : session;
        string safePlayer = Sanitize(nickname);
        string fileName = $"{now:yyyyMMdd_HHmmss}_{phase}_{sessionShort}.json";
        string relativePath = $"{safePlayer}/{fileName}";

        var snapshot = new GearSnapshot(phase, capturedUtc, session, nickname, exitStatus, items.Count, items);

        lock (writeLock)
        {
            string playerDir = System.IO.Path.Combine(gearFolder, safePlayer);
            System.IO.Directory.CreateDirectory(playerDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(playerDir, fileName), jsonUtil.Serialize(snapshot, false) ?? "");

            var indexEntry = new GearIndexEntry(phase, relativePath, capturedUtc, nickname, session, exitStatus, items.Count);
            System.IO.File.AppendAllText(gearIndexFile, (jsonUtil.Serialize(indexEntry, false) ?? "") + "\n", new UTF8Encoding(false));
        }

        Leaves.Utils.Instance.printColor($"[LootFuckery] Gear snapshot ({phase}): {relativePath} ({items.Count} items)");
    }

    // Keeps a map/nickname safe as a single path segment (nicknames can contain anything). Not security -
    // both values are trusted here - just to avoid a stray character breaking the folder/file name.
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('_');
            }
        }
        string result = builder.ToString();
        return result.Length > 0 ? result : "unknown";
    }

    // Explicit camelCase names (same discipline as the dataDump records) so the web UI reads a fixed
    // schema regardless of the serializer's naming policy.
    private sealed record RunFile(
        [property: JsonPropertyName("map")] string Map,
        [property: JsonPropertyName("capturedUtc")] string CapturedUtc,
        [property: JsonPropertyName("session")] string Session,
        [property: JsonPropertyName("player")] string Player,
        [property: JsonPropertyName("lootPoints")] int LootPoints,
        [property: JsonPropertyName("itemCount")] int ItemCount,
        [property: JsonPropertyName("loot")] IEnumerable<SpawnpointTemplate> Loot);

    private sealed record IndexEntry(
        [property: JsonPropertyName("map")] string Map,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("capturedUtc")] string CapturedUtc,
        [property: JsonPropertyName("player")] string Player,
        [property: JsonPropertyName("session")] string Session,
        [property: JsonPropertyName("lootPoints")] int LootPoints,
        [property: JsonPropertyName("itemCount")] int ItemCount);

    // One captured raid's spawn-id set, held in memory for extract-matching. The id-set is built once
    // and never mutated, so it can be read without a lock once obtained.
    private sealed class RecentRun
    {
        public string RelativePath { get; }
        public string Map { get; }
        public HashSet<string> SpawnedIds { get; }

        public RecentRun(string relativePath, string map, HashSet<string> spawnedIds)
        {
            RelativePath = relativePath;
            Map = map;
            SpawnedIds = spawnedIds;
        }
    }

    // One line in extractions.jsonl: what a player carried out of a specific captured raid. runFile ties
    // it back to that raid's run file; broughtOut is only the items whose ids this raid spawned.
    private sealed record ExtractionEntry(
        [property: JsonPropertyName("runFile")] string RunFile,
        [property: JsonPropertyName("map")] string Map,
        [property: JsonPropertyName("player")] string Player,
        [property: JsonPropertyName("session")] string Session,
        [property: JsonPropertyName("exitStatus")] string ExitStatus,
        [property: JsonPropertyName("extractedUtc")] string ExtractedUtc,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("broughtOut")] List<BroughtOutItem> BroughtOut);

    private sealed record BroughtOutItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("tpl")] string Tpl,
        [property: JsonPropertyName("stack")] int Stack);

    // One full inventory snapshot. items is the raw profile Item list (flat tree - each item carries its
    // own parentId/slotId), dumped untouched so containers and their contents survive round-trip.
    private sealed record GearSnapshot(
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("capturedUtc")] string CapturedUtc,
        [property: JsonPropertyName("session")] string Session,
        [property: JsonPropertyName("player")] string Player,
        [property: JsonPropertyName("exitStatus")] string ExitStatus,
        [property: JsonPropertyName("itemCount")] int ItemCount,
        [property: JsonPropertyName("items")] IEnumerable<Item> Items);

    // One line in gear/index.jsonl - enough to list snapshots and pair them by session without opening
    // the (large) per-snapshot files.
    private sealed record GearIndexEntry(
        [property: JsonPropertyName("phase")] string Phase,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("capturedUtc")] string CapturedUtc,
        [property: JsonPropertyName("player")] string Player,
        [property: JsonPropertyName("session")] string Session,
        [property: JsonPropertyName("exitStatus")] string ExitStatus,
        [property: JsonPropertyName("itemCount")] int ItemCount);
}
