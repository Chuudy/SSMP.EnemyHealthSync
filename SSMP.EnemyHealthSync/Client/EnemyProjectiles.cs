using System;
using System.Collections.Generic;
using HarmonyLib;
using HutongGames.PlayMaker;
using SSMPEnemyHealthSync.Networking;
using SSMPEnemyHealthSync.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Projectiles are separate objects an enemy's FSM spawns from the game's object pool, so only the
/// game running that enemy has them. When an enemy we own spawns something that can hurt the hero,
/// the other players spawn the same prefab with the same position and velocity. Each copy is a real
/// projectile on its own game: it flies, breaks and recycles by itself, and hits that game's hero.
/// </summary>
internal static class EnemyProjectiles
{
    private sealed class PendingSpawn
    {
        internal GameObject Object = null!;
        internal string Prefab = "";
        internal uint EnemyId;
        internal Transform EnemyRoot = null!;
        internal int Frame;
        internal readonly List<(string Fsm, string Event)> Events = new();
    }

    /// <summary>Frames to wait for a velocity when the spawning FSM sets it a little later.</summary>
    private const int MaxVelocityWaitFrames = 2;

    /// <summary>A copy that hasn't recycled itself by now is removed, so nothing can linger forever.</summary>
    private const float ReplicaLifetime = 20f;

    private const int MaxEvents = 4;

    private static readonly AccessTools.FieldRef<ObjectPool, Dictionary<GameObject, List<GameObject>>> PooledObjects =
        AccessTools.FieldRefAccess<ObjectPool, Dictionary<GameObject, List<GameObject>>>("pooledObjects");

    private static readonly List<PendingSpawn> Pending = new();

    /// <summary>Copies spawned for another player's enemy, with the time they are removed at the latest.</summary>
    private static readonly Dictionary<GameObject, float> Replicas = new();

    private static readonly List<GameObject> ReplicaBuffer = new();

    /// <summary>Whether a prefab can hurt the hero and isn't an enemy itself.</summary>
    private static readonly Dictionary<GameObject, bool> IsProjectileCache = new();

    /// <summary>Prefabs by name in this room.</summary>
    private static readonly Dictionary<string, GameObject> PrefabsByName = new(StringComparer.Ordinal);

    /// <summary>When a prefab that wasn't found may be looked up again.</summary>
    private static readonly Dictionary<string, float> MissingPrefabRetry = new(StringComparer.Ordinal);

    private const float MissingPrefabRetryInterval = 2f;

    private static bool _initialized;

    private static bool Enabled => Config.SyncProjectiles && Config.SyncMovement && Client.IsConnected;

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
        Pending.Clear();
        Replicas.Clear();
        PrefabsByName.Clear();
        MissingPrefabRetry.Clear();
        IsProjectileCache.Clear();
    }

    // ------------------------------------------------------------------ owner side

    /// <summary>Called after every object pool spawn.</summary>
    internal static void OnSpawned(GameObject prefab, GameObject? spawned)
    {
        // Only spawns made by a running FSM can belong to an enemy's attack
        var fsm = FsmExecutionStack.ExecutingFsm;
        if (fsm == null || spawned == null || prefab == null || !Enabled || !IsProjectile(prefab))
        {
            return;
        }

        // The pool calls itself when it has to create a pool first
        foreach (var pending in Pending)
        {
            if (pending.Object == spawned)
            {
                return;
            }
        }

        if (!EnemyTracker.TryGetOwnedSpawner(fsm, out var enemyId, out var root))
        {
            return;
        }

        Pending.Add(new PendingSpawn
        {
            Object = spawned, Prefab = prefab.name, EnemyId = enemyId, EnemyRoot = root, Frame = Time.frameCount
        });
    }

    /// <summary>Remember events the enemy sends a projectile it just spawned, for the copies.</summary>
    internal static void OnFsmEvent(Fsm target, FsmEvent fsmEvent)
    {
        if (Pending.Count == 0)
        {
            return;
        }

        var sender = FsmExecutionStack.ExecutingFsm;
        var component = target.FsmComponent;
        if (sender == null || sender == target || component == null)
        {
            return;
        }

        foreach (var pending in Pending)
        {
            if (pending.Object == component.gameObject && pending.Events.Count < MaxEvents)
            {
                pending.Events.Add((component.FsmName ?? "", fsmEvent.Name ?? ""));
                return;
            }
        }
    }

    private static bool IsProjectile(GameObject prefab)
    {
        if (!IsProjectileCache.TryGetValue(prefab, out var result))
        {
            result = prefab.GetComponentInChildren<DamageHero>(true) != null &&
                     prefab.GetComponentInChildren<HealthManager>(true) == null;
            IsProjectileCache[prefab] = result;
        }

        return result;
    }

    /// <summary>Called late every frame: send spawns once the FSM that made them has finished setting them up.</summary>
    internal static void LateUpdate()
    {
        try
        {
            FlushPending();
            ExpireReplicas();
        }
        catch (Exception e)
        {
            Log.Debug($"Projectile sync failed: {e.Message}");
            Pending.Clear();
        }
    }

    private static void FlushPending()
    {
        for (var i = Pending.Count - 1; i >= 0; i--)
        {
            var pending = Pending[i];
            var obj = pending.Object;
            if (obj == null || !obj.activeInHierarchy || pending.EnemyRoot == null)
            {
                Log.Debug($"Projectile '{pending.Prefab}' of enemy {pending.EnemyId} was gone before it " +
                          "could be sent (broke or was recycled within a frame)");
                Pending.RemoveAt(i);
                continue;
            }

            var body = obj.GetComponent<Rigidbody2D>();
            if (body != null && body.linearVelocity.sqrMagnitude < 0.0001f &&
                Time.frameCount - pending.Frame < MaxVelocityWaitFrames)
            {
                continue;
            }

            Pending.RemoveAt(i);
            Client.SendProjectile(BuildPacket(pending, obj.transform, body));
        }
    }

    private static EnemyProjectilePacket BuildPacket(PendingSpawn pending, Transform t, Rigidbody2D? body)
    {
        var attached = t.parent == pending.EnemyRoot;
        var position = attached ? t.localPosition : t.position;
        var scale = attached ? t.localScale : t.lossyScale;
        var velocity = body != null ? body.linearVelocity : Vector2.zero;
        Log.Debug($"Enemy {pending.EnemyId} spawned projectile '{pending.Prefab}' at {t.position}, velocity {velocity}");

        return new EnemyProjectilePacket
        {
            EnemyId = pending.EnemyId,
            Prefab = pending.Prefab,
            Attached = attached,
            X = position.x, Y = position.y, Z = position.z,
            RotationZ = attached ? t.localEulerAngles.z : t.eulerAngles.z,
            ScaleX = scale.x, ScaleY = scale.y,
            VelocityX = velocity.x, VelocityY = velocity.y,
            Events = pending.Events.ToArray(),
        };
    }

    // ------------------------------------------------------------------ other players

    /// <summary>
    /// A projectile from another player's copy of an enemy. Spawned even if we now run that enemy
    /// ourselves: the owner can change between the throw and its arrival, and dropping it then
    /// would leave the throw on the thrower's game only. At worst a throw at the moment of a
    /// switch shows twice.
    /// </summary>
    internal static void OnRemoteProjectile(EnemyProjectilePacket packet)
    {
        if (!Enabled)
        {
            return;
        }

        var hasRoot = EnemyTracker.TryGetEnemyRoot(packet.EnemyId, out var root);
        if (packet.Attached && !hasRoot)
        {
            Log.Debug($"Skipped projectile '{packet.Prefab}': it is attached to enemy {packet.EnemyId}, " +
                      "which isn't in this room here");
            return;
        }

        var prefab = FindPrefab(packet.Prefab);
        if (prefab == null)
        {
            return;
        }

        try
        {
            Spawn(packet, prefab, root);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to spawn projectile '{packet.Prefab}': {e}");
        }
    }

    private static void Spawn(EnemyProjectilePacket packet, GameObject prefab, Transform? root)
    {
        var position = new Vector3(packet.X, packet.Y, packet.Z);
        var rotation = Quaternion.Euler(0f, 0f, packet.RotationZ);
        var spawned = packet.Attached && root != null
            ? prefab.Spawn(root, position, rotation)
            : prefab.Spawn(position, rotation);
        if (spawned == null)
        {
            return;
        }

        var scale = spawned.transform.localScale;
        spawned.transform.localScale = new Vector3(packet.ScaleX, packet.ScaleY, scale.z);

        var body = spawned.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.linearVelocity = new Vector2(packet.VelocityX, packet.VelocityY);
        }

        Replicas[spawned] = Time.unscaledTime + ReplicaLifetime;

        foreach (var (fsmName, eventName) in packet.Events)
        {
            foreach (var fsm in spawned.GetComponents<PlayMakerFSM>())
            {
                if (fsm.FsmName == fsmName)
                {
                    fsm.SendEvent(eventName);
                }
            }
        }

        Log.Debug($"Spawned projectile '{packet.Prefab}' for enemy {packet.EnemyId}");
    }

    /// <summary>
    /// The prefab with this name. A failed lookup is retried after a short while (searching every
    /// loaded object is too slow to do for every projectile), so one miss doesn't skip that
    /// projectile for the rest of the room.
    /// </summary>
    private static GameObject? FindPrefab(string name)
    {
        if (PrefabsByName.TryGetValue(name, out var cached))
        {
            if (cached != null)
            {
                return cached;
            }

            // Unloaded since
            PrefabsByName.Remove(name);
        }

        var now = Time.unscaledTime;
        if (MissingPrefabRetry.TryGetValue(name, out var retryAt) && now < retryAt)
        {
            return null;
        }

        var found = FindInPool(name) ?? FindLoaded(name);
        if (found == null)
        {
            MissingPrefabRetry[name] = now + MissingPrefabRetryInterval;
            Log.Debug($"Projectile prefab '{name}' isn't loaded here, skipping it (will look again in " +
                      $"{MissingPrefabRetryInterval}s)");
            return null;
        }

        MissingPrefabRetry.Remove(name);
        PrefabsByName[name] = found;
        return found;
    }

    private static GameObject? FindInPool(string name)
    {
        var pool = ObjectPool.instance;
        if (pool == null)
        {
            return null;
        }

        foreach (var prefab in PooledObjects(pool).Keys)
        {
            if (prefab != null && prefab.name == name && prefab.GetComponentInChildren<DamageHero>(true) != null)
            {
                return prefab;
            }
        }

        return null;
    }

    /// <summary>
    /// Prefabs are loaded with the room that references them, and aren't part of any scene. Some
    /// FSMs spawn from a template object placed in the room instead, so those are the fallback.
    /// </summary>
    private static GameObject? FindLoaded(string name)
    {
        GameObject? sceneTemplate = null;
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null || go.name != name || go.GetComponentInChildren<DamageHero>(true) == null)
            {
                continue;
            }

            if (!go.scene.IsValid())
            {
                if (go.transform.parent == null)
                {
                    return go;
                }
            }
            else
            {
                sceneTemplate ??= go;
            }
        }

        return sceneTemplate;
    }

    private static void ExpireReplicas()
    {
        if (Replicas.Count == 0)
        {
            return;
        }

        var now = Time.unscaledTime;
        ReplicaBuffer.Clear();
        ReplicaBuffer.AddRange(Replicas.Keys);
        foreach (var replica in ReplicaBuffer)
        {
            if (replica == null || !replica.activeInHierarchy)
            {
                // Recycled itself
                Replicas.Remove(replica!);
            }
            else if (now >= Replicas[replica])
            {
                Replicas.Remove(replica);
                replica.Recycle();
            }
        }
    }

    /// <summary>Whether a hit comes from a copy; the real projectile's hit arrives from its owner.</summary>
    internal static bool IsReplica(GameObject? source)
    {
        if (source == null || Replicas.Count == 0)
        {
            return false;
        }

        for (var t = source.transform; t != null; t = t.parent)
        {
            if (Replicas.ContainsKey(t.gameObject))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Every pooled spawn ends up in this overload.</summary>
[HarmonyPatch(typeof(ObjectPool), nameof(ObjectPool.Spawn), typeof(GameObject), typeof(Transform), typeof(Vector3),
    typeof(Quaternion), typeof(bool))]
internal static class ObjectPoolSpawnPatch
{
    private static void Postfix(GameObject prefab, GameObject __result)
    {
        try
        {
            EnemyProjectiles.OnSpawned(prefab, __result);
        }
        catch (Exception e)
        {
            Log.Debug($"Projectile spawn hook failed: {e.Message}");
        }
    }
}
