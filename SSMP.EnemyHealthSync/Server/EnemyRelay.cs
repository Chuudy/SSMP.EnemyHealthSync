using System;
using System.Collections.Generic;
using System.Linq;
using SSMPEnemyHealthSync.Networking;
using SSMPEnemyHealthSync.Utils;

namespace SSMPEnemyHealthSync.Server;

/// <summary>
/// Relays enemy damage and deaths between players in the same scene, and remembers which enemies
/// died in a scene while at least one player is still in it. Once everyone has left a scene its
/// enemies are forgotten, so they respawn as they normally would.
///
/// Server packet handlers run on the network thread, so all state is guarded by a lock.
/// </summary>
internal static class EnemyRelay
{
    private static readonly object Lock = new();

    /// <summary>Scene of each player, as reported by the player itself (SceneEntered packets).</summary>
    private static readonly Dictionary<ushort, string> PlayerScene = new();

    /// <summary>Enemies that died in each occupied scene.</summary>
    private static readonly Dictionary<string, HashSet<uint>> DeadByScene = new(StringComparer.Ordinal);

    internal static void OnSceneEntered(ushort playerId, SceneEnteredPacket packet)
    {
        var scene = packet.SceneName ?? "";
        uint[] deadSnapshot;

        lock (Lock)
        {
            if (scene.Length == 0)
            {
                PlayerScene.Remove(playerId);
            }
            else
            {
                PlayerScene[playerId] = scene;
            }

            ForgetEmptyScenes();

            deadSnapshot = scene.Length > 0 && DeadByScene.TryGetValue(scene, out var dead)
                ? dead.ToArray()
                : [];
        }

        Log.Debug($"Player {playerId} entered '{scene}', sending {deadSnapshot.Length} dead enemies");

        // Enemies other players already killed here stay dead for the newcomer
        foreach (var id in deadSnapshot)
        {
            Server.SendDeath(id, playerId);
        }
    }

    internal static void OnDamage(ushort playerId, EnemyDamagePacket packet)
    {
        if (packet.Damage <= 0)
        {
            return;
        }

        var targets = GetOthersInScene(playerId, out _);
        foreach (var target in targets)
        {
            Server.SendDamage(packet.EnemyId, packet.Damage, target);
        }
    }

    internal static void OnDeath(ushort playerId, EnemyDeathPacket packet)
    {
        ushort[] targets;
        lock (Lock)
        {
            targets = GetOthersInScene(playerId, out var scene);
            if (scene == null)
            {
                return;
            }

            if (!DeadByScene.TryGetValue(scene, out var dead))
            {
                dead = new HashSet<uint>();
                DeadByScene[scene] = dead;
            }

            if (!dead.Add(packet.EnemyId))
            {
                // Someone else's death packet for this enemy was already relayed
                return;
            }
        }

        foreach (var target in targets)
        {
            Server.SendDeath(packet.EnemyId, target);
        }
    }

    internal static void OnPlayerDisconnect(ushort playerId)
    {
        lock (Lock)
        {
            PlayerScene.Remove(playerId);
            ForgetEmptyScenes();
        }
    }

    internal static void Clear()
    {
        lock (Lock)
        {
            PlayerScene.Clear();
            DeadByScene.Clear();
        }
    }

    private static ushort[] GetOthersInScene(ushort playerId, out string? scene)
    {
        lock (Lock)
        {
            if (!PlayerScene.TryGetValue(playerId, out var s))
            {
                scene = null;
                return [];
            }

            scene = s;
            return PlayerScene
                .Where(kv => kv.Key != playerId && kv.Value == s)
                .Select(kv => kv.Key)
                .ToArray();
        }
    }

    /// <summary>Must be called while holding <see cref="Lock"/>.</summary>
    private static void ForgetEmptyScenes()
    {
        var occupied = new HashSet<string>(PlayerScene.Values, StringComparer.Ordinal);
        foreach (var scene in DeadByScene.Keys.Where(s => !occupied.Contains(s)).ToArray())
        {
            DeadByScene.Remove(scene);
        }
    }
}
