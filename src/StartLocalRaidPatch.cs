using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Services;

namespace Leaves_LootFuckery;

// Postfix on LocationLifecycleService.StartLocalRaid (route /client/match/local/start) - the entry twin
// of EndLocalRaidPatch. EMPIRICALLY CONFIRMED on a headless Fika raid: every REAL player's client hits
// this route when they deploy (the headless host uses /fika/raid/headless/start + /fika/raid/create
// instead and never touches it - but a headless carries no real gear, so that's exactly who we DON'T
// want a snapshot from). All coop play here is headless, so every real player is a joiner and every
// joiner provably fires this. See the mod HANDOFF for the captured request log.
//
// We take ONLY sessionId (the original's own first parameter, bound by name) - not the request. The
// player's entry loadout isn't in the request; it's the server's stored PMC profile, which the menu's
// item-moving calls have already synced by the time this fires. RecordEntry reads it via GetPmcProfile.
// Static patch → reaches the singleton through ServiceLocator, same as the start/end loot patches.
// Enabled in LootFuckery.OnLoad only when captureGearSnapshots is on.
public class StartLocalRaidPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.StartLocalRaid))!;
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId)
    {
        // Whole body guarded: this is a postfix on the live raid-start path, and a throwing postfix can
        // propagate into the route handler. Nothing here (service lookup included) may escape - capture is
        // best-effort observation, never a reason a real raid could fail to start. RecordEntry is itself
        // fully wrapped too; this is the outer belt to that inner one.
        try
        {
#pragma warning disable CS0618
            var capture = ServiceLocator.ServiceProvider.GetService<RealRunCapture>();
#pragma warning restore CS0618
            if (capture == null)
            {
                return;
            }
            capture.RecordEntry(sessionId);
        }
        catch
        {
            // Swallowed by design - see above. No logger reachable here without another service lookup,
            // and the record methods already log their own failures.
        }
    }
}
