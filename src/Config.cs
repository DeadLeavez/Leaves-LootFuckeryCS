using System.Text.Json.Serialization;

namespace Leaves_LootFuckery;

public class TotalMax
{
    public double Total { get; set; }
    public double Max { get; set; }

    // Handbook unit price of this item, captured when the entry is first created (we still have the
    // item id then). The dump multiplies it by the average count to show total expected value per
    // raid - handy for spotting over-tuned loot at a glance.
    public double Price { get; set; }
}

public class SpawnpointProbabilityEntry
{
    public double Occ { get; set; }
    public double Prob { get; set; }
}

// Mirrors config/config.jsonc.
public record LootFuckeryConfig
{
    [JsonPropertyName("editProbabilities")]
    public required bool EditProbabilities { get; set; }

    // Cache each location's LazyLoad loot data (LooseLoot / StaticContainers / StaticLoot) after the server
    // is fully started, so those files are parsed off disk ONCE instead of on every raid's loot generation
    // (and every data-dump iteration). Big speed-up for the dump and real raids; costs memory (the parsed
    // loot trees stay resident, like the old TS server). See LazyLoadCachePatch.
    [JsonPropertyName("cacheLocationData")]
    public required bool CacheLocationData { get; set; }

    [JsonPropertyName("dataDump")]
    public required bool DataDump { get; set; }

    // Capture EVERY real raid's generated loot to output/history/<map>/ (full fidelity, kept forever)
    // via a postfix on LocationLifecycleService.GenerateLocationAndLoot. Off leaves the patch disabled.
    [JsonPropertyName("captureRealRuns")]
    public required bool CaptureRealRuns { get; set; }

    // Forensic gear logging: on every raid START and END, dump the player's FULL profile inventory tree
    // (equipment + every container's contents, raw) to output/history/gear/. Three snapshots per raid -
    // entry (server profile), exit-submitted (client's submitted profile), exit-server (server profile
    // after SPT applied results) - so a vanished item can be pinned to where it disappeared. Independent
    // of captureRealRuns (that one is loot; this one is the player's kit). Off leaves both patches disabled.
    [JsonPropertyName("captureGearSnapshots")]
    public required bool CaptureGearSnapshots { get; set; }

    [JsonPropertyName("categorize")]
    public required bool Categorize { get; set; }

    [JsonPropertyName("convertToCompact")]
    public required bool ConvertToCompact { get; set; }

    [JsonPropertyName("runAllMaps")]
    public required bool RunAllMaps { get; set; }

    [JsonPropertyName("mapToGenerate")]
    public required string MapToGenerate { get; set; }

    [JsonPropertyName("timesToGenerate")]
    public required int TimesToGenerate { get; set; }

    [JsonPropertyName("convertToName")]
    public required bool ConvertToName { get; set; }

    [JsonPropertyName("localizeNames")]
    public required bool LocalizeNames { get; set; }

    [JsonPropertyName("localization")]
    public required string Localization { get; set; }

    [JsonPropertyName("HarmlessErrorLogging")]
    public required bool HarmlessErrorLogging { get; set; }
}

// Mirrors config/loot.jsonc. Priority top-to-bottom: map-specific item, global item, map-specific category, global category.
public record LootConfig
{
    [JsonPropertyName("globalCategories")]
    public required Dictionary<string, MultiEntry> GlobalCategories { get; set; }

    [JsonPropertyName("globalItems")]
    public required GlobalItemsConfig GlobalItems { get; set; }

    [JsonPropertyName("mapSpecific")]
    public required Dictionary<string, MapSpecificConfig> MapSpecific { get; set; }
}

public record GlobalItemsConfig
{
    [JsonPropertyName("items")]
    public required Dictionary<string, MultiEntry> Items { get; set; }
}

public record MapSpecificConfig
{
    [JsonPropertyName("totalLootMultiplier")]
    public required double TotalLootMultiplier { get; set; }

    [JsonPropertyName("categories")]
    public required Dictionary<string, MultiEntry> Categories { get; set; }

    [JsonPropertyName("items")]
    public required Dictionary<string, MultiEntry> Items { get; set; }
}

public record MultiEntry
{
    [JsonPropertyName("multi")]
    public required double Multi { get; set; }
}
