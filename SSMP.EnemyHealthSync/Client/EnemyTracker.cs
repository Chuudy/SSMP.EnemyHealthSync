using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SSMPEnemyHealthSync.Utils;
using Object = UnityEngine.Object;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Gives every enemy (HealthManager) in the current room an ID that is the same on every client,
/// sends local damage/deaths, and applies damage/deaths received from other players.
///
/// SSMP does not sync enemies itself (its entity system is disabled in 0.3.1), so every player runs
/// their own copy of each enemy. Enemy positions and AI are therefore not shared; only damage and
/// deaths are.
/// </summary>
internal static class EnemyTracker
{
    private sealed class TrackedEnemy
    {
        internal uint Id;
        internal HealthManager Hm = null!;

        /// <summary>Death already handled (sent or applied); never send or apply it again.</summary>
        internal bool KnownDead;

        /// <summary>
        /// Seen alive and active at least once. Enemies that start out dead (e.g. from save data)
        /// never count as a kill.
        /// </summary>
        internal bool SeenAlive;

        /// <summary>A remote death arrived while the enemy was inactive; apply it once it activates.</summary>
        internal bool PendingDeath;
    }

    private sealed class PendingUpdate
    {
        internal int Damage;
        internal bool Dead;
    }

    private static readonly Dictionary<HealthManager, TrackedEnemy> ByHm = new();
    private static readonly Dictionary<uint, TrackedEnemy> ById = new();

    /// <summary>Updates for IDs we have not found (yet), e.g. enemies spawned later in the fight.</summary>
    private static readonly Dictionary<uint, PendingUpdate> Pending = new();

    private static readonly List<TrackedEnemy> UpdateBuffer = new();

    private static float _lastRescanTime = -999f;
    private const float RescanInterval = 1f;

    private static bool _initialized;

    /// <summary>True while we change an enemy because of network data, so the patches don't echo it back.</summary>
    internal static bool ApplyingRemote { get; private set; }

    private static bool Active => Config.Enabled && Client.IsConnected;

    /// <summary>Whether hits from SSMP's played-back remote attacks should be ignored.</summary>
    internal static bool IgnoreRemoteHits => Active && Config.SyncHealth;

    internal static void Init()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    internal static void OnConnected()
    {
        Clear();
        if (!Config.Enabled)
        {
            return;
        }

        var scene = SceneManager.GetActiveScene();
        ScanRoom(scene);
        Client.SendSceneEntered(scene.name);
    }

    internal static void OnDisconnected() => Clear();

    private static void OnActiveSceneChanged(Scene oldScene, Scene newScene)
    {
        Clear();
        if (!Active)
        {
            return;
        }

        ScanRoom(newScene);
        Client.SendSceneEntered(newScene.name);
    }

    /// <summary>
    /// Additive scenes belonging to the current room (e.g. boss scenes) start with the room's name,
    /// the same rule SSMP uses. Other additive loads are ignored instead of resetting everything.
    /// </summary>
    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Active)
        {
            return;
        }

        var active = SceneManager.GetActiveScene().name;
        if (scene.name == active || !scene.name.StartsWith(active, StringComparison.Ordinal))
        {
            return;
        }

        Log.Debug($"Additive room scene loaded: {scene.name}");
        ScanScene(scene);
    }

    private static void Clear()
    {
        ByHm.Clear();
        ById.Clear();
        Pending.Clear();
        _lastRescanTime = -999f;
    }

    /// <summary>Scan the active scene plus any already-loaded additive scenes of the same room.</summary>
    private static void ScanRoom(Scene activeScene)
    {
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded && scene.name.StartsWith(activeScene.name, StringComparison.Ordinal))
            {
                ScanScene(scene);
            }
        }
    }

    private static void ScanScene(Scene scene)
    {
        var found = 0;
        var all = Object.FindObjectsByType<HealthManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var hm in all)
        {
            if (hm != null && hm.gameObject.scene == scene && !ByHm.ContainsKey(hm))
            {
                Register(hm);
                found++;
            }
        }

        Log.Debug($"Scanned {scene.name}: {found} new enemies, {ByHm.Count} tracked");
    }

    /// <summary>Rescan for enemies spawned after the room loaded, at most once per second.</summary>
    private static void RescanThrottled()
    {
        if (Time.unscaledTime - _lastRescanTime < RescanInterval)
        {
            return;
        }

        _lastRescanTime = Time.unscaledTime;
        ScanRoom(SceneManager.GetActiveScene());
    }

    private static TrackedEnemy? GetOrRegister(HealthManager hm)
    {
        if (ByHm.TryGetValue(hm, out var tracked))
        {
            return tracked;
        }

        // Only enemies that live in a real scene (not prefabs or pool templates)
        return hm.gameObject.scene.IsValid() ? Register(hm) : null;
    }

    private static TrackedEnemy Register(HealthManager hm)
    {
        var id = ComputeId(hm.transform);
        // Two objects with the exact same hierarchy path should not exist, but never overwrite one
        while (ById.ContainsKey(id))
        {
            id++;
        }

        var tracked = new TrackedEnemy { Id = id, Hm = hm };
        ByHm[hm] = tracked;
        ById[id] = tracked;

        if (Pending.TryGetValue(id, out var pending))
        {
            Pending.Remove(id);
            if (pending.Damage > 0)
            {
                ApplyDamage(tracked, pending.Damage);
            }

            if (pending.Dead)
            {
                ApplyDeath(tracked);
            }
        }

        return tracked;
    }

    /// <summary>
    /// ID from the scene name and the object's full hierarchy path including sibling indices. This
    /// does not depend on where the enemy currently is, so clients that load the room at different
    /// times still agree. Enemies spawned during a fight are best effort: their path can differ
    /// between clients if they spawn in a different order.
    /// </summary>
    private static uint ComputeId(Transform transform)
    {
        var hash = 2166136261u;

        void AddChar(int c)
        {
            unchecked
            {
                hash ^= (uint) c;
                hash *= 16777619u;
            }
        }

        void Add(string s)
        {
            foreach (var c in s)
            {
                AddChar(c);
            }
        }

        void AddInt(int value)
        {
            for (var i = 0; i < 4; i++)
            {
                AddChar((value >> (i * 8)) & 0xFF);
            }
        }

        Add(transform.gameObject.scene.name);

        var path = new List<Transform>();
        for (var t = transform; t != null; t = t.parent)
        {
            path.Add(t);
        }

        for (var i = path.Count - 1; i >= 0; i--)
        {
            AddChar('/');
            Add(path[i].name);
            AddInt(path[i].GetSiblingIndex());
        }

        return hash;
    }

    // ------------------------------------------------------------------ local events (from patches)

    internal static void OnLocalDamage(HealthManager hm, int damage)
    {
        if (!Active || !Config.SyncHealth || ApplyingRemote || damage <= 0)
        {
            return;
        }

        var tracked = GetOrRegister(hm);
        if (tracked == null || tracked.KnownDead)
        {
            return;
        }

        tracked.SeenAlive = true;
        Log.Debug($"Local damage {damage} to {hm.name} ({tracked.Id}), hp now {hm.hp}");
        Client.SendDamage(tracked.Id, damage);
    }

    internal static void OnLocalDeath(HealthManager hm)
    {
        if (!Active || ApplyingRemote)
        {
            return;
        }

        var tracked = GetOrRegister(hm);
        if (tracked != null)
        {
            SendDeathIfNew(tracked);
        }
    }

    private static void SendDeathIfNew(TrackedEnemy tracked)
    {
        if (tracked.KnownDead || !tracked.SeenAlive)
        {
            return;
        }

        tracked.KnownDead = true;
        if (Config.SyncDeath)
        {
            Log.Debug($"Local death of {tracked.Hm.name} ({tracked.Id})");
            Client.SendDeath(tracked.Id);
        }
    }

    /// <summary>
    /// Called every frame: applies deaths that were waiting for an inactive enemy to activate, and
    /// catches deaths that did not go through HealthManager.Die.
    /// </summary>
    internal static void Update()
    {
        if (ByHm.Count == 0 || !Active)
        {
            return;
        }

        UpdateBuffer.Clear();
        UpdateBuffer.AddRange(ByHm.Values);
        foreach (var tracked in UpdateBuffer)
        {
            var hm = tracked.Hm;
            if (hm == null || !hm.gameObject.activeInHierarchy)
            {
                continue;
            }

            if (tracked.PendingDeath)
            {
                ApplyDeath(tracked);
                continue;
            }

            if (hm.isDead)
            {
                SendDeathIfNew(tracked);
            }
            else if (!tracked.SeenAlive && hm.hp > 0)
            {
                tracked.SeenAlive = true;
            }
        }
    }

    // ------------------------------------------------------------------ remote events (from packets)

    internal static void OnRemoteDamage(uint id, int damage)
    {
        if (!Active || !Config.SyncHealth || damage <= 0)
        {
            return;
        }

        var tracked = Find(id);
        if (tracked == null)
        {
            GetPending(id).Damage += damage;
            return;
        }

        ApplyDamage(tracked, damage);
    }

    internal static void OnRemoteDeath(uint id)
    {
        if (!Active || !Config.SyncDeath)
        {
            return;
        }

        var tracked = Find(id);
        if (tracked == null)
        {
            GetPending(id).Dead = true;
            return;
        }

        ApplyDeath(tracked);
    }

    private static TrackedEnemy? Find(uint id)
    {
        if (ById.TryGetValue(id, out var tracked))
        {
            return tracked;
        }

        RescanThrottled();
        if (ById.TryGetValue(id, out tracked))
        {
            return tracked;
        }

        Log.Debug($"Update for unknown enemy {id}, keeping it until the enemy shows up");
        return null;
    }

    private static PendingUpdate GetPending(uint id)
    {
        if (!Pending.TryGetValue(id, out var pending))
        {
            pending = new PendingUpdate();
            Pending[id] = pending;
        }

        return pending;
    }

    private static void ApplyDamage(TrackedEnemy tracked, int damage)
    {
        var hm = tracked.Hm;
        if (tracked.KnownDead || hm == null || hm.isDead)
        {
            return;
        }

        // Never kill from a damage packet: the player who landed the killing blow sends a death
        // packet right after, which plays the proper death.
        hm.hp = Math.Max(1, hm.hp - damage);
        Log.Debug($"Remote damage {damage} to {hm.name} ({tracked.Id}), hp now {hm.hp}");
    }

    private static void ApplyDeath(TrackedEnemy tracked)
    {
        tracked.KnownDead = true;

        var hm = tracked.Hm;
        if (hm == null || hm.isDead)
        {
            tracked.PendingDeath = false;
            return;
        }

        // Die() on an inactive enemy (e.g. an arena wave that hasn't started) is unsafe; wait for it.
        if (!hm.gameObject.activeInHierarchy)
        {
            tracked.PendingDeath = true;
            return;
        }

        tracked.PendingDeath = false;
        ApplyingRemote = true;
        try
        {
            hm.hp = 0;
            hm.Die(null, AttackTypes.Generic, true);
            Log.Debug($"Remote death of {hm.name} ({tracked.Id})");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to kill {hm.name} ({tracked.Id}): {e}");
        }
        finally
        {
            ApplyingRemote = false;
        }
    }
}
