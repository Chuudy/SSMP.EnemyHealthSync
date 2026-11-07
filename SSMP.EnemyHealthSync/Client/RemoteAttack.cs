using System;
using HarmonyLib;
using UnityEngine;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Detects attacks that SSMP plays back locally on behalf of a remote player.
/// SSMP keeps the DamageEnemies component on those attacks, so they can hit our local copy of an
/// enemy. The real hit already arrives from that player as an EnemyDamage packet, so these local
/// hits must be ignored or the damage would count twice.
/// </summary>
internal static class RemoteAttack
{
    // SSMP.Util.RemoteAttackComponent is internal to SSMP, so look it up by name.
    private static readonly Type? ComponentType = AccessTools.TypeByName("SSMP.Util.RemoteAttackComponent");

    private static bool _warned;

    internal static bool IsRemote(GameObject? source)
    {
        if (source == null)
        {
            return false;
        }

        if (ComponentType == null)
        {
            if (!_warned)
            {
                _warned = true;
                Utils.Log.Warn("SSMP.Util.RemoteAttackComponent not found; remote attacks may double-count damage");
            }

            return false;
        }

        return source.GetComponentInParent(ComponentType, true) != null;
    }
}
