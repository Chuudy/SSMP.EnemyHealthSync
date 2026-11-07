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
/// Measures the health lost in every local damage call and reports it, with the hit's direction
/// and attack type when there is a HitInstance. Only the outermost call is measured, so damage
/// methods that call each other are not counted twice.
/// </summary>
[HarmonyPatch]
internal static class HealthManagerDamagePatch
{
    internal struct State
    {
        internal bool Measure;
        internal int Hp;
        internal float Direction;
        internal AttackTypes AttackType;
    }

    private static int _depth;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HealthManager), nameof(HealthManager.TakeDamage), [typeof(HitInstance)]);
        yield return AccessTools.Method(typeof(HealthManager), nameof(HealthManager.ApplyExtraDamage), [typeof(int)]);
        yield return AccessTools.Method(typeof(HealthManager), nameof(HealthManager.ApplyExtraDamage), [typeof(HitInstance)]);
    }

    private static void Prefix(HealthManager __instance, object[] __args, out State __state)
    {
        __state = new State { Measure = _depth == 0, Hp = __instance.hp, Direction = float.NaN, AttackType = AttackTypes.Generic };
        if (__args.Length > 0 && __args[0] is HitInstance hit)
        {
            __state.Direction = hit.Direction;
            __state.AttackType = hit.AttackType;
        }

        _depth++;
    }

    // A finalizer runs even if the original method throws, so _depth can never get stuck
    private static void Finalizer(HealthManager __instance, ref State __state)
    {
        _depth--;
        if (!__state.Measure || __instance == null)
        {
            return;
        }

        var damage = __state.Hp - __instance.hp;
        if (damage > 0)
        {
            EnemyTracker.OnLocalDamage(__instance, damage, __state.Direction, __state.AttackType);
        }
    }
}

/// <summary>
/// Reports local enemy deaths (all Die overloads, outermost call only), and stops puppets from
/// dying on their own: only the enemy's owner decides when it dies.
/// </summary>
[HarmonyPatch]
internal static class HealthManagerDiePatch
{
    private static int _depth;

    private static IEnumerable<MethodBase> TargetMethods() =>
        typeof(HealthManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == nameof(HealthManager.Die));

    private static bool Prefix(HealthManager __instance, out bool __state)
    {
        __state = _depth == 0 && !__instance.isDead;
        _depth++;

        if (!EnemyTracker.ApplyingRemote && EnemyTracker.IsPuppet(__instance))
        {
            __state = false;
            return false;
        }

        return true;
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
