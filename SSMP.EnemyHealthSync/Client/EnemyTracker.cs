using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SSMPEnemyHealthSync.Networking;
using SSMPEnemyHealthSync.Utils;
using Object = UnityEngine.Object;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Gives every enemy (HealthManager) in the current room an ID that is the same on every client,
/// and keeps the enemies of a room in sync between players.
///
/// SSMP does not sync enemies itself (its entity system is disabled in 0.3.1). This addon uses
/// per-enemy ownership: each enemy's AI runs on one player's game (its owner), which streams the
/// enemy's position, facing, animation, HP and hitboxes. The other players' copies become puppets
/// that follow that stream (see <see cref="EnemyMirror"/>). Enemies start out owned by the room
/// host (the first player in the room, chosen by the server); an owner hands an enemy to a player
/// who is clearly closer to it, so enemies chase and attack every player.
/// Damage from any player is sent to the room and applied by the owner; deaths come from the owner.
///
/// With SyncMovement off (or before any state arrives) enemies keep their own AI and only damage
/// and deaths are shared.
/// </summary>
internal static class EnemyTracker
{
    private sealed class TrackedEnemy
    {
        internal uint Id;
        internal HealthManager Hm = null!;
        internal EnemyMirror Mirror = null!;

        /// <summary>Death already handled (sent or applied); never send or apply it again.</summary>
        internal bool KnownDead;

        /// <summary>
        /// Seen alive and active at least once. Enemies that start out dead (e.g. from save data)
        /// never count as a kill.
        /// </summary>
        internal bool SeenAlive;

        /// <summary>A remote death arrived while the enemy was inactive; apply it once it activates.</summary>
        internal bool PendingDeath;

        /// <summary>When HP first reached 0 without isDead being set (bosses with their own death sequence), or -1.</summary>
        internal float ZeroHpSince = -1f;

        /// <summary>Boss-like enemy: handed over more cautiously.</summary>
        internal bool IsBoss;

        /// <summary>We run this enemy's AI and stream its state.</summary>
        internal bool Owned;

        internal float OwnedSince;
        internal float LastHandoverSent = -999f;

        /// <summary>Since when a handover has been waiting for the enemy's attack or burrow to end, or -1.</summary>
        internal float BusySince = -1f;

        /// <summary>Last time we recovered this enemy from walking in place.</summary>
        internal float LastStuckFix = -999f;

        /// <summary>Last time a state packet arrived for this enemy (puppet side).</summary>
        internal float LastStateTime = -999f;
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

    /// <summary>Each tracked enemy's synced FSMs, for routing events that reach a puppet.</summary>
    private static readonly Dictionary<PlayMakerFSM, (TrackedEnemy Enemy, int Index)> FsmOwners = new();

    /// <summary>When each (enemy, FSM, event) was last sent to its owner, to drop repeats.</summary>
    private static readonly Dictionary<(uint, int, string), float> LastFsmEventSent = new();

    private const float FsmEventRepeatInterval = 0.3f;

    /// <summary>
    /// Events HealthManager sends an enemy's FSMs when it is hit or dies. The owner gets these by
    /// playing every hit for real, so a puppet's copies are never forwarded.
    /// </summary>
    private static readonly HashSet<string> LocalOnlyEvents = new(StringComparer.Ordinal)
    {
        "FINISHED", "ATTACK BLOCKED", "BLOCKED DOWN", "BLOCKED EXPLOSION", "BLOCKED HIT L", "BLOCKED HIT R",
        "BLOCKED HIT U", "BLOCKED HIT", "DEALT ACTUAL DAMAGE", "DEALT DAMAGE", "EXTRACT", "FATAL DAMAGE",
        "HIT LANDED", "HIT", "KILLED", "LAVA DEATH", "STUN DAMAGE", "TOOK DAMAGE", "TOOK EXPLOSION DAMAGE",
        "TOOK HEAVY DAMAGE", "TOOK SPELL DAMAGE", "TOOK TAG DAMAGE", "WEAK HIT", "ZERO HP",
    };

    private static float _lastRescanTime = -999f;
    private const float RescanInterval = 1f;

    /// <summary>Unchanged enemies are still re-sent this often, so puppets recover from lost packets.</summary>
    private const float StateHeartbeat = 0.5f;
    private static float _nextStateSendTime;

    /// <summary>
    /// Bosses with a "special death" may let their own FSM play the death without HealthManager
    /// setting isDead. HP staying at 0 this long counts as a death; shorter dips are phase changes
    /// that refill HP.
    /// </summary>
    private const float ZeroHpDeathDelay = 1f;

    /// <summary>A puppet whose owner sent nothing for this long gets its own AI back (owner stopped streaming).</summary>
    private const float PuppetTimeout = 2f;

    private const float HandoverCheckInterval = 0.25f;
    private const float HandoverRetryInterval = 1f;
    private const float HandoverHold = 5f;
    private const float MaxAttackHandoverWait = 3f;
    private const float MaxHiddenHandoverWait = 8f;
    private const float HandoverVerticalWeight = 2f;
    private const float StuckFixInterval = 10f;
    private const float HandoverMargin = 4f;
    private const float BossHandoverHold = 8f;
    private const float BossHandoverMargin = 8f;
    private static float _nextHandoverCheckTime;

    // Ownership of the current room, from the server's RoomOwnership snapshot. We own an enemy when
    // _isRoomHost XOR _ownershipExceptions.Contains(id). Until the first snapshot we own nothing.
    private static bool _ownershipKnown;
    private static bool _isRoomHost;
    private static readonly HashSet<uint> OwnershipExceptions = new();

    private static readonly List<(ushort Id, Vector2 Position)> NearbyPlayers = new();

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
        // Enemies that survive this (e.g. on disconnect) must get their own AI back
        foreach (var tracked in ByHm.Values)
        {
            if (tracked.Hm != null)
            {
                tracked.Mirror.Unpuppetize();
            }
        }

        ByHm.Clear();
        ById.Clear();
        Pending.Clear();
        FsmOwners.Clear();
        LastFsmEventSent.Clear();
        _lastRescanTime = -999f;
        _ownershipKnown = false;
        _isRoomHost = false;
        OwnershipExceptions.Clear();
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

        var tracked = new TrackedEnemy
        {
            Id = id,
            Hm = hm,
            Mirror = new EnemyMirror(hm),
            IsBoss = hm.hasSpecialDeath || hm.bigEnemyDeath || hm.initHp >= Config.BossHpThreshold,
        };
        ByHm[hm] = tracked;
        ById[id] = tracked;

        var fsms = tracked.Mirror.Fsms;
        for (var i = 0; i < fsms.Count; i++)
        {
            if (fsms[i] != null)
            {
                FsmOwners[fsms[i]] = (tracked, i);
            }
        }

        if (OwnsEnemy(id))
        {
            tracked.Owned = true;
            tracked.OwnedSince = Time.unscaledTime;
        }

        if (Pending.TryGetValue(id, out var pending))
        {
            Pending.Remove(id);
            if (pending.Damage > 0)
            {
                ApplyDamageClamped(tracked, pending.Damage);
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

    /// <summary>Whether this enemy is currently a puppet (used by the Die patch).</summary>
    internal static bool IsPuppet(HealthManager hm) =>
        ByHm.TryGetValue(hm, out var tracked) && tracked.Mirror.IsPuppet;

    // ------------------------------------------------------------------ ownership

    private static bool OwnsEnemy(uint id) => _ownershipKnown && (_isRoomHost ^ OwnershipExceptions.Contains(id));

    internal static void OnRoomOwnership(RoomOwnershipPacket packet)
    {
        if (!Active || packet.SceneName != SceneManager.GetActiveScene().name)
        {
            return;
        }

        _ownershipKnown = true;
        _isRoomHost = packet.IsRoomHost;
        OwnershipExceptions.Clear();
        OwnershipExceptions.UnionWith(packet.Exceptions);

        var now = Time.unscaledTime;
        var owned = 0;
        foreach (var tracked in ByHm.Values)
        {
            var nowOwned = OwnsEnemy(tracked.Id);
            if (nowOwned)
            {
                owned++;
            }

            if (nowOwned == tracked.Owned || tracked.Hm == null)
            {
                continue;
            }

            tracked.Owned = nowOwned;
            if (nowOwned)
            {
                // Take over: the enemy runs its own AI again, carrying on from the previous owner's FSM states
                tracked.OwnedSince = now;
                tracked.Mirror.Unpuppetize();
                Log.Debug($"Took over {tracked.Hm.name} ({tracked.Id})");
            }
            else if (Config.SyncMovement && !tracked.KnownDead && !tracked.Hm.isDead)
            {
                // Handed away: stop our AI right away so two AIs never run at once. If the new owner
                // doesn't stream (e.g. SyncMovement off), PuppetTimeout gives it back.
                tracked.LastStateTime = now;
                tracked.Mirror.Puppetize();
                Log.Debug($"Handed over {tracked.Hm.name} ({tracked.Id})");
            }
        }

        Log.Debug($"Ownership in {packet.SceneName}: room host={packet.IsRoomHost}, we own {owned}/{ByHm.Count} enemies");
    }

    internal static void OnRemoteState(EnemyStatePacket packet)
    {
        if (!Active || !Config.SyncMovement || OwnsEnemy(packet.EnemyId))
        {
            return;
        }

        // State is unreliable and a newer one always follows, so don't keep it for unknown enemies
        var tracked = Find(packet.EnemyId, logUnknown: false);
        if (tracked == null || tracked.KnownDead || tracked.Hm == null || tracked.Hm.isDead)
        {
            return;
        }

        tracked.LastStateTime = Time.unscaledTime;
        tracked.Mirror.Apply(packet);
    }

    /// <summary>
    /// For each enemy we own: if another player in the room is clearly closer to it, hand it to
    /// them so it chases and attacks them. Only the owner decides, so two players never claim the
    /// same enemy. Hold time and distance margin prevent flip-flopping; bosses use larger ones.
    /// </summary>
    private static void CheckHandovers(float now)
    {
        if (!Config.SyncMovement || !_ownershipKnown || now < _nextHandoverCheckTime)
        {
            return;
        }

        _nextHandoverCheckTime = now + HandoverCheckInterval;

        // While we are downed our enemies would only stand over us, so hand every one of them to a
        // standing teammate, even with Handover off (otherwise a downed room host freezes the room).
        var downed = DownedState.IsDownedOrPending;
        var canHandover = Config.Handover || downed;

        var hero = HeroController.SilentInstance;
        var players = Client.Api?.ClientManager?.Players;
        if (hero == null || players == null)
        {
            return;
        }

        NearbyPlayers.Clear();
        foreach (var player in players)
        {
            // Never hand an enemy to a downed player
            if (player.IsInLocalScene && player.PlayerObject != null && !DownedState.IsRemoteDowned(player.Id))
            {
                NearbyPlayers.Add((player.Id, player.PlayerObject.transform.position));
            }
        }

        Vector2 heroPos = hero.transform.position;
        var range = downed ? float.MaxValue : Config.HandoverRange;

        foreach (var tracked in ByHm.Values)
        {
            var hm = tracked.Hm;
            if (!tracked.Owned || tracked.KnownDead || hm == null || hm.isDead)
            {
                continue;
            }

            var active = tracked.SeenAlive && hm.gameObject.activeInHierarchy;
            var hold = tracked.IsBoss ? BossHandoverHold : HandoverHold;

            // An enemy walking in place: its movement didn't start up again (e.g. after a takeover).
            // At most one recovery per StuckFixInterval, in case an enemy walks in place on purpose.
            // A hidden enemy (burrowed, dormant) may move or stand still on purpose
            var hidden = tracked.Mirror.IsHidden;
            if (!downed && active && !hidden && tracked.Mirror.CheckStuck(now) &&
                now - tracked.LastStuckFix >= StuckFixInterval)
            {
                tracked.Mirror.ResetStuck(now);
                tracked.LastStuckFix = now;
                var (stuckTarget, stuckDist) = NearestPlayer(hm.transform.position);
                if (!canHandover || NearbyPlayers.Count == 0 || stuckDist > range ||
                    now - tracked.OwnedSince < hold || now - tracked.LastHandoverSent < HandoverRetryInterval)
                {
                    // Just got it (so handing it back would only bounce it), or nobody to give it
                    // to: restart its AI here
                    Log.Debug($"{hm.name} ({tracked.Id}) is stuck walking in place, restarting its AI");
                    tracked.Mirror.RestartAi();
                }
                else
                {
                    tracked.LastHandoverSent = now;
                    Log.Debug($"{hm.name} ({tracked.Id}) is stuck walking in place, handing it to player {stuckTarget}");
                    Client.SendHandover(tracked.Id, stuckTarget);
                }

                continue;
            }

            if (!canHandover || NearbyPlayers.Count == 0 || now - tracked.LastHandoverSent < HandoverRetryInterval)
            {
                continue;
            }

            // Normally only hand over enemies that are actually fighting; when downed, hand over all
            if (!downed && !active)
            {
                continue;
            }

            if (!downed && now - tracked.OwnedSince < hold)
            {
                continue;
            }

            // Handing over mid-attack would cut the attack off; wait until it's done. Same for a hidden
            // enemy (burrowed, dormant, before its arena entrance): its FSM relies on values set
            // before it went under (where to come up), which the new owner doesn't have, so it could
            // end up stuck out of sight. Enemies with always-dangerous parts (e.g. spikes) or that
            // stay hidden would never finish, so the wait is capped. A downed owner still waits for
            // hidden enemies, but not for attacks.
            var attacking = !downed && tracked.Mirror.IsAttacking;
            if (attacking || hidden)
            {
                if (tracked.BusySince < 0f)
                {
                    tracked.BusySince = now;
                }

                if (now - tracked.BusySince < (hidden ? MaxHiddenHandoverWait : MaxAttackHandoverWait))
                {
                    continue;
                }
            }
            else
            {
                tracked.BusySince = -1f;
            }

            Vector2 enemyPos = hm.transform.position;
            var myDist = HandoverDistance(heroPos, enemyPos);
            var (bestId, bestDist) = NearestPlayer(enemyPos);

            var margin = tracked.IsBoss ? BossHandoverMargin : HandoverMargin;
            if (bestDist > range || (!downed && bestDist + margin >= myDist))
            {
                continue;
            }

            tracked.LastHandoverSent = now;
            Log.Debug($"Handing {hm.name} ({tracked.Id}) to player {bestId} ({bestDist:F1} vs our {myDist:F1})");
            Client.SendHandover(tracked.Id, bestId);
        }
    }

    /// <summary>The other player in <see cref="NearbyPlayers"/> nearest to a position, by <see cref="HandoverDistance"/>.</summary>
    private static (ushort Id, float Distance) NearestPlayer(Vector2 enemyPos)
    {
        ushort bestId = 0;
        var bestDist = float.MaxValue;
        foreach (var (id, pos) in NearbyPlayers)
        {
            var dist = HandoverDistance(pos, enemyPos);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestId = id;
            }
        }

        return (bestId, bestDist);
    }

    /// <summary>
    /// Distance for handovers, with vertical distance counting double: most enemies move along the
    /// ground, so a player on a ledge above is harder to reach than one at the same height.
    /// </summary>
    private static float HandoverDistance(Vector2 a, Vector2 b)
    {
        var dx = a.x - b.x;
        var dy = (a.y - b.y) * HandoverVerticalWeight;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // ------------------------------------------------------------------ local events (from patches)

    internal static void OnLocalDamage(HealthManager hm, int damage, float direction, AttackTypes attackType)
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

        // A puppet only shows the hit; the host applies the damage and decides about death
        if (tracked.Mirror.IsPuppet && hm.hp <= 0)
        {
            hm.hp = 1;
        }

        tracked.SeenAlive = true;
        Log.Debug($"Local damage {damage} to {hm.name} ({tracked.Id}), hp now {hm.hp}");
        Client.SendDamage(tracked.Id, damage, direction, (byte) attackType);
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
    /// Called every frame: moves puppets, streams the state of enemies we own, hands enemies over to
    /// closer players, applies deaths that
    /// were waiting for an inactive enemy to activate, and catches deaths that did not go through
    /// HealthManager.Die.
    /// </summary>
    internal static void Update()
    {
        if (ByHm.Count == 0 || !Active)
        {
            return;
        }

        var now = Time.unscaledTime;
        var sendState = _ownershipKnown && Config.SyncMovement && now >= _nextStateSendTime;
        if (sendState)
        {
            _nextStateSendTime = now + 1f / Mathf.Max(1, Config.StateSendRate);
        }

        UpdateBuffer.Clear();
        UpdateBuffer.AddRange(ByHm.Values);
        foreach (var tracked in UpdateBuffer)
        {
            var hm = tracked.Hm;
            if (hm == null)
            {
                continue;
            }

            if (tracked.Mirror.IsPuppet)
            {
                if (now - tracked.LastStateTime > PuppetTimeout)
                {
                    // Nobody is streaming this enemy any more: let it run its own AI again
                    Log.Debug($"No state for {hm.name} ({tracked.Id}) for {PuppetTimeout}s, resuming its own AI");
                    tracked.Mirror.Unpuppetize();
                }
                else
                {
                    tracked.Mirror.Tick(Time.deltaTime);
                }
            }
            else
            {
                tracked.Mirror.TickOwned();
            }

            if (sendState && tracked.Owned && !tracked.KnownDead && !hm.isDead &&
                tracked.Mirror.TryCapture(tracked.Id, now, StateHeartbeat, out var state))
            {
                Client.SendState(state);
            }

            CheckZeroHpDeath(tracked, now);

            if (!hm.gameObject.activeInHierarchy)
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

        CheckHandovers(now);
    }

    /// <summary>
    /// Report a death for enemies that are at 0 HP without isDead (see <see cref="ZeroHpDeathDelay"/>):
    /// after the delay, or right away if the enemy is deactivated while at 0 HP.
    /// Puppets are skipped: their HP comes from the host and never drops to 0.
    /// </summary>
    private static void CheckZeroHpDeath(TrackedEnemy tracked, float now)
    {
        var hm = tracked.Hm;
        if (tracked.KnownDead || tracked.Mirror.IsPuppet || !tracked.SeenAlive || hm.isDead || hm.hp > 0)
        {
            tracked.ZeroHpSince = -1f;
            return;
        }

        if (tracked.ZeroHpSince < 0f)
        {
            tracked.ZeroHpSince = now;
            return;
        }

        if (now - tracked.ZeroHpSince >= ZeroHpDeathDelay || !hm.gameObject.activeInHierarchy)
        {
            Log.Debug($"{hm.name} ({tracked.Id}) stayed at 0 HP without isDead, treating it as dead");
            SendDeathIfNew(tracked);
        }
    }

    // ------------------------------------------------------------------ FSM events

    /// <summary>
    /// Called (from a Harmony patch) for every PlayMaker event delivered to an FSM. Returns false to
    /// drop it. A puppet's FSMs are off, but PlayMaker still runs events through them and would enter
    /// states locally; a puppet only follows its owner, so they are dropped. Events the owner's copy
    /// is waiting for (another enemy calling it in, an arena starting a wave) only fire on the game
    /// that runs the sender, where the receiver may be a puppet: those are sent to the owner.
    /// </summary>
    internal static bool OnFsmEvent(PlayMakerFSM? component, string? eventName)
    {
        if (component == null || !Active || !Config.SyncMovement ||
            !FsmOwners.TryGetValue(component, out var entry))
        {
            return true;
        }

        var tracked = entry.Enemy;
        if (!tracked.Mirror.IsPuppet)
        {
            return true;
        }

        if (string.IsNullOrEmpty(eventName) || LocalOnlyEvents.Contains(eventName!) || tracked.KnownDead ||
            !tracked.Mirror.OwnerStateHandles(entry.Index, eventName!))
        {
            return false;
        }

        var key = (tracked.Id, entry.Index, eventName!);
        var now = Time.unscaledTime;
        if (LastFsmEventSent.TryGetValue(key, out var last) && now - last < FsmEventRepeatInterval)
        {
            return false;
        }

        LastFsmEventSent[key] = now;
        Log.Debug($"Sending event '{eventName}' for {tracked.Hm.name} ({tracked.Id}) FSM {entry.Index} to its owner");
        Client.SendFsmEvent(tracked.Id, entry.Index, eventName!);
        return false;
    }

    internal static void OnRemoteFsmEvent(EnemyFsmEventPacket packet)
    {
        if (!Active || !Config.SyncMovement || !ById.TryGetValue(packet.EnemyId, out var tracked) ||
            tracked.Hm == null || tracked.KnownDead || !tracked.Owned)
        {
            return;
        }

        var delivered = false;
        try
        {
            delivered = tracked.Mirror.DeliverRemoteEvent(packet.FsmIndex, packet.EventName);
        }
        catch (Exception e)
        {
            Log.Error($"Failed to deliver event '{packet.EventName}' to {tracked.Hm.name} ({tracked.Id}): {e}");
        }

        Log.Debug($"Remote event '{packet.EventName}' for {tracked.Hm.name} ({tracked.Id}) FSM {packet.FsmIndex}: " +
                  (delivered ? "delivered" : "ignored (no longer waiting for it)"));
    }

    // ------------------------------------------------------------------ remote events (from packets)

    internal static void OnRemoteDamage(EnemyDamagePacket packet)
    {
        if (!Active || !Config.SyncHealth || packet.Damage <= 0)
        {
            return;
        }

        var tracked = Find(packet.EnemyId, logUnknown: true);
        if (tracked == null)
        {
            GetPending(packet.EnemyId).Damage += packet.Damage;
            return;
        }

        if (tracked.Mirror.IsPuppet)
        {
            // The owner applies it; its HP reaches us in the state stream
            return;
        }

        if (tracked.Owned && Config.SyncMovement)
        {
            ApplyDamageAsHost(tracked, packet);
        }
        else
        {
            ApplyDamageClamped(tracked, packet.Damage);
        }
    }

    internal static void OnRemoteDeath(uint id)
    {
        if (!Active || !Config.SyncDeath)
        {
            return;
        }

        var tracked = Find(id, logUnknown: true);
        if (tracked == null)
        {
            GetPending(id).Dead = true;
            return;
        }

        ApplyDeath(tracked);
    }

    private static TrackedEnemy? Find(uint id, bool logUnknown)
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

        if (logUnknown)
        {
            Log.Debug($"Update for unknown enemy {id}, keeping it until the enemy shows up");
        }

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

    /// <summary>
    /// Damage without a hit reaction, never below 1 HP: the player who landed the killing blow
    /// sends a death packet right after, which plays the proper death.
    /// </summary>
    private static void ApplyDamageClamped(TrackedEnemy tracked, int damage)
    {
        var hm = tracked.Hm;
        if (tracked.KnownDead || hm == null || hm.isDead)
        {
            return;
        }

        hm.hp = Math.Max(1, hm.hp - damage);
        Log.Debug($"Remote damage {damage} to {hm.name} ({tracked.Id}), hp now {hm.hp}");
    }

    /// <summary>
    /// As the enemy's owner, another player's hit is played as a real hit on our enemy, so it flinches,
    /// staggers and dies like it would from our own attack. The puppets then see that reaction.
    /// The HP is corrected afterwards so exactly the attacker's damage is taken.
    /// </summary>
    private static void ApplyDamageAsHost(TrackedEnemy tracked, EnemyDamagePacket packet)
    {
        var hm = tracked.Hm;
        if (tracked.KnownDead || hm == null || hm.isDead)
        {
            return;
        }

        if (!hm.gameObject.activeInHierarchy)
        {
            ApplyDamageClamped(tracked, packet.Damage);
            return;
        }

        var attackType = (AttackTypes) packet.AttackType;
        var targetHp = hm.hp - packet.Damage;

        ApplyingRemote = true;
        try
        {
            var hero = HeroController.SilentInstance != null ? HeroController.SilentInstance.gameObject : null;
            if (hero != null && !float.IsNaN(packet.Direction))
            {
                hm.Hit(new HitInstance
                {
                    Source = hero,
                    AttackType = attackType,
                    DamageDealt = packet.Damage,
                    Direction = packet.Direction,
                    Multiplier = 1f,
                    MagnitudeMultiplier = 1f,
                    SilkGeneration = HitSilkGeneration.None,
                    IsFirstHit = true,
                });
            }

            var hpAfterHit = hm.hp;
            if (!hm.isDead)
            {
                hm.hp = targetHp;
                // If the hit itself already took it to 0, its own death sequence has started
                if (targetHp <= 0 && hpAfterHit > 0)
                {
                    hm.Die(float.IsNaN(packet.Direction) ? null : packet.Direction, attackType, true);
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to apply remote hit to {hm.name} ({tracked.Id}): {e}");
        }
        finally
        {
            ApplyingRemote = false;
        }

        tracked.SeenAlive = true;
        Log.Debug($"Remote hit {packet.Damage} to {hm.name} ({tracked.Id}) as owner, hp now {hm.hp}");

        // The patches stay quiet while ApplyingRemote, so report a kill by another player here
        if (hm.isDead)
        {
            SendDeathIfNew(tracked);
        }
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

        // Give the enemy its FSMs back so its own death sequence can play
        tracked.Mirror.Unpuppetize(resumeOwnerState: false);

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
