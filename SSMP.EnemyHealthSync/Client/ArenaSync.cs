using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SSMPEnemyHealthSync.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Arenas (BattleScene: locked gates, waves of enemies) run separately on each player's game. Each
/// one counts the enemies left in its waves, so a game that got out of step (an enemy left over that
/// only exists there) never ends its arena and keeps its gates shut. When one player's arena is
/// cleared, it is cleared for everyone in the room.
/// With DebugLogging on, running arenas also log their progress every few seconds.
/// </summary>
internal static class ArenaSync
{
    private static readonly AccessTools.FieldRef<BattleScene, bool> Completed =
        AccessTools.FieldRefAccess<BattleScene, bool>("completed");

    private static readonly AccessTools.FieldRef<BattleScene, bool> Started =
        AccessTools.FieldRefAccess<BattleScene, bool>("started");

    private const float DiagnosticsInterval = 3f;
    private static float _nextDiagnostics;

    /// <summary>True while we end an arena because of another player, so it isn't sent back.</summary>
    private static bool _applyingRemote;

    internal static void OnLocalCleared(BattleScene battle)
    {
        if (_applyingRemote || !Config.Enabled || !Client.IsConnected || battle == null)
        {
            return;
        }

        var id = EnemyTracker.ComputeId(battle.transform);
        Log.Debug($"Arena '{battle.name}' ({id}) cleared, clearing it for everyone in the room");
        Client.SendArenaCleared(id);
    }

    internal static void OnRemoteCleared(uint arenaId)
    {
        if (!Config.Enabled || !Client.IsConnected)
        {
            return;
        }

        var battle = Find(arenaId);
        if (battle == null)
        {
            Log.Debug($"Arena {arenaId} was cleared by another player, but it isn't in this room");
            return;
        }

        if (Completed(battle))
        {
            return;
        }

        if (!battle.gameObject.activeInHierarchy)
        {
            Log.Debug($"Arena '{battle.name}' ({arenaId}) was cleared by another player, but it is inactive here");
            return;
        }

        Log.Info($"Arena '{battle.name}' was cleared by another player, ending it here " +
                 $"(was on wave {battle.currentWave + 1}, {battle.currentEnemies} enemies left)");
        _applyingRemote = true;
        try
        {
            battle.DoEndBattle();
        }
        catch (Exception e)
        {
            Log.Error($"Failed to end arena '{battle.name}': {e}");
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private static BattleScene? Find(uint arenaId)
    {
        var active = SceneManager.GetActiveScene().name;
        foreach (var battle in Object.FindObjectsByType<BattleScene>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (battle != null && battle.gameObject.scene.name.StartsWith(active, StringComparison.Ordinal) &&
                EnemyTracker.ComputeId(battle.transform) == arenaId)
            {
                return battle;
            }
        }

        return null;
    }

    /// <summary>Debug logging: progress of running arenas, and which enemies each wave is still waiting for.</summary>
    internal static void Update()
    {
        if (!Config.DebugLogging || !Client.IsConnected || Time.unscaledTime < _nextDiagnostics)
        {
            return;
        }

        _nextDiagnostics = Time.unscaledTime + DiagnosticsInterval;
        try
        {
            foreach (var battle in Object.FindObjectsByType<BattleScene>(FindObjectsSortMode.None))
            {
                if (battle == null || !Started(battle) || Completed(battle))
                {
                    continue;
                }

                Log.Info($"[Arena] '{battle.name}': wave {battle.currentWave + 1}/{battle.waves.Count}, " +
                         $"enemies {battle.currentEnemies}, next wave at {battle.enemiesToNext}; waiting for: " +
                         DescribeRemaining(battle));
            }
        }
        catch (Exception e)
        {
            Log.Debug($"[Arena] diagnostics failed: {e.Message}");
        }
    }

    private static string DescribeRemaining(BattleScene battle)
    {
        var sb = new StringBuilder();
        for (var i = 0; i <= battle.currentWave && i < battle.waves.Count; i++)
        {
            var wave = battle.waves[i];
            if (wave == null)
            {
                continue;
            }

            foreach (Transform child in wave.transform)
            {
                var hm = child.GetComponent<HealthManager>();
                var r = child.GetComponent<Renderer>();
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{wave.name}/{child.name}");
                sb.Append(child.gameObject.activeSelf ? "" : " inactive");
                sb.Append(r != null && !r.enabled ? " hidden" : "");
                if (hm != null)
                {
                    sb.Append($" hp {hm.hp}{(hm.isDead ? " dead" : "")}");
                    sb.Append(EnemyTracker.DescribeOwnership(hm));
                }
            }
        }

        return sb.Length > 0 ? sb.ToString() : "nothing";
    }
}

/// <summary>Reports arenas cleared on this game (the first call that actually ends the battle).</summary>
[HarmonyPatch(typeof(BattleScene), nameof(BattleScene.DoEndBattle))]
internal static class BattleSceneEndPatch
{
    private static readonly AccessTools.FieldRef<BattleScene, bool> Completed =
        AccessTools.FieldRefAccess<BattleScene, bool>("completed");

    private static void Prefix(BattleScene __instance, out bool __state) => __state = !Completed(__instance);

    private static void Postfix(BattleScene __instance, bool __state)
    {
        if (__state && Completed(__instance))
        {
            ArenaSync.OnLocalCleared(__instance);
        }
    }
}
