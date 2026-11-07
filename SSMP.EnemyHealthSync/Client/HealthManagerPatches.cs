using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Ignores hits from remote players' played-back attacks; their damage arrives over the network instead.
/// </summary>
[HarmonyPatch(typeof(HealthManager), nameof(HealthManager.Hit))]
internal static class HealthManagerHitPatch
{
    private static bool Prefix(ref HitInstance hitInstance, ref IHitResponder.HitResponse __result)
    {
        if (!EnemyTracker.IgnoreRemoteHits || !RemoteAttack.IsRemote(hitInstance.Source))
        {
            return true;
        }

        __result = IHitResponder.HitResponse.Default;
        return false;
    }
}

/// <summary>
/// Measures the health lost in every local damage call and reports it. Only the outermost call is
/// measured, so damage methods that call each other are not counted twice.
/// </summary>
[HarmonyPatch]
internal static class HealthManagerDamagePatch
{
    private static int _depth;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HealthManager), nameof(HealthManager.TakeDamage), [typeof(HitInstance)]);
        yield return AccessTools.Method(typeof(HealthManager), nameof(HealthManager.ApplyExtraDamage), [typeof(int)]);
        yield return AccessTools.Method(typeof(HealthManager), nameof(HealthManager.ApplyExtraDamage), [typeof(HitInstance)]);
    }

    private static void Prefix(HealthManager __instance, out int __state)
    {
        __state = _depth == 0 ? __instance.hp : int.MinValue;
        _depth++;
    }

    // A finalizer runs even if the original method throws, so _depth can never get stuck
    private static void Finalizer(HealthManager __instance, int __state)
    {
        _depth--;
        if (__state == int.MinValue || __instance == null)
        {
            return;
        }

        var damage = __state - __instance.hp;
        if (damage > 0)
        {
            EnemyTracker.OnLocalDamage(__instance, damage);
        }
    }
}

/// <summary>Reports local enemy deaths (all Die overloads, outermost call only).</summary>
[HarmonyPatch]
internal static class HealthManagerDiePatch
{
    private static int _depth;

    private static IEnumerable<MethodBase> TargetMethods() =>
        typeof(HealthManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(HealthManager.Die));

    private static void Prefix(HealthManager __instance, out bool __state)
    {
        __state = _depth == 0 && !__instance.isDead;
        _depth++;
    }

    private static void Finalizer(HealthManager __instance, bool __state)
    {
        _depth--;
        if (__state && __instance != null && __instance.isDead)
        {
            EnemyTracker.OnLocalDeath(__instance);
        }
    }
}
