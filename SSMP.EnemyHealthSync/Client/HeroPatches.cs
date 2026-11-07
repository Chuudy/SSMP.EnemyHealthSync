using System;
using GlobalEnums;
using HarmonyLib;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Turns a fatal hit into going down: PlayerData.TakeHealth is where health actually drops, so the
/// damage is reduced to leave exactly 1 HP (blue health is used up first, as in the game).
/// </summary>
[HarmonyPatch(typeof(PlayerData), nameof(PlayerData.TakeHealth))]
internal static class PlayerDataTakeHealthPatch
{
    private static void Prefix(PlayerData __instance, ref int amount, bool hasBlueHealth)
    {
        if (amount <= 0)
        {
            return;
        }

        if (DownedState.BlocksDamage)
        {
            amount = 0;
            return;
        }

        var blue = hasBlueHealth ? Math.Max(0, __instance.healthBlue) : 0;
        var pool = __instance.health + blue;
        if (amount < pool || !DownedState.CanGoDown())
        {
            return;
        }

        amount = Math.Max(0, pool - 1);
        DownedState.MarkPendingDown();
    }
}

/// <summary>
/// No hits (and no knockback) from enemies while downed, about to go down, or just revived.
/// Environmental hazards (spikes, acid, pits, ...) still go through so the game's hazard respawn
/// moves the player to safe ground; the TakeHealth patch makes them cost no health meanwhile.
/// </summary>
[HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
internal static class HeroTakeDamagePatch
{
    private static bool Prefix(HazardType hazardType) =>
        !DownedState.BlocksDamage || hazardType > HazardType.ENEMY;
}

/// <summary>Each attack of the local player counts as a revive hit on downed teammates next to them.</summary>
[HarmonyPatch(typeof(HeroController), nameof(HeroController.Attack))]
internal static class HeroAttackPatch
{
    private static void Postfix() => DownedState.OnLocalAttack();
}
