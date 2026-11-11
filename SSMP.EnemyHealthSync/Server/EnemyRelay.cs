using System;
using System.Collections.Generic;
using System.Linq;
using SSMPEnemyHealthSync.Networking;
using SSMPEnemyHealthSync.Utils;

namespace SSMPEnemyHealthSync.Server;

/// <summary>
/// Server side of the enemy sync:
/// - Keeps, per scene, the players in the order they entered. The first one is the room host.
/// - Tracks who owns (runs the AI of) each enemy: the room host, unless the enemy's owner handed
///   it to another player. Only the current owner may hand an enemy over.
/// - Relays enemy state from each enemy's owner to the other players in the room.
/// - Relays damage and deaths between players in the same room.
/// - Remembers which enemies died in a scene while at least one player is still in it. Once
///   everyone has left, its enemies are forgotten, so they respawn as they normally would.
///
/// Server packet handlers run on the network thread, so all state is guarded by a lock. Packets
/// are sent outside the lock.
/// </summary>
internal static class EnemyRelay
{
    private static readonly object Lock = new();

    /// <summary>Scene of each player, as reported by the player itself (SceneEntered packets).</summary>
    private static readonly Dictionary<ushort, string> PlayerScene = new();

    /// <summary>Players of each scene in the order they entered; index 0 is the room host.</summary>
    private static readonly Dictionary<string, List<ushort>> RoomPlayers = new(StringComparer.Ordinal);

    /// <summary>Enemies whose owner is not the room host, per scene.</summary>
    private static readonly Dictionary<string, Dictionary<uint, ushort>> OwnerOverrides = new(StringComparer.Ordinal);

    /// <summary>Enemies that died in each occupied scene.</summary>
    private static readonly Dictionary<string, HashSet<uint>> DeadByScene = new(StringComparer.Ordinal);

    /// <summary>Players that are downed (waiting for a revive), with their revive progress.</summary>
    private static readonly Dictionary<ushort, (byte Hits, byte Needed)> Downed = new();

    private readonly struct OwnershipMessage
    {
        internal readonly ushort PlayerId;
        internal readonly RoomOwnershipPacket Packet;

        internal OwnershipMessage(ushort playerId, RoomOwnershipPacket packet)
        {
            PlayerId = playerId;
            Packet = packet;
        }
    }

    internal static void OnSceneEntered(ushort playerId, SceneEnteredPacket packet)
    {
        var scene = packet.SceneName ?? "";
        uint[] deadSnapshot = [];
        (ushort Id, byte Hits, byte Needed)[] downedInRoom = [];
        var messages = new List<OwnershipMessage>();
        ushort[] notifyStoodUp;

        lock (Lock)
        {
            // Changing rooms ends being downed (the client clears it too)
            notifyStoodUp = ClearDowned(playerId);

            var oldScene = RemoveFromRoom(playerId, out var oldRoomChanged);
            if (oldRoomChanged && oldScene != null)
            {
                messages.AddRange(BuildSnapshots(oldScene));
            }

            if (scene.Length > 0)
            {
                PlayerScene[playerId] = scene;
                if (!RoomPlayers.TryGetValue(scene, out var players))
                {
                    players = new List<ushort>();
                    RoomPlayers[scene] = players;
                }

                players.Add(playerId);
                messages.Add(new OwnershipMessage(playerId, BuildSnapshot(scene, playerId)));

                if (DeadByScene.TryGetValue(scene, out var dead))
                {
                    deadSnapshot = dead.ToArray();
                }

                downedInRoom = players
                    .Where(p => p != playerId && Downed.ContainsKey(p))
                    .Select(p => (p, Downed[p].Hits, Downed[p].Needed))
                    .ToArray();
            }

            ForgetEmptyScenes();
        }

        SendSnapshots(messages);

        foreach (var target in notifyStoodUp)
        {
            Server.SendDowned(playerId, false, 0, 0, target);
        }

        if (scene.Length == 0)
        {
            return;
        }

        // Teammates already downed here can be revived by the newcomer
        foreach (var (downedId, hits, needed) in downedInRoom)
        {
            Server.SendDowned(downedId, true, hits, needed, playerId);
        }

        Log.Debug($"Player {playerId} entered '{scene}', sending {deadSnapshot.Length} dead enemies");

        // Enemies other players already killed here stay dead for the newcomer
        foreach (var id in deadSnapshot)
        {
            Server.SendDeath(id, playerId);
        }
    }

    internal static void OnHandover(ushort playerId, HandoverPacket packet)
    {
        List<OwnershipMessage> messages;
        lock (Lock)
        {
            if (packet.ToPlayerId == playerId ||
                !PlayerScene.TryGetValue(playerId, out var scene) ||
                !RoomPlayers.TryGetValue(scene, out var players) ||
                !players.Contains(packet.ToPlayerId) ||
                OwnerOf(scene, players, packet.EnemyId) != playerId)
            {
                // Stale (ownership changed meanwhile, or the target left): ignore
                return;
            }

            if (!OwnerOverrides.TryGetValue(scene, out var overrides))
            {
                overrides = new Dictionary<uint, ushort>();
                OwnerOverrides[scene] = overrides;
            }

            if (packet.ToPlayerId == players[0])
            {
                overrides.Remove(packet.EnemyId);
            }
            else
            {
                overrides[packet.EnemyId] = packet.ToPlayerId;
            }

            Log.Debug($"Enemy {packet.EnemyId} in '{scene}' handed from player {playerId} to {packet.ToPlayerId}");
            messages = BuildSnapshots(scene);
        }

        SendSnapshots(messages);
    }

    internal static void OnState(ushort playerId, EnemyStatePacket packet)
    {
        ushort[] targets;
        lock (Lock)
        {
            // Only the owner's copy of an enemy is the real one
            if (!PlayerScene.TryGetValue(playerId, out var scene) ||
                !RoomPlayers.TryGetValue(scene, out var players) ||
                OwnerOf(scene, players, packet.EnemyId) != playerId)
            {
                return;
            }

            targets = players.Where(p => p != playerId).ToArray();
        }

        foreach (var target in targets)
        {
            Server.SendState(packet, target);
        }
    }

    /// <summary>A puppet's FSM got an event; only the enemy's owner can act on it.</summary>
    internal static void OnFsmEvent(ushort playerId, EnemyFsmEventPacket packet)
    {
        ushort owner;
        lock (Lock)
        {
            if (!PlayerScene.TryGetValue(playerId, out var scene) ||
                !RoomPlayers.TryGetValue(scene, out var players))
            {
                return;
            }

            owner = OwnerOf(scene, players, packet.EnemyId);
            if (owner == playerId || !players.Contains(owner))
            {
                return;
            }
        }

        Server.SendFsmEvent(packet, owner);
    }

    /// <summary>The owner's copy of an enemy was removed without dying; others remove theirs.</summary>
    internal static void OnDespawn(ushort playerId, EnemyDespawnPacket packet)
    {
        ushort[] targets;
        lock (Lock)
        {
            if (!PlayerScene.TryGetValue(playerId, out var scene) ||
                !RoomPlayers.TryGetValue(scene, out var players) ||
                OwnerOf(scene, players, packet.EnemyId) != playerId)
            {
                return;
            }

            targets = players.Where(p => p != playerId).ToArray();
            OwnerOverrides.TryGetValue(scene, out var overrides);
            overrides?.Remove(packet.EnemyId);
        }

        foreach (var target in targets)
        {
            Server.SendDespawn(packet, target);
        }
    }

    internal static void OnFsmVars(ushort playerId, EnemyFsmVarsPacket packet)
    {
        ushort[] targets;
        lock (Lock)
        {
            if (!PlayerScene.TryGetValue(playerId, out var scene) ||
                !RoomPlayers.TryGetValue(scene, out var players) ||
                OwnerOf(scene, players, packet.EnemyId) != playerId)
            {
                return;
            }

            targets = players.Where(p => p != playerId).ToArray();
        }

        foreach (var target in targets)
        {
            Server.SendFsmVars(packet, target);
        }
    }

    /// <summary>
    /// Relayed from anyone in the room, not just the enemy's owner: right after a handover the old
    /// owner's game still runs the enemy for a moment, and a throw made then is real on that game.
    /// </summary>
    internal static void OnProjectile(ushort playerId, EnemyProjectilePacket packet)
    {
        foreach (var target in GetOthersInScene(playerId, out _))
        {
            Server.SendProjectile(packet, target);
        }
    }

    internal static void OnArenaCleared(ushort playerId, ArenaClearedPacket packet)
    {
        foreach (var target in GetOthersInScene(playerId, out _))
        {
            Server.SendArenaCleared(packet, target);
        }
    }

    internal static void OnDamage(ushort playerId, EnemyDamagePacket packet)
    {
        if (packet.Damage <= 0)
        {
            return;
        }

        // Everyone in the room gets it: the owner applies it, puppets ignore it, and players with
        // movement sync off apply it to their own copy.
        foreach (var target in GetOthersInScene(playerId, out _))
        {
            Server.SendDamage(packet, target);
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

            OwnerOverrides.TryGetValue(scene, out var overrides);
            overrides?.Remove(packet.EnemyId);
        }

        foreach (var target in targets)
        {
            Server.SendDeath(packet.EnemyId, target);
        }
    }

    internal static void OnDowned(ushort playerId, PlayerDownedPacket packet)
    {
        ushort[] targets;
        var progress = (packet.ReviveHits, packet.ReviveHitsNeeded);
        lock (Lock)
        {
            bool changed;
            if (packet.Downed)
            {
                // A downed player re-sends this after each revive hit to share the progress
                changed = !Downed.TryGetValue(playerId, out var old) || old != progress;
                Downed[playerId] = progress;
            }
            else
            {
                changed = Downed.Remove(playerId);
            }

            if (!changed)
            {
                return;
            }

            targets = GetOthersInScene(playerId, out _);
        }

        Log.Debug(packet.Downed
            ? $"Player {playerId} is down (revive {progress.ReviveHits}/{progress.ReviveHitsNeeded})"
            : $"Player {playerId} is back up");
        foreach (var target in targets)
        {
            Server.SendDowned(playerId, packet.Downed, progress.ReviveHits, progress.ReviveHitsNeeded, target);
        }
    }

    internal static void OnReviveHit(ushort playerId, ReviveHitPacket packet)
    {
        lock (Lock)
        {
            // Only a standing player in the same room can revive a downed one
            if (packet.TargetPlayerId == playerId ||
                Downed.ContainsKey(playerId) ||
                !Downed.ContainsKey(packet.TargetPlayerId) ||
                !PlayerScene.TryGetValue(playerId, out var scene) ||
                !PlayerScene.TryGetValue(packet.TargetPlayerId, out var targetScene) ||
                scene != targetScene)
            {
                return;
            }
        }

        Server.SendReviveHit(packet.TargetPlayerId);
    }

    /// <summary>
    /// Mark a player as no longer downed. Returns the other players of their room to notify (empty
    /// if they were not downed). Must be called while holding <see cref="Lock"/>, before the player
    /// is removed from the room.
    /// </summary>
    private static ushort[] ClearDowned(ushort playerId)
    {
        return Downed.Remove(playerId) ? GetOthersInScene(playerId, out _) : [];
    }

    internal static void OnPlayerDisconnect(ushort playerId)
    {
        var messages = new List<OwnershipMessage>();
        ushort[] notifyStoodUp;
        lock (Lock)
        {
            notifyStoodUp = ClearDowned(playerId);
            var scene = RemoveFromRoom(playerId, out var changed);
            if (changed && scene != null)
            {
                messages.AddRange(BuildSnapshots(scene));
            }

            ForgetEmptyScenes();
        }

        SendSnapshots(messages);

        foreach (var target in notifyStoodUp)
        {
            Server.SendDowned(playerId, false, 0, 0, target);
        }
    }

    internal static void Clear()
    {
        lock (Lock)
        {
            Downed.Clear();
            PlayerScene.Clear();
            RoomPlayers.Clear();
            OwnerOverrides.Clear();
            DeadByScene.Clear();
        }
    }

    /// <summary>Must be called while holding <see cref="Lock"/>.</summary>
    private static ushort OwnerOf(string scene, List<ushort> players, uint enemyId) =>
        OwnerOverrides.TryGetValue(scene, out var overrides) && overrides.TryGetValue(enemyId, out var owner)
            ? owner
            : players[0];

    /// <summary>
    /// Remove a player from their current room and give their enemies back to the room host.
    /// <paramref name="roomChanged"/> is true when the remaining players' ownership changed (new
    /// room host or enemies returned). Returns the room's scene, or null if not in a room.
    /// Must be called while holding <see cref="Lock"/>.
    /// </summary>
    private static string? RemoveFromRoom(ushort playerId, out bool roomChanged)
    {
        roomChanged = false;
        if (!PlayerScene.TryGetValue(playerId, out var scene))
        {
            return null;
        }

        PlayerScene.Remove(playerId);
        if (!RoomPlayers.TryGetValue(scene, out var players))
        {
            return scene;
        }

        var wasHost = players.Count > 0 && players[0] == playerId;
        players.Remove(playerId);
        if (players.Count == 0)
        {
            // Forget the room right away, even if the same player re-enters it in the same call
            // (e.g. reloading the room after dying): its enemies respawn.
            RoomPlayers.Remove(scene);
            OwnerOverrides.Remove(scene);
            DeadByScene.Remove(scene);
            return scene;
        }

        if (OwnerOverrides.TryGetValue(scene, out var overrides))
        {
            // The leaver's enemies go back to the room host; enemies the new host owned through an
            // override are now its default
            foreach (var id in overrides.Where(kv => kv.Value == playerId || kv.Value == players[0])
                         .Select(kv => kv.Key).ToArray())
            {
                overrides.Remove(id);
                roomChanged = true;
            }
        }

        if (wasHost)
        {
            Log.Debug($"Room host of '{scene}' left, player {players[0]} takes over");
            roomChanged = true;
        }

        return scene;
    }

    /// <summary>Snapshots for everyone in the room. Must be called while holding <see cref="Lock"/>.</summary>
    private static List<OwnershipMessage> BuildSnapshots(string scene)
    {
        if (!RoomPlayers.TryGetValue(scene, out var players))
        {
            return [];
        }

        return players.Select(p => new OwnershipMessage(p, BuildSnapshot(scene, p))).ToList();
    }

    /// <summary>
    /// The room host gets the enemies owned by others; everyone else gets the enemies they own.
    /// Must be called while holding <see cref="Lock"/>.
    /// </summary>
    private static RoomOwnershipPacket BuildSnapshot(string scene, ushort playerId)
    {
        var isHost = RoomPlayers[scene][0] == playerId;
        var packet = new RoomOwnershipPacket { SceneName = scene, IsRoomHost = isHost };
        if (OwnerOverrides.TryGetValue(scene, out var overrides))
        {
            packet.Exceptions = overrides
                .Where(kv => isHost ? kv.Value != playerId : kv.Value == playerId)
                .Select(kv => kv.Key)
                .ToList();
        }

        return packet;
    }

    private static void SendSnapshots(List<OwnershipMessage> messages)
    {
        foreach (var message in messages)
        {
            Server.SendOwnership(message.Packet, message.PlayerId);
        }
    }

    private static ushort[] GetOthersInScene(ushort playerId, out string? scene)
    {
        lock (Lock)
        {
            if (!PlayerScene.TryGetValue(playerId, out var s) || !RoomPlayers.TryGetValue(s, out var players))
            {
                scene = null;
                return [];
            }

            scene = s;
            return players.Where(p => p != playerId).ToArray();
        }
    }

    /// <summary>Must be called while holding <see cref="Lock"/>.</summary>
    private static void ForgetEmptyScenes()
    {
        foreach (var scene in DeadByScene.Keys.Where(s => !RoomPlayers.ContainsKey(s)).ToArray())
        {
            DeadByScene.Remove(scene);
        }

        foreach (var scene in OwnerOverrides.Keys.Where(s => !RoomPlayers.ContainsKey(s)).ToArray())
        {
            OwnerOverrides.Remove(scene);
        }
    }
}
