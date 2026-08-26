using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;

namespace Leaves_LootFuckery;

// Caches each location's LazyLoad loot data so its file is parsed off disk ONCE instead of on every access.
//
// The problem: LazyLoad<T>.Value (SPTarkov.Server.Core.Utils.Json.LazyLoad) has no cache - its getter
// re-runs the deserialize closure (read + JSON-parse a file off disk) on EVERY access, then re-applies its
// transformers. Location loot reads three of these per generation: LooseLoot (once), StaticContainers
// (THREE times) and StaticLoot (once). Streets' looseLoot.json alone is ~97 MB, and the data dump runs a
// generation timesToGenerate (hundreds) of times, so that one file gets parsed tens of gigabytes' worth.
// The old TS server kept the parsed loot resident in memory; this restores that.
//
// Why a SWAP and not a Harmony patch on the getter: LazyLoad<T> is generic, and for reference-type T the
// CLR shares one JIT'd method body across all instantiations. Patching a specific closed-generic getter is
// unreliable (it commonly no-ops). Replacing the LazyLoad object the loot generator reads cannot miss -
// GenerateLocationLoot reads location.LooseLoot.Value, so whatever object lives on that property is used.
//
// How the swap works: for each LazyLoad we build a NEW one whose deserialize closure, on first call, pulls
// the value from the ORIGINAL LazyLoad (which parses the file once AND applies every transformer registered
// on it - our probability edits, the LEWDS floor, etc.), stores it as a "master", and thereafter returns a
// fresh COPY of that master. A copy is required because loot generation is DESTRUCTIVE - it overwrites each
// chosen spawn point's item pool with the single item it rolled (LocationLootGenerator.cs:844 etc), so the
// master must never be handed out directly or run 2 sees run 1's leftovers.
//
// The copy is the whole point of the perf work here. A FastCloner deep clone of LooseLoot is ~2 s on
// Streets because the in-memory graph is millions of tiny objects (every spawn point, template, item,
// distribution entry, position, boxed nullable), not a flat 97 MB blob - so deep-cloning means millions of
// allocations + string interning on the Root/Key/Name setters. Generation only ever mutates a sliver of
// that graph, so LooseLoot uses a STRUCTURAL copy (CloneLooseLootForGeneration) that duplicates only the
// wrapper objects generation reassigns and shares all the read-only bulk by reference. Static containers /
// static loot are small and read-only during generation, so they keep the simple deep clone.
//
// Install() must run only once the server is fully started (first OnUpdate), so every mod's OnLoad - and
// therefore every transformer - has already been registered on the original LazyLoads before we snapshot.
public static class LocationDataCache
{
    private static bool installed;

    public static void Install(ICloner cloner)
    {
        if (installed)
        {
            return;
        }
        installed = true;

        int swapped = 0;
        foreach (var mapCodeName in Leaves.Utils.Instance.getLocationNames())
        {
            var location = Leaves.Utils.Instance.findLocationByCodeName(mapCodeName);
            if (location == null)
            {
                continue;
            }

            if (location.LooseLoot != null)
            {
                location.LooseLoot = MakeCachedLooseLoot(location.LooseLoot);
                swapped++;
            }
            if (location.StaticContainers != null)
            {
                location.StaticContainers = MakeCachedDeep(location.StaticContainers, cloner);
                swapped++;
            }
            if (location.StaticLoot != null)
            {
                location.StaticLoot = MakeCachedDeep(location.StaticLoot, cloner);
                swapped++;
            }
        }

        Leaves.Utils.Instance.printColor($"[LootFuckery] Location-data cache installed on {swapped} LazyLoad(s) - files now parse once, not per access", LogTextColor.Cyan);
    }

    // Loose loot: parse+transform once, then hand out a cheap structural copy per access (see below).
    private static LazyLoad<LooseLoot> MakeCachedLooseLoot(LazyLoad<LooseLoot> original)
    {
        LooseLoot? master = null;
        bool loaded = false;
        object gate = new object();

        return new LazyLoad<LooseLoot>(() =>
        {
            if (!loaded)
            {
                lock (gate)
                {
                    if (!loaded)
                    {
                        // Reads the original ONCE: parses the file from disk and applies its transformers.
                        master = original.Value;
                        loaded = true;
                    }
                }
            }

            return CloneLooseLootForGeneration(master!);
        });
    }

    // Static containers / static loot: small, and read-only during generation, so a plain deep clone is
    // fine (still parses the file only once). Kept generic since both types go through here.
    private static LazyLoad<T> MakeCachedDeep<T>(LazyLoad<T> original, ICloner cloner)
    {
        T? master = default;
        bool loaded = false;
        object gate = new object();

        return new LazyLoad<T>(() =>
        {
            if (!loaded)
            {
                lock (gate)
                {
                    if (!loaded)
                    {
                        master = original.Value;
                        loaded = true;
                    }
                }
            }

            return cloner.Clone(master)!;
        });
    }

    // Structural copy of a LooseLoot: duplicate ONLY the objects loot generation reassigns, share the rest.
    //
    // Generation's writes (LocationLootGenerator.GenerateDynamicLoot / GetForcedDynamicLoot):
    //   - LooseLoot.Spawnpoints / .SpawnpointsForced          -> reassigned (christmas filter)  => new LooseLoot
    //   - chosen Spawnpoint.Template.Items                     -> reassigned (filter, then the rolled item)
    //   - chosen Spawnpoint.Template.Root                      -> set to the rolled item's id
    // The Template's Items/Root are reassigned, so each point needs its OWN Spawnpoint + SpawnpointTemplate
    // wrapper. Everything else - SpawnpointCount, ItemDistribution, the Items list + SptLootItem objects,
    // positions - is only READ, so it is shared by reference (that's what keeps this cheap). `with` on a
    // record does exactly this member-wise shallow copy, and copies the Root backing field directly so it
    // does NOT re-run the interning setter.
    private static LooseLoot CloneLooseLootForGeneration(LooseLoot master)
    {
        return master with
        {
            Spawnpoints = CopyPointWrappers(master.Spawnpoints),
            SpawnpointsForced = CopyPointWrappers(master.SpawnpointsForced)
        };
    }

    private static List<Spawnpoint>? CopyPointWrappers(IEnumerable<Spawnpoint>? points)
    {
        if (points == null)
        {
            return null;
        }

        var copies = new List<Spawnpoint>();
        foreach (var point in points)
        {
            var pointCopy = point with { };
            if (point.Template != null)
            {
                // Fresh template wrapper so reassigning its Items/Root can't reach the shared master.
                pointCopy.Template = point.Template with { };
            }
            copies.Add(pointCopy);
        }
        return copies;
    }
}
