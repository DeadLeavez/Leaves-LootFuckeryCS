using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Services;

namespace Leaves_LootFuckery;

// A postfix on LocationLifecycleService.GenerateLocationAndLoot - the single method BOTH vanilla SPT and
// Fika call to build a raid's loot. (Fika changes the client endpoint and prefix-patches StartLocalRaid
// to skip the vanilla path, but its override still calls THIS method itself, once per raid, and caches
// the result for every coop player. So patching here captures real runs no matter which endpoint fired.)
//
// This is SPT's own Harmony wrapper: subclass AbstractPatch, point GetTargetMethod at the method, and
// add a static [PatchPostfix] that runs AFTER the original. The patch method is static because the patch
// library invokes it directly with no instance - so it can't take constructor-injected services; instead
// it pulls the singleton RealRunCapture out of the DI container through ServiceLocator. LootFuckery.OnLoad
// calls .Enable() on this once (only when captureRealRuns is on).
public class GenerateLocationAndLootPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        // Single method, no overloads - GetMethod by name is unambiguous.
        return typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.GenerateLocationAndLoot))!;
    }

    // The parameter names sessionId and name are the ORIGINAL method's own parameter names; the patch
    // library binds a postfix parameter to the matching original argument by name. __result is the
    // LocationBase the method just returned - the generated loot for this raid.
    [PatchPostfix]
    public static void Postfix(MongoId sessionId, string name, LocationBase? __result)
    {
        // ServiceLocator is marked [Obsolete] (SPT intends to move patches onto DI injection later), but
        // it's the current supported way for a static patch to reach a service, and works in 4.0.13.
#pragma warning disable CS0618
        var capture = ServiceLocator.ServiceProvider.GetService<RealRunCapture>();
#pragma warning restore CS0618
        if (capture == null)
        {
            return;
        }
        capture.Capture(sessionId, name, __result);
    }
}
