using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Services;

namespace Leaves_LootFuckery;

// Postfix on LocationLifecycleService.EndLocalRaid (route /client/match/local/end) - every player's
// client hits this on extract, coop included. Fika's own EndLocalRaidOverride is a per-player PREFIX
// that does NO item-id remapping, so this postfix coexists and reads the submitted request fine (it runs
// even when Fika's prefix skips the original for a spectator - that player just has nothing to match).
//
// Hands the extract to RealRunCapture, which intersects the player's carried item ids against each
// captured raid's spawn ids to work out which raid they were in and what they brought out. See
// RealRunCapture.RecordExtraction. Static patch → reaches the singleton via ServiceLocator, like the
// start patch. Enabled in LootFuckery.OnLoad (only when captureRealRuns is on).
public class EndLocalRaidPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.EndLocalRaid))!;
    }

    // sessionId + request are the original method's own parameters (bound by name). EndLocalRaid is void,
    // so there's no __result - everything we need is in the client-submitted request.
    [PatchPostfix]
    public static void Postfix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        // Whole body guarded: a throwing postfix on the live raid-END path could propagate into the route
        // handler, so nothing here may escape - not the service lookup, not either consumer. Both record
        // methods are fully wrapped internally too; this is the outer backstop.
        try
        {
#pragma warning disable CS0618
            var capture = ServiceLocator.ServiceProvider.GetService<RealRunCapture>();
#pragma warning restore CS0618
            if (capture == null)
            {
                return;
            }
            // Two independent consumers, each self-gated on its own config flag: the loot capture (which
            // raid did they extract from, what did they bring out) and the forensic gear snapshot (their
            // full inventory at exit). The patch is enabled whenever EITHER is on; see LootFuckery.OnLoad.
            capture.RecordExtraction(sessionId, request);
            capture.RecordExit(sessionId, request);
        }
        catch
        {
            // Swallowed by design - capture must never break a real raid's extract. The record methods
            // log their own failures.
        }
    }
}
