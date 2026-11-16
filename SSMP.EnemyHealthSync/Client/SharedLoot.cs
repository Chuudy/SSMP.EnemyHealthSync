using System;
using System.Collections.Generic;
using GlobalSettings;
using HarmonyLib;
using SSMPEnemyHealthSync.Networking;
using SSMPEnemyHealthSync.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Shared loot: rosaries and shell shards an enemy drops land in the same places for everyone, and
/// whoever picks a piece up collects it for every player in the room.
///
/// Only the game where the enemy died on its own (not from another player's death message) rolls
/// the drops; it gives each piece an ID and sends where it was thrown. The other games don't roll
/// their own drops for that kill and spawn the same pieces instead. Picking a piece up (or it
/// breaking or burning up) is sent too, and the other games collect their copy the same way, so
/// each player gets the amount.
/// </summary>
internal static class SharedLoot
{
    private sealed class PendingDrop
    {
        internal CurrencyObjectBase Piece = null!;
        internal string Prefab = "";
        internal uint Id;
    }

    private sealed class PendingCollect
    {
        internal uint Id;
        internal float GiveUpAt;
    }

    /// <summary>A remote pickup that arrives during a piece's pickup delay is retried this long.</summary>
    private const float CollectRetrySeconds = 3f;

    private const float CleanupInterval = 2f;

    private static readonly AccessTools.FieldRef<CurrencyObjectBase, bool> Activated =
        AccessTools.FieldRefAccess<CurrencyObjectBase, bool>("activated");

    private static readonly AccessTools.FieldRef<ObjectPool, Dictionary<GameObject, List<GameObject>>> PooledObjects =
        AccessTools.FieldRefAccess<ObjectPool, Dictionary<GameObject, List<GameObject>>>("pooledObjects");

    private static readonly Random Ids = new();

    private static readonly Dictionary<uint, CurrencyObjectBase> Pieces = new();
    private static readonly Dictionary<CurrencyObjectBase, uint> PieceIds = new();
    private static readonly List<PendingDrop> Pending = new();
    private static readonly List<PendingCollect> PendingCollects = new();
    private static readonly List<uint> RemoveBuffer = new();
    private static readonly Dictionary<string, GameObject> Prefabs = new(StringComparer.Ordinal);

    /// <summary>The enemy whose drops are being thrown right now, while we roll them.</summary>
    private static bool _capturing;

    /// <summary>True while we collect or remove a piece because another player did.</summary>
    private static bool _applyingRemote;

    private static float _nextCleanup;
    private static bool _initialized;

    private static bool Enabled => Config.SharedLoot && Client.IsConnected;

    internal static void Init()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SceneManager.activeSceneChanged += (_, _) => Clear();
    }

    internal static void Clear()
    {
        Pieces.Clear();
        PieceIds.Clear();
        Pending.Clear();
        PendingCollects.Clear();
        Prefabs.Clear();
        _capturing = false;
    }

    // ------------------------------------------------------------------ rolling drops

    /// <summary>
    /// HealthManager.SpawnCurrency is about to throw an enemy's drops. Returns false to skip them:
    /// the enemy died here because of another player's death message, and that player's game
    /// sends the drops it rolled.
    /// </summary>
    internal static bool BeforeSpawnCurrency(HealthManager hm)
    {
        if (!Enabled)
        {
            return true;
        }

        switch (EnemyTracker.GetDropRole(hm))
        {
            case EnemyTracker.DropRole.Remote:
                Log.Debug($"Skipping {hm.name}'s own drops: the player whose game killed it shares theirs");
                return false;
            case EnemyTracker.DropRole.Local:
                _capturing = true;
                return true;
            default:
                return true;
        }
    }

    internal static void AfterSpawnCurrency() => _capturing = false;

    /// <summary>Called after every object pool spawn.</summary>
    internal static void OnSpawned(GameObject prefab, GameObject? spawned)
    {
        if (!_capturing || spawned == null || prefab == null)
        {
            return;
        }

        var piece = spawned.GetComponent<CurrencyObjectBase>();
        if (piece == null || PieceIds.ContainsKey(piece))
        {
            // Not currency, or the pool calling itself
            return;
        }

        var id = NewId();
        Register(id, piece);
        Pending.Add(new PendingDrop { Piece = piece, Prefab = prefab.name, Id = id });
    }

    private static uint NewId()
    {
        uint id;
        do
        {
            id = (uint) Ids.Next(1, int.MaxValue) ^ ((uint) Ids.Next(0, 2) << 31);
        } while (id == 0 || Pieces.ContainsKey(id));

        return id;
    }

    private static void Register(uint id, CurrencyObjectBase piece)
    {
        Pieces[id] = piece;
        PieceIds[piece] = id;
    }

    private static void Unregister(CurrencyObjectBase piece)
    {
        if (PieceIds.TryGetValue(piece, out var id))
        {
            PieceIds.Remove(piece);
            Pieces.Remove(id);
        }
    }

    // ------------------------------------------------------------------ every frame

    internal static void LateUpdate()
    {
        try
        {
            FlushPending();
            RetryCollects();
            Cleanup();
        }
        catch (Exception e)
        {
            Log.Debug($"Shared loot failed: {e.Message}");
            Pending.Clear();
        }
    }

    /// <summary>Send this frame's thrown pieces: their throw velocity is set right after they spawn.</summary>
    private static void FlushPending()
    {
        if (Pending.Count == 0)
        {
            return;
        }

        foreach (var drop in Pending)
        {
            if (drop.Piece == null || !drop.Piece.gameObject.activeInHierarchy)
            {
                continue;
            }

            var t = drop.Piece.transform;
            var body = drop.Piece.GetComponent<Rigidbody2D>();
            var velocity = body != null ? body.linearVelocity : Vector2.zero;
            Client.SendLootDrop(new LootDropPacket
            {
                DropId = drop.Id,
                Prefab = drop.Prefab,
                X = t.position.x, Y = t.position.y, Z = t.position.z,
                VelocityX = velocity.x, VelocityY = velocity.y,
            });
        }

        Log.Debug($"Shared {Pending.Count} dropped piece(s)");
        Pending.Clear();
    }

    private static void Cleanup()
    {
        if (Time.unscaledTime < _nextCleanup || Pieces.Count == 0)
        {
            return;
        }

        _nextCleanup = Time.unscaledTime + CleanupInterval;
        RemoveBuffer.Clear();
        foreach (var pair in Pieces)
        {
            if (pair.Value == null || !pair.Value.gameObject.activeInHierarchy)
            {
                RemoveBuffer.Add(pair.Key);
            }
        }

        foreach (var id in RemoveBuffer)
        {
            if (Pieces.TryGetValue(id, out var piece))
            {
                Pieces.Remove(id);
                if (piece != null)
                {
                    PieceIds.Remove(piece);
                }
            }
        }
    }

    // ------------------------------------------------------------------ copies from other players

    internal static void OnRemoteDrop(LootDropPacket packet)
    {
        if (!Enabled || Pieces.ContainsKey(packet.DropId))
        {
            return;
        }

        var prefab = FindPrefab(packet.Prefab);
        if (prefab == null)
        {
            Log.Debug($"Shared drop '{packet.Prefab}' has no prefab here, skipping it");
            return;
        }

        try
        {
            var spawned = prefab.Spawn(new Vector3(packet.X, packet.Y, packet.Z));
            var piece = spawned != null ? spawned.GetComponent<CurrencyObjectBase>() : null;
            if (piece == null)
            {
                return;
            }

            var body = spawned!.GetComponent<Rigidbody2D>();
            if (body != null)
            {
                body.linearVelocity = new Vector2(packet.VelocityX, packet.VelocityY);
            }

            Register(packet.DropId, piece);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to spawn shared drop '{packet.Prefab}': {e}");
        }
    }

    private static GameObject? FindPrefab(string name)
    {
        if (Prefabs.TryGetValue(name, out var cached) && cached != null)
        {
            return cached;
        }

        GameObject? found = null;
        foreach (var candidate in new[]
                 {
                     Gameplay.SmallGeoPrefab, Gameplay.MediumGeoPrefab, Gameplay.LargeGeoPrefab,
                     Gameplay.LargeSmoothGeoPrefab, Gameplay.ShellShardPrefab, Gameplay.ShellShardMidPrefab,
                 })
        {
            if (candidate != null && candidate.name == name)
            {
                found = candidate;
                break;
            }
        }

        if (found == null && ObjectPool.instance != null)
        {
            foreach (var prefab in PooledObjects(ObjectPool.instance).Keys)
            {
                if (prefab != null && prefab.name == name && prefab.GetComponent<CurrencyObjectBase>() != null)
                {
                    found = prefab;
                    break;
                }
            }
        }

        if (found != null)
        {
            Prefabs[name] = found;
        }

        return found;
    }

    // ------------------------------------------------------------------ pickups

    /// <summary>A piece was collected here; collect it for the others too.</summary>
    internal static void OnCollected(CurrencyObjectBase piece) => SendGone(piece, LootGoneKind.Collected);

    /// <summary>A piece broke or burned up without being collected; remove it for the others.</summary>
    internal static void OnDestroyedUncollected(CurrencyObjectBase piece, LootGoneKind kind) => SendGone(piece, kind);

    private static void SendGone(CurrencyObjectBase piece, LootGoneKind kind)
    {
        if (_applyingRemote || !Enabled || !PieceIds.TryGetValue(piece, out var id))
        {
            return;
        }

        Unregister(piece);
        Log.Debug($"Shared piece {id} {kind.ToString().ToLowerInvariant()} here");
        Client.SendLootGone(new LootGonePacket { DropId = id, Kind = (byte) kind });
    }

    internal static void OnRemoteGone(LootGonePacket packet)
    {
        if (!Enabled || !Pieces.TryGetValue(packet.DropId, out var piece))
        {
            return;
        }

        if (piece == null || !piece.gameObject.activeInHierarchy)
        {
            Pieces.Remove(packet.DropId);
            return;
        }

        switch ((LootGoneKind) packet.Kind)
        {
            case LootGoneKind.Collected:
                if (!TryCollect(packet.DropId, piece))
                {
                    // Still in its pickup delay (it was just spawned here): try again shortly
                    PendingCollects.Add(new PendingCollect
                    {
                        Id = packet.DropId, GiveUpAt = Time.unscaledTime + CollectRetrySeconds
                    });
                }

                break;
            case LootGoneKind.Broken:
                Remove(packet.DropId, piece, p => p.Break());
                break;
            default:
                Remove(packet.DropId, piece, p => p.BurnUp());
                break;
        }
    }

    /// <summary>Collect our copy the normal way, so we get the amount, with the usual effect and sound.</summary>
    private static bool TryCollect(uint id, CurrencyObjectBase piece)
    {
        if (Activated(piece))
        {
            // Already collected here
            Unregister(piece);
            return true;
        }

        _applyingRemote = true;
        try
        {
            piece.DoCollect();
        }
        catch (Exception e)
        {
            Log.Debug($"Could not collect shared piece {id}: {e.Message}");
        }
        finally
        {
            _applyingRemote = false;
        }

        if (!Activated(piece))
        {
            return false;
        }

        Unregister(piece);
        Log.Debug($"Collected shared piece {id}, picked up by another player");
        return true;
    }

    private static void Remove(uint id, CurrencyObjectBase piece, Action<CurrencyObjectBase> remove)
    {
        Unregister(piece);
        _applyingRemote = true;
        try
        {
            remove(piece);
        }
        catch (Exception e)
        {
            Log.Debug($"Could not remove shared piece {id}: {e.Message}");
        }
        finally
        {
            _applyingRemote = false;
        }
    }

    private static void RetryCollects()
    {
        for (var i = PendingCollects.Count - 1; i >= 0; i--)
        {
            var pending = PendingCollects[i];
            if (!Pieces.TryGetValue(pending.Id, out var piece) || piece == null || !piece.gameObject.activeInHierarchy)
            {
                PendingCollects.RemoveAt(i);
                continue;
            }

            if (TryCollect(pending.Id, piece))
            {
                PendingCollects.RemoveAt(i);
            }
            else if (Time.unscaledTime >= pending.GiveUpAt)
            {
                // E.g. our hero is dead: leave the piece here to be picked up normally
                Log.Debug($"Gave up collecting shared piece {pending.Id} for another player's pickup");
                PendingCollects.RemoveAt(i);
            }
        }
    }
}

internal enum LootGoneKind : byte
{
    Collected = 0,
    Broken = 1,
    Burned = 2,
}

[HarmonyPatch(typeof(HealthManager), "SpawnCurrency")]
internal static class HealthManagerSpawnCurrencyPatch
{
    private static bool Prefix(HealthManager __instance)
    {
        try
        {
            return SharedLoot.BeforeSpawnCurrency(__instance);
        }
        catch (Exception e)
        {
            Log.Debug($"Shared loot hook failed: {e.Message}");
            return true;
        }
    }

    private static void Finalizer() => SharedLoot.AfterSpawnCurrency();
}

[HarmonyPatch(typeof(CurrencyObjectBase), "Collect")]
internal static class CurrencyCollectPatch
{
    private static void Postfix(CurrencyObjectBase __instance, bool __result)
    {
        if (__result)
        {
            SharedLoot.OnCollected(__instance);
        }
    }
}

[HarmonyPatch(typeof(CurrencyObjectBase), nameof(CurrencyObjectBase.Break))]
internal static class CurrencyBreakPatch
{
    private static void Prefix(CurrencyObjectBase __instance) =>
        SharedLoot.OnDestroyedUncollected(__instance, LootGoneKind.Broken);
}

[HarmonyPatch(typeof(CurrencyObjectBase), nameof(CurrencyObjectBase.BurnUp))]
internal static class CurrencyBurnUpPatch
{
    private static void Prefix(CurrencyObjectBase __instance) =>
        SharedLoot.OnDestroyedUncollected(__instance, LootGoneKind.Burned);
}
