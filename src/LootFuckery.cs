using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Leaves_LootFuckery;

// InjectionType.Singleton is REQUIRED now that this implements BOTH IOnLoad and IOnUpdate - without
// it SPT constructs the class twice (once per interface collection), so OnLoad would set up state on
// one instance and OnUpdate would run on a different, un-set-up one. Same pitfall as BarterEconomy.
//
// TypePriority is deliberately ABOVE L.E.W.D.S.'s (+90000) so this mod's OnLoad - and therefore its
// AddTransformer registration - happens LAST. LazyLoad<LooseLoot>.Value applies transformers in
// registration order, so registering last makes our probability edits the FINAL pass, on top of
// L.E.W.D.S.'s spawn-probability floors. That's what lets loot.jsonc reduce an item (e.g. a keycard)
// BELOW a L.E.W.D.S. floor: if we ran first, that floor would clamp our reduction straight back up
// (which is exactly the keycard bug this fixes). +95000 stays inside the PostDBModLoader stage
// (next stage starts at +100000).
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 95000)]
public class LootFuckery(
    DatabaseService databaseService,
    ModHelper modHelper,
    JsonUtil jsonUtil,
    LocationLifecycleService locationLifecycleService,
    HandbookHelper handbookHelper,
    RealRunCapture realRunCapture,
    ICloner cloner)
    : IOnLoad, IOnUpdate
{
    private LootFuckeryConfig config = null!;
    private LootConfig lootConfig = null!;
    private string outputFolder = null!;
    private string modPath = null!;
    private bool dataDumpDone;

    public Task OnLoad()
    {
        modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        config = modHelper.GetJsonDataFromFile<LootFuckeryConfig>(modPath, "config/config.jsonc");
        lootConfig = modHelper.GetJsonDataFromFile<LootConfig>(modPath, "config/loot.jsonc");
        outputFolder = System.IO.Path.Combine(modPath, "output");

        Leaves.Utils.Instance.printColor("[LootFuckery] LootFuckery Starting:");

        // EditProbabilities registers lazy LooseLoot transformers (AddTransformer), which run at
        // raid-access time - long after every mod has loaded - so registering them here in OnLoad is
        // fine regardless of load order.
        if (config.EditProbabilities)
        {
            EditProbabilities();
        }

        // Raid capture: two independent features sharing the RealRunCapture store and the raid-lifecycle
        // patches. captureRealRuns = loot (GenerateLocationAndLoot + the loot half of EndLocalRaid);
        // captureGearSnapshots = the player's full inventory at raid start + end (StartLocalRaid + the
        // gear half of EndLocalRaid). Init the store if EITHER is on, then enable only the patches each
        // needs (EndLocalRaid is shared, so it's enabled for either). Enabling here rather than at
        // PreSptModLoader is fine - none of these methods run until a raid starts, long after mods load.
        if (config.CaptureRealRuns || config.CaptureGearSnapshots)
        {
            realRunCapture.Init(config, modPath);

            if (config.CaptureRealRuns)
            {
                new GenerateLocationAndLootPatch().Enable();
            }
            if (config.CaptureGearSnapshots)
            {
                new StartLocalRaidPatch().Enable();
            }
            // EndLocalRaid feeds both the loot extraction record and the exit gear snapshots.
            new EndLocalRaidPatch().Enable();

            Leaves.Utils.Instance.printColor($"[LootFuckery] Raid capture enabled - loot:{config.CaptureRealRuns} gear:{config.CaptureGearSnapshots}", LogTextColor.Cyan);
        }

        // Location-data caching is DEFERRED to the first OnUpdate (see OnUpdate + LocationDataCache): the
        // LazyLoad swap must happen only once every mod's OnLoad - and therefore every transformer - has run.

        // The DataDump (simulated runs) is DEFERRED to the first OnUpdate - see OnUpdate for why.
        return Task.CompletedTask;
    }

    // Runs the DataDump exactly once, on the first OnUpdate. OnUpdate only fires after the server has
    // fully started, i.e. after EVERY mod's OnLoad at every priority (including PostSptModLoader and
    // other mods' own first-OnUpdate work). That makes this the latest safe point to simulate raids
    // against the FINAL loot table - so injections from content mods like WTT are included in the
    // dump. (Doing it in OnLoad at PostDBModLoader+1 would have run before those injections.)
    public Task<bool> OnUpdate(long secondsSinceLastRun)
    {
        // Install the location-data cache now: the first OnUpdate means the server is fully started, so every
        // mod's OnLoad has run at every priority and every LazyLoad transformer is registered. Only now is it
        // safe to snapshot the deserialized+transformed loot. Done before the dump below so the dump's many
        // generations hit the cache too. Independent of DataDump, so it must run before the early-return.
        if (config.CacheLocationData)
        {
            LocationDataCache.Install(cloner);
        }

        if (dataDumpDone || !config.DataDump)
        {
            return Task.FromResult(false);
        }
        dataDumpDone = true;

        if (config.RunAllMaps)
        {
            foreach (var mapName in Leaves.Utils.Instance.getLocationNames())
            {
                GenerateMap(mapName);
            }
        }
        else
        {
            GenerateMap(config.MapToGenerate);
        }

        return Task.FromResult(true);
    }

    private void EditProbabilities()
    {
        Leaves.Utils.Instance.printColor("[LootFuckery] Editing Loot Probabilities\n-----------------------", LogTextColor.Cyan);

        foreach (var mapCodeName in Leaves.Utils.Instance.getLocationNames())
        {
            if (!lootConfig.MapSpecific.ContainsKey(mapCodeName))
            {
                continue;
            }

            var location = Leaves.Utils.Instance.findLocationByCodeName(mapCodeName);
            if (location?.LooseLoot == null)
            {
                continue;
            }

            Leaves.Utils.Instance.printColor($"[LootFuckery] Adjusting: {mapCodeName}", LogTextColor.Yellow);

            // LazyLoad<T>.Value re-deserializes from disk on every access - there is no built-in caching.
            // AddTransformer is SPT's own mechanism for making an edit persist across every future access
            // (this is the same approach SPT's built-in SeasonalEventService uses for its Christmas loot boost).
            LooseLoot? Transform(LooseLoot? looseLoot)
            {
                return ApplyLootAdjustments(looseLoot, mapCodeName);
            }

            location.LooseLoot.AddTransformer(Transform);
        }
    }

    private LooseLoot? ApplyLootAdjustments(LooseLoot? looseLoot, string mapCodeName)
    {
        if (looseLoot == null)
        {
            return looseLoot;
        }

        var mapConfig = lootConfig.MapSpecific[mapCodeName];

        if (looseLoot.SpawnpointCount != null)
        {
            looseLoot.SpawnpointCount.Mean *= mapConfig.TotalLootMultiplier;
        }

        if (looseLoot.Spawnpoints != null)
        {
            foreach (var point in looseLoot.Spawnpoints)
            {
                AdjustPoint(point, mapCodeName);
            }
        }

        return looseLoot;
    }

    private void AdjustPoint(Spawnpoint point, string mapCodeName)
    {
        var items = point.Template?.Items;
        if (items == null)
        {
            return;
        }

        double runningMulti = 0;
        int countedItems = 0;

        foreach (var item in items)
        {
            // If an item has a parent, it's not the root node of the entry, so skip it.
            if (!string.IsNullOrEmpty(item.ParentId))
            {
                continue;
            }

            var distribution = FindDistribution(point, item.ComposedKey);
            if (distribution?.RelativeProbability == null)
            {
                if (config.HarmlessErrorLogging)
                {
                    Leaves.Utils.Instance.printColor($"[LootFuckery] Found point item that doesn't exist, or doesn't have a relativeProbability entry, {item.Template} this is usually due to a mod doing something wrong. But it shouldn't be breaking anything.", LogTextColor.Red);
                }
                continue;
            }

            double multi = GetProbability(item.Template, mapCodeName);
            runningMulti += multi;
            countedItems++;

            distribution.RelativeProbability = Math.Ceiling(distribution.RelativeProbability.Value * 100 * multi);
        }

        // Nothing usable on this point - leave its probability untouched instead of dividing by zero.
        if (countedItems == 0)
        {
            return;
        }

        // Average multiplier, capped at 0.99 - at 1 the point would be force-spawned and stop counting toward max loot spawns.
        double avgMulti = runningMulti / countedItems;
        point.Probability = (point.Probability ?? 0) * avgMulti;

        if (point.Probability >= 1)
        {
            point.Probability = 0.99;
        }
    }

    private LooseLootItemDistribution? FindDistribution(Spawnpoint point, string? composedKey)
    {
        if (point.ItemDistribution == null || composedKey == null)
        {
            return null;
        }

        foreach (var distribution in point.ItemDistribution)
        {
            if (distribution.ComposedKey?.Key == composedKey)
            {
                return distribution;
            }
        }

        return null;
    }

    private double GetProbability(MongoId itemTpl, string mapCodeName)
    {
        var itemDB = databaseService.GetItems();

        if (!itemDB.TryGetValue(itemTpl, out var itemTemplate) || itemTemplate.Parent == "")
        {
            if (config.HarmlessErrorLogging)
            {
                Leaves.Utils.Instance.printColor($"[LootFuckery] Found item that doesn't exist, or without parent: {itemTpl} this is usually due to a mod doing something wrong. But it shouldn't be breaking anything.", LogTextColor.Red);
            }
            return 0;
        }

        MongoId parent = itemTemplate.Parent;
        lootConfig.MapSpecific.TryGetValue(mapCodeName, out var mapConfig);

        // Priority: map-specific item, then global item, then map-specific category, then global category.
        if (mapConfig != null && mapConfig.Items.TryGetValue(itemTpl.ToString(), out var mapItem))
        {
            return mapItem.Multi;
        }

        if (lootConfig.GlobalItems.Items.TryGetValue(itemTpl.ToString(), out var globalItem))
        {
            return globalItem.Multi;
        }

        if (mapConfig != null && mapConfig.Categories.TryGetValue(parent.ToString(), out var mapCategory))
        {
            return mapCategory.Multi;
        }

        if (lootConfig.GlobalCategories.TryGetValue(parent.ToString(), out var globalCategory))
        {
            return globalCategory.Multi;
        }

        if (config.HarmlessErrorLogging)
        {
            string parentName = itemDB.TryGetValue(parent, out var parentTemplate) ? parentTemplate.Name ?? "" : "";
            Leaves.Utils.Instance.printColor($"[LootFuckery] Found item without matching parent: {itemTpl} - Parent: {parentName} - ID: {parent}", LogTextColor.Red);
        }
        return 0;
    }

    private void GenerateMap(string mapToGenerate)
    {
        Leaves.Utils.Instance.printColor("[LootFuckery] Generating Run(s)", LogTextColor.Yellow);

        // Our simulated runs call the SAME method the real-run capture postfix is patched onto. Flag the
        // loop so those synthetic generations are never recorded as real raids (the real-profile gate in
        // RealRunCapture would exclude them anyway - this is the belt to that gate's suspenders).
        RunResults runs;
        realRunCapture.SuppressCapture = true;
        try
        {
            runs = GenerateRuns(mapToGenerate);
        }
        finally
        {
            realRunCapture.SuppressCapture = false;
        }

        // Containers, richest first, each carrying the items found INSIDE that container type.
        var containers = new List<DumpContainer>();
        foreach (var (containerTpl, totals) in runs.Containers.OrderByDescending(c => c.Value.Total))
        {
            runs.ContainerContents.TryGetValue(containerTpl, out var inside);
            containers.Add(new DumpContainer(
                containerTpl,
                ResolveItemName(containerTpl),
                Round(totals.Total / config.TimesToGenerate),
                (int)totals.Max,
                GroupCategories(BuildCategories(inside ?? new Dictionary<string, TotalMax>()))));
        }

        var dump = new LootDump(
            mapToGenerate,
            DateTime.UtcNow.ToString("o"),
            config.TimesToGenerate,
            GroupCategories(BuildCategories(runs.LooseLoot)),
            containers);

        WriteResult($"loot_{mapToGenerate}_-", dump);
    }

    // ============================================================
    // Dump model - the shape written to disk
    // ============================================================
    //
    // Deliberately plain records so this serializes to real, machine-readable JSON: ids AND resolved
    // names on every node (a viewer can show one and key off the other), and numbers written by the
    // serializer, which means invariant decimal points. The old hand-rolled text format wrote
    // culture-formatted numbers ("Avg:21,6"), so every consumer had to repair them before parsing.

    // Explicit camelCase names: this file is meant to be read by a web viewer, and pinning them here
    // means the schema doesn't shift if the serializer's naming policy ever changes.
    private sealed record DumpItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("avg")] double Avg,
        [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("unitPrice")] double UnitPrice,
        [property: JsonPropertyName("value")] double Value);

    private sealed record DumpCategory(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("items")] List<DumpItem> Items);

    // A handful of broad sections (Weapon / Weapon mod / Barter item / Equipment / ...) sitting above
    // the ~74 fine-grained categories, so a viewer isn't a wall of top-level headings.
    private sealed record DumpGroup(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("categories")] List<DumpCategory> Categories);

    // Avg/Max are of the CONTAINER itself (how many spawn per raid); Contents is what was found
    // inside it, grouped by item category.
    private sealed record DumpContainer(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("avg")] double Avg,
        [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("contents")] List<DumpGroup> Contents);

    private sealed record LootDump(
        [property: JsonPropertyName("map")] string Map,
        [property: JsonPropertyName("generatedUtc")] string GeneratedUtc,
        [property: JsonPropertyName("runs")] int Runs,
        [property: JsonPropertyName("looseLoot")] List<DumpGroup> LooseLoot,
        [property: JsonPropertyName("containers")] List<DumpContainer> Containers);

    private static double Round(double value) => Math.Round(value, 4);

    // Group a flat item table by the items' parent category. When config.Categorize is off everything
    // lands in one "All" bucket, so the JSON shape stays identical either way - a consumer never has
    // to handle two schemas.
    private List<DumpCategory> BuildCategories(Dictionary<string, TotalMax> table)
    {
        var itemDB = databaseService.GetItems();
        var grouped = new Dictionary<string, List<DumpItem>>();

        foreach (var (itemId, totals) in table)
        {
            string categoryId = "All";
            if (config.Categorize)
            {
                if (!itemDB.TryGetValue(itemId, out var template) || template.Parent == "")
                {
                    continue; // unknown item or no parent - nothing sensible to group it under
                }
                categoryId = template.Parent.ToString();
            }

            double avg = totals.Total / config.TimesToGenerate;
            if (!grouped.TryGetValue(categoryId, out var bucket))
            {
                bucket = new List<DumpItem>();
                grouped[categoryId] = bucket;
            }
            bucket.Add(new DumpItem(
                itemId, ResolveItemName(itemId), Round(avg), (int)totals.Max,
                Round(totals.Price), Round(avg * totals.Price)));
        }

        var result = new List<DumpCategory>();
        foreach (var (categoryId, items) in grouped)
        {
            items.Sort((a, b) => b.Avg.CompareTo(a.Avg));
            result.Add(new DumpCategory(
                categoryId,
                categoryId == "All" ? "All" : ResolveCategoryName(categoryId),
                Round(items.Sum(i => i.Value)),
                items));
        }
        // Most valuable category first - that's what you're hunting when checking for over-tuned loot.
        result.Sort((a, b) => b.Value.CompareTo(a.Value));
        return result;
    }

    private string ResolveItemName(string itemId)
    {
        if (!config.ConvertToName)
        {
            return itemId;
        }
        var itemDB = databaseService.GetItems();
        string? fallback = itemDB.TryGetValue(itemId, out var template) ? template.Name : null;
        return (config.LocalizeNames ? Leaves.Utils.Instance.getLocale(config.Localization, itemId, " Name") : null)
               ?? fallback ?? itemId;
    }

    private string ResolveCategoryName(string categoryId)
    {
        var itemDB = databaseService.GetItems();
        return itemDB.TryGetValue(categoryId, out var template) ? (template.Name ?? categoryId) : categoryId;
    }

    // EFT's item tree root, plus the structural nodes that carry no meaning for a human reading a
    // loot report. Skipping them is what turns "Pistol" into "Weapon" and "Backpack" into "Equipment"
    // instead of stopping on "Compound item" / "Searchable item".
    private static readonly MongoId ItemRoot = "54009119af1c881c07000029";
    private static readonly HashSet<string> StructuralNodes = new()
    {
        "566162e44bdc2d3f298b4573", // Compound item
        "5661632d4bdc2d903d8b456b"  // Stackable item
    };

    // The one place EFT's tree disagrees with how a player thinks about loot: Backpack and Vest hang off
    // SearchableItem, which is a sibling of Equipment rather than a child of it - so no amount of walking
    // parents will land them under Equipment. Remapped explicitly instead of skipped, because skipping
    // SearchableItem would just make Backpack and Vest two more top-level sections.
    private static readonly Dictionary<string, string> GroupOverrides = new()
    {
        ["566168634bdc2d144c8b456c"] = "543be5f84bdc2dd4348b456a" // SearchableItem -> Equipment
    };

    // The broad section a category belongs to: walk its ancestry to the root, then take the highest
    // node that is neither the root nor structural. Derived from the item tree rather than a hardcoded
    // list, so modded items land in the right section automatically.
    private string ResolveGroupId(string categoryId)
    {
        var itemDB = databaseService.GetItems();
        var chain = new List<string>();
        string? current = categoryId;
        while (current != null && itemDB.TryGetValue(current, out var template))
        {
            chain.Add(current);
            string parent = template.Parent.ToString();
            current = string.IsNullOrEmpty(parent) ? null : parent;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i] == ItemRoot.ToString() || StructuralNodes.Contains(chain[i]))
            {
                continue;
            }
            return GroupOverrides.TryGetValue(chain[i], out var mapped) ? mapped : chain[i];
        }
        return categoryId; // nothing above it - it IS its own section
    }

    // Fold the flat category list into groups, keeping both levels sorted by value.
    private List<DumpGroup> GroupCategories(List<DumpCategory> categories)
    {
        var byGroup = new Dictionary<string, List<DumpCategory>>();
        foreach (var category in categories)
        {
            string groupId = config.Categorize ? ResolveGroupId(category.Id) : "All";
            if (!byGroup.TryGetValue(groupId, out var bucket))
            {
                bucket = new List<DumpCategory>();
                byGroup[groupId] = bucket;
            }
            bucket.Add(category);
        }

        var groups = new List<DumpGroup>();
        foreach (var (groupId, cats) in byGroup)
        {
            cats.Sort((a, b) => b.Value.CompareTo(a.Value));
            groups.Add(new DumpGroup(
                groupId,
                groupId == "All" ? "All" : ResolveCategoryName(groupId),
                Round(cats.Sum(c => c.Value)),
                cats));
        }
        groups.Sort((a, b) => b.Value.CompareTo(a.Value));
        return groups;
    }

    // Loose loot (what this mod actually edits) plus the two container tables, which are recorded
    // for information only - we never touch static container loot.
    // ContainerContents is keyed by CONTAINER template id -> the items found inside that container
    // type, so the dump can group contents per container ("what's actually in a Drawer").
    private sealed record RunResults(
        Dictionary<string, TotalMax> LooseLoot,
        Dictionary<string, TotalMax> Containers,
        Dictionary<string, Dictionary<string, TotalMax>> ContainerContents);

    private static void Bump(Dictionary<string, int> table, string key)
    {
        table[key] = table.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private RunResults GenerateRuns(string mapToGenerate)
    {
        var lootTable = new Dictionary<string, TotalMax>();
        var containerTable = new Dictionary<string, TotalMax>();
        var contentsTable = new Dictionary<string, Dictionary<string, TotalMax>>();
        var pointProbability = new Dictionary<string, int>();

        // No real player session exists at server startup - GenerateLocationAndLoot only uses the
        // session id to look up in-progress raid adjustments for that profile, which gracefully
        // returns an empty result for an unknown id (confirmed via ProfileActivityService), and the
        // actual loot generation itself doesn't touch the session at all.
        var fakeSessionId = new MongoId();

        // Timing so we can see where the cost actually is: total wall time, the FIRST generation (which
        // pays the one-time file parse when the cache is on), and the average.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long firstGenerationMs = 0;

        for (int i = 0; i < config.TimesToGenerate; i++)
        {
            var currentRun = new Dictionary<string, int>();
            var containerRun = new Dictionary<string, int>();
            var contentsRun = new Dictionary<string, Dictionary<string, int>>();
            long generationStartMs = timer.ElapsedMilliseconds;
            var generatedLoot = locationLifecycleService.GenerateLocationAndLoot(fakeSessionId, mapToGenerate);
            if (i == 0)
            {
                firstGenerationMs = timer.ElapsedMilliseconds - generationStartMs;
            }

            if (generatedLoot.Loot != null)
            {
                foreach (var entry in generatedLoot.Loot)
                {
                    string pointId = entry.Id ?? "";
                    if (pointProbability.ContainsKey(pointId))
                    {
                        pointProbability[pointId] += 1;
                    }
                    else
                    {
                        pointProbability[pointId] = 1;
                    }
                }

                Leaves.Utils.Instance.printColor($"[LootFuckery] \tCurrent Generation Count: {i + 1}", LogTextColor.Magenta);

                foreach (var point in generatedLoot.Loot)
                {
                    var firstItem = point.Items?.FirstOrDefault();
                    if (firstItem == null)
                    {
                        continue;
                    }

                    if (point.IsContainer == true)
                    {
                        // A container point is the container ITSELF (the root item) plus whatever
                        // spawned inside it. We don't edit container loot, but it's in the generated
                        // data, so record it: the container type on one table, its contents on
                        // another. DIRECT children only (ParentId == the container) - going deeper
                        // would count every attachment screwed onto a rifle in a weapon box.
                        string containerTpl = firstItem.Template.ToString();
                        Bump(containerRun, containerTpl);
                        if (!contentsRun.TryGetValue(containerTpl, out var inside))
                        {
                            inside = new Dictionary<string, int>();
                            contentsRun[containerTpl] = inside;
                        }
                        foreach (var inner in point.Items!)
                        {
                            if (inner.ParentId == firstItem.Id.ToString())
                            {
                                Bump(inside, inner.Template.ToString());
                            }
                        }
                        continue;
                    }

                    Bump(currentRun, firstItem.Template.ToString());
                }
            }

            AddTableValues(lootTable, currentRun);
            AddTableValues(containerTable, containerRun);
            foreach (var (containerTpl, itemsInside) in contentsRun)
            {
                if (!contentsTable.TryGetValue(containerTpl, out var perContainer))
                {
                    perContainer = new Dictionary<string, TotalMax>();
                    contentsTable[containerTpl] = perContainer;
                }
                AddTableValues(perContainer, itemsInside);
            }
        }

        timer.Stop();
        // Steady-state avg excludes the first generation: that one pays the one-time file parse (~seconds on
        // a big map), so folding it into the average makes per-run generation look far worse than it is.
        long steadyAverageMs = config.TimesToGenerate > 1
            ? (timer.ElapsedMilliseconds - firstGenerationMs) / (config.TimesToGenerate - 1)
            : timer.ElapsedMilliseconds;
        Leaves.Utils.Instance.printColor($"[LootFuckery] {mapToGenerate}: {config.TimesToGenerate} generations in {timer.ElapsedMilliseconds} ms (first: {firstGenerationMs} ms one-time parse, steady-state avg: {steadyAverageMs} ms)", LogTextColor.Green);

        SaveSpawnpointProbabilities(mapToGenerate, pointProbability);

        return new RunResults(lootTable, containerTable, contentsTable);
    }

    private void SaveSpawnpointProbabilities(string mapToGenerate, Dictionary<string, int> pointProbability)
    {
        var looseLoot = Leaves.Utils.Instance.findLocationByCodeName(mapToGenerate)?.LooseLoot?.Value;
        if (looseLoot?.Spawnpoints == null)
        {
            return;
        }

        var data = new Dictionary<string, SpawnpointProbabilityEntry>();
        foreach (var spawnPoint in looseLoot.Spawnpoints)
        {
            string pointId = spawnPoint.Template?.Id ?? "";
            if (pointProbability.TryGetValue(pointId, out var occurrences))
            {
                data[pointId] = new SpawnpointProbabilityEntry
                {
                    Occ = (double)occurrences / config.TimesToGenerate,
                    Prob = spawnPoint.Probability ?? 0
                };
            }
        }

        Leaves.Utils.Instance.saveJsonFile(data, modPath, $"{mapToGenerate}_probability.json");
    }

    private void AddTableValues(Dictionary<string, TotalMax> target, Dictionary<string, int> current)
    {
        foreach (var (key, value) in current)
        {
            if (!target.TryGetValue(key, out var existing))
            {
                target[key] = new TotalMax { Total = value, Max = value, Price = handbookHelper.GetTemplatePrice(new MongoId(key)) };
            }
            else
            {
                existing.Total += value;
                if (existing.Max < value)
                {
                    existing.Max = value;
                }
            }
        }
    }






    // extraSections is appended verbatim after the main body (the container tables) - always as text,
    // even in non-compact mode, since it's a human-readable appendix rather than part of the data.
    // Always real JSON now. convertToCompact chooses MINIFIED vs indented - it used to pick between
    // JSON and a hand-rolled text format, which wrote culture-formatted numbers ("21,6") that no JSON
    // parser would accept. The serializer always emits invariant decimal points.
    private void WriteResult<T>(string prefix, T data)
    {
        string text = jsonUtil.Serialize(data, !config.ConvertToCompact) ?? "";
        string fullPath = Leaves.Utils.Instance.writeTimestampedResult(outputFolder, prefix, text);
        Leaves.Utils.Instance.printColor($"[LootFuckery] Written results to: {fullPath}");
    }




}
