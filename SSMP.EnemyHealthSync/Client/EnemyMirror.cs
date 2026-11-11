using System;
using System.Collections.Generic;
using System.Linq;
using SSMPEnemyHealthSync.Networking;
using UnityEngine;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// The movement/animation/hitbox side of one enemy.
/// On its owner it captures <see cref="EnemyStatePacket"/>s. On other players it turns the enemy
/// into a puppet: AI switched off, following the host's snapshots instead.
/// </summary>
internal sealed class EnemyMirror
{
    private const int MaxHitboxes = 32;
    private const float SnapDistance = 3f;
    private const float Smoothing = 15f;
    private const float PositionEpsilon = 0.01f;

    private readonly HealthManager _hm;
    private readonly GameObject _go;
    private readonly tk2dSpriteAnimator? _animator;
    private readonly Dictionary<tk2dSpriteAnimationClip, ushort> _clipIds = new();

    // Index-aligned: hitbox i is _hitboxObjects[i] with optional collider _hitboxColliders[i] and
    // optional contact damage _hitboxDamagers[i]
    private readonly List<GameObject> _hitboxObjects = new();
    private readonly List<Collider2D?> _hitboxColliders = new();
    private readonly List<DamageHero?> _hitboxDamagers = new();

    /// <summary>The enemy's own renderer; FSMs switch it off while the enemy is burrowed or dormant.</summary>
    private readonly Renderer? _renderer;

    /// <summary>The enemy's tk2d sprite; FSMs fade it in and out through its colour.</summary>
    private readonly tk2dBaseSprite? _sprite;

    // Index-aligned: visual object i (effects, dust/particle emitters below the enemy) is
    // _visualObjects[i] with optional renderer and particle system
    private const int MaxVisuals = 32;
    private readonly List<GameObject> _visualObjects = new();
    private readonly List<Renderer?> _visualRenderers = new();
    private readonly List<ParticleSystem?> _visualParticles = new();

    /// <summary>Visual objects that are also hitboxes: their active state is synced as a hitbox.</summary>
    private readonly List<bool> _visualIsHitbox = new();

    // Puppet side: the owner's visual bits from the previous snapshot. Visuals are applied when the
    // owner's bit changes, because effects switch themselves off when their animation ends.
    private uint _prevVisualActive, _prevVisualRenderer, _prevVisualParticles;
    private bool _visualsSynced;

    /// <summary>Puppet side: whether the owner's body was kinematic in its latest snapshot, or null before one arrived.</summary>
    private bool? _ownerKinematic;

    /// <summary>Puppet side: whether the owner's enemy was visible, and its sprite colour, in the latest snapshot.</summary>
    private bool? _ownerVisible;
    private uint? _ownerSpriteColor;

    /// <summary>Puppet side: a damager's damage before we set it to 0, to give it back when the owner's is on again.</summary>
    private readonly Dictionary<DamageHero, int> _savedDamage = new();

    // What Puppetize changed, so Unpuppetize can restore it exactly
    private readonly List<Behaviour> _disabledBehaviours = new();
    private Rigidbody2D? _body;
    private RigidbodyType2D _bodyType;

    /// <summary>The enemy's FSMs in hierarchy order; FSM i means the same FSM on every client.</summary>
    private readonly PlayMakerFSM[] _fsms;

    /// <summary>Puppet side: the owner's latest FSM states, resumed from when we take over.</summary>
    private ushort[] _ownerFsmStates = [];

    /// <summary>Puppet side: the owner's latest animation clip ID.</summary>
    private ushort _ownerClip = EnemyStatePacket.NoClip;

    /// <summary>Puppet side: the owner's latest hitbox collider / contact damage bits.</summary>
    private uint _ownerColliderEnabled;
    private uint _ownerDamaging;

    /// <summary>Each damager's damage as the enemy was loaded, to give back when it is 0.</summary>
    private readonly Dictionary<DamageHero, int> _initialDamage = new();

    // Puppet side: the owner's velocity, estimated from the positions in its snapshots. Movement
    // scripts (Walker, ...) and FSM states often set the velocity only once when a walk starts; a
    // puppet's is zeroed, so a new owner gives it back or the enemy walks in place.
    private Vector2 _lastSnapshotPos;
    private float _lastSnapshotTime = -1f;
    private Vector2 _ownerVelocity;
    private const float MaxVelocitySampleGap = 0.6f;

    // Owner side: stuck detection (trying to move, but not moving)
    private Vector2 _stuckRefPos;
    private float _stuckSince = -1f;
    private const float StuckMoveThreshold = 0.15f;
    private const float StuckSeconds = 1.5f;

    /// <summary>
    /// After taking over, the FSMs restart (which sets up their variables); on the next frame they
    /// jump to the owner's states. The frame to do that on, or negative when nothing is pending.
    /// Until then no snapshots are sent, so nobody sees the restarted (often asleep) enemy.
    /// </summary>
    private int _resumeFsmFrame = -1;

    /// <summary>
    /// Frames a restarted FSM gets to run its own setup (finding its children, choosing its role
    /// from its animation, e.g. "calls a flying Skarr when it dies") before jumping to the owner's
    /// states. Start states usually wait a frame before moving on.
    /// </summary>
    private const int ResumeSetupFrames = 3;

    /// <summary>Puppet side: the owner's latest FSM variable values, or null before any arrived.</summary>
    private FsmVarSet?[]? _ownerVars;

    // Owner side: FSM variables sent at most this often, and only when they changed
    private const float VarsInterval = 0.5f;
    private float _lastVarsSent = -999f;
    private uint _lastVarsHash;

    private Vector2 _target;
    private bool _hasTarget;

    // Last snapshot sent (host side), for change detection
    private EnemyStatePacket? _lastSent;
    private float _lastSentTime;

    internal bool IsPuppet { get; private set; }

    internal EnemyMirror(HealthManager hm)
    {
        _hm = hm;
        _go = hm.gameObject;
        _animator = _go.GetComponent<tk2dSpriteAnimator>();

        var clips = _animator != null && _animator.Library != null ? _animator.Library.clips : null;
        if (clips != null)
        {
            for (var i = 0; i < clips.Length && i < EnemyStatePacket.NoClip; i++)
            {
                if (clips[i] != null && !_clipIds.ContainsKey(clips[i]))
                {
                    _clipIds[clips[i]] = (ushort) i;
                }
            }
        }

        CollectHitboxes();
        CollectVisuals();
        _renderer = _go.GetComponent<Renderer>();
        _sprite = _go.GetComponent<tk2dBaseSprite>();
        _fsms = _go.GetComponentsInChildren<PlayMakerFSM>(true).Take(EnemyStatePacket.MaxFsms).ToArray();
        _body = _go.GetComponent<Rigidbody2D>();
    }

    /// <summary>
    /// Visual objects: descendants (not the enemy itself) with a renderer or particle system, such
    /// as dust that warns of an attack from underground or emerge/slam effects. Their FSMs only run
    /// on the owner, so puppets copy whether they are on.
    /// </summary>
    private void CollectVisuals()
    {
        var objects = new HashSet<GameObject>();
        foreach (var r in _go.GetComponentsInChildren<Renderer>(true))
        {
            if (r.gameObject != _go)
            {
                objects.Add(r.gameObject);
            }
        }

        foreach (var ps in _go.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (ps.gameObject != _go)
            {
                objects.Add(ps.gameObject);
            }
        }

        var ordered = objects
            .Select(o => (Path: RelativePath(o.transform), Object: o))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count > MaxVisuals)
        {
            Utils.Log.Debug($"{_go.name} has {ordered.Count} visual objects, only the first {MaxVisuals} are synced");
        }

        foreach (var (_, obj) in ordered.Take(MaxVisuals))
        {
            _visualObjects.Add(obj);
            _visualRenderers.Add(obj.GetComponent<Renderer>());
            _visualParticles.Add(obj.GetComponent<ParticleSystem>());
            _visualIsHitbox.Add(_hitboxObjects.Contains(obj));
        }
    }

    /// <summary>The enemy's synced FSMs, by index.</summary>
    internal IReadOnlyList<PlayMakerFSM> Fsms => _fsms;

    /// <summary>
    /// Puppet side: whether the owner's copy of FSM <paramref name="index"/>, in the state the owner
    /// last reported, reacts to <paramref name="eventName"/> (a transition of that state, or a
    /// global transition). Only those events are worth sending to the owner.
    /// </summary>
    internal bool OwnerStateHandles(int index, string eventName)
    {
        if (index < 0 || index >= _fsms.Length || index >= _ownerFsmStates.Length)
        {
            return false;
        }

        var fsm = _fsms[index];
        var stateIndex = _ownerFsmStates[index];
        var states = fsm != null ? fsm.FsmStates : null;
        if (states == null || stateIndex == EnemyStatePacket.NoFsmState || stateIndex >= states.Length)
        {
            return false;
        }

        return Handles(states[stateIndex]?.Transitions, eventName) || Handles(fsm!.FsmGlobalTransitions, eventName);
    }

    /// <summary>
    /// Owner side: deliver an event that reached another player's puppet of this enemy, if our
    /// FSM is still in a state that reacts to it (it may have moved on, or got it itself).
    /// </summary>
    internal bool DeliverRemoteEvent(int index, string eventName)
    {
        if (IsPuppet || index < 0 || index >= _fsms.Length)
        {
            return false;
        }

        var fsm = _fsms[index];
        if (fsm == null || !fsm.enabled || fsm.Fsm == null)
        {
            return false;
        }

        var active = fsm.Fsm.ActiveState;
        if (!Handles(active?.Transitions, eventName) && !Handles(fsm.FsmGlobalTransitions, eventName))
        {
            return false;
        }

        fsm.SendEvent(eventName);
        return true;
    }

    private static bool Handles(HutongGames.PlayMaker.FsmTransition[]? transitions, string eventName)
    {
        if (transitions == null)
        {
            return false;
        }

        foreach (var t in transitions)
        {
            if (t != null && t.EventName == eventName)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ FSM variables

    /// <summary>
    /// Owner side: the enemy's FSM variables, when they changed and <see cref="VarsInterval"/> has
    /// passed (or always with <paramref name="force"/>, e.g. right before a handover).
    /// </summary>
    internal bool TryCaptureVars(uint id, float now, bool force, out EnemyFsmVarsPacket packet)
    {
        packet = null!;
        if (_fsms.Length == 0 || _resumeFsmFrame >= 0 || (!force && now - _lastVarsSent < VarsInterval))
        {
            return false;
        }

        var sets = new FsmVarSet?[_fsms.Length];
        var hash = 2166136261u;
        for (var i = 0; i < _fsms.Length; i++)
        {
            sets[i] = CaptureVars(_fsms[i], ref hash);
        }

        if (!force && hash == _lastVarsHash)
        {
            return false;
        }

        _lastVarsHash = hash;
        _lastVarsSent = now;
        packet = new EnemyFsmVarsPacket { EnemyId = id, Fsms = sets };
        return true;
    }

    private static FsmVarSet? CaptureVars(PlayMakerFSM? fsm, ref uint hash)
    {
        var vars = fsm != null ? fsm.FsmVariables : null;
        if (vars == null)
        {
            return null;
        }

        var set = new FsmVarSet
        {
            Bools = vars.BoolVariables.Select(v => v.Value).ToArray(),
            Ints = vars.IntVariables.Select(v => v.Value).ToArray(),
            Floats = vars.FloatVariables.Select(v => v.Value).ToArray(),
            Strings = vars.StringVariables.Select(v => v.Value ?? "").ToArray(),
            Vector2s = vars.Vector2Variables.SelectMany(v => new[] { v.Value.x, v.Value.y }).ToArray(),
            Vector3s = vars.Vector3Variables.SelectMany(v => new[] { v.Value.x, v.Value.y, v.Value.z }).ToArray(),
        };

        unchecked
        {
            foreach (var b in set.Bools) hash = (hash ^ (b ? 1u : 0u)) * 16777619u;
            foreach (var n in set.Ints) hash = (hash ^ (uint) n) * 16777619u;
            foreach (var f in set.Floats) hash = (hash ^ (uint) BitConverter.SingleToInt32Bits(f)) * 16777619u;
            foreach (var s in set.Strings) hash = (hash ^ (uint) s.GetHashCode()) * 16777619u;
            foreach (var f in set.Vector2s) hash = (hash ^ (uint) BitConverter.SingleToInt32Bits(f)) * 16777619u;
            foreach (var f in set.Vector3s) hash = (hash ^ (uint) BitConverter.SingleToInt32Bits(f)) * 16777619u;
        }

        return set;
    }

    /// <summary>
    /// Before a death from the owner: start FSMs that never ran (a puppet's may not have), so their
    /// start state runs now and not when the death event arrives, which would reset variables
    /// (e.g. "call another enemy when dying") after the owner's were applied. Then apply those.
    /// </summary>
    internal void PrepareForRemoteDeath()
    {
        if (_go.activeInHierarchy)
        {
            foreach (var fsm in _fsms)
            {
                try
                {
                    if (fsm != null && fsm.enabled && fsm.Fsm != null && !fsm.Fsm.Started)
                    {
                        fsm.Fsm.Start();
                    }
                }
                catch (Exception e)
                {
                    Utils.Log.Debug($"{_go.name}: could not start FSM '{fsm!.FsmName}': {e.Message}");
                }
            }
        }

        ApplyOwnerVars();
    }

    /// <summary>Puppet side: remember the owner's FSM variables, for when we take the enemy over.</summary>
    internal void SetOwnerVars(FsmVarSet?[] sets) => _ownerVars = sets;

    /// <summary>
    /// Give our FSMs the owner's variable values (where the variable lists match). Object references
    /// are not touched: our own FSM setup finds our own children and hero.
    /// </summary>
    internal void ApplyOwnerVars()
    {
        if (_ownerVars == null)
        {
            return;
        }

        var applied = 0;
        for (var i = 0; i < _fsms.Length && i < _ownerVars.Length; i++)
        {
            var set = _ownerVars[i];
            var vars = _fsms[i] != null ? _fsms[i].FsmVariables : null;
            if (set == null || vars == null)
            {
                continue;
            }

            try
            {
                var bools = vars.BoolVariables;
                if (bools.Length == set.Bools.Length)
                {
                    for (var v = 0; v < bools.Length; v++) bools[v].Value = set.Bools[v];
                }

                var ints = vars.IntVariables;
                if (ints.Length == set.Ints.Length)
                {
                    for (var v = 0; v < ints.Length; v++) ints[v].Value = set.Ints[v];
                }

                var floats = vars.FloatVariables;
                if (floats.Length == set.Floats.Length)
                {
                    for (var v = 0; v < floats.Length; v++) floats[v].Value = set.Floats[v];
                }

                var strings = vars.StringVariables;
                if (strings.Length == set.Strings.Length)
                {
                    for (var v = 0; v < strings.Length; v++) strings[v].Value = set.Strings[v];
                }

                var v2 = vars.Vector2Variables;
                if (v2.Length * 2 == set.Vector2s.Length)
                {
                    for (var v = 0; v < v2.Length; v++)
                    {
                        v2[v].Value = new Vector2(set.Vector2s[v * 2], set.Vector2s[v * 2 + 1]);
                    }
                }

                var v3 = vars.Vector3Variables;
                if (v3.Length * 3 == set.Vector3s.Length)
                {
                    for (var v = 0; v < v3.Length; v++)
                    {
                        v3[v].Value = new Vector3(set.Vector3s[v * 3], set.Vector3s[v * 3 + 1], set.Vector3s[v * 3 + 2]);
                    }
                }

                applied++;
            }
            catch (Exception e)
            {
                Utils.Log.Debug($"{_go.name}: could not apply the owner's variables to FSM '{_fsms[i].FsmName}': {e.Message}");
            }
        }

        if (applied > 0)
        {
            Utils.Log.Debug($"{_go.name}: applied the owner's variables to {applied} FSM(s)");
        }
    }

    /// <summary>Owner side: the enemy is hidden (burrowed, dormant, waiting for its arena wave).</summary>
    internal bool IsHidden => _renderer != null && !_renderer.enabled;

    /// <summary>
    /// Attack hitboxes: the enemy and its descendants that have a DamageHero or Collider2D, in a
    /// deterministic order (hierarchy path), so the bit index means the same object on every client.
    /// </summary>
    private void CollectHitboxes()
    {
        var objects = new HashSet<GameObject>();
        foreach (var c in _go.GetComponentsInChildren<Collider2D>(true))
        {
            objects.Add(c.gameObject);
        }

        foreach (var d in _go.GetComponentsInChildren<DamageHero>(true))
        {
            objects.Add(d.gameObject);
        }

        var ordered = objects
            .Select(o => (Path: RelativePath(o.transform), Object: o))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count > MaxHitboxes)
        {
            Utils.Log.Debug($"{_go.name} has {ordered.Count} hitboxes, only the first {MaxHitboxes} are synced");
        }

        foreach (var (_, obj) in ordered.Take(MaxHitboxes))
        {
            _hitboxObjects.Add(obj);
            _hitboxColliders.Add(obj.GetComponent<Collider2D>());
            var damager = obj.GetComponent<DamageHero>();
            _hitboxDamagers.Add(damager);
            if (damager != null && damager.damageDealt > 0)
            {
                _initialDamage[damager] = damager.damageDealt;
            }
        }
    }

    private string RelativePath(Transform t)
    {
        var parts = new List<string>();
        for (; t != null && t != _go.transform; t = t.parent)
        {
            parts.Add($"{t.name}#{t.GetSiblingIndex():D4}");
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    // ------------------------------------------------------------------ host side

    /// <summary>
    /// Capture the current state. Returns false when nothing changed and the heartbeat interval
    /// has not passed, so idle enemies cost (almost) no bandwidth.
    /// </summary>
    internal bool TryCapture(uint id, float now, float heartbeat, out EnemyStatePacket packet)
    {
        if (_resumeFsmFrame >= 0)
        {
            // Just took over: the FSMs are in their start state until they resume the owner's
            packet = null!;
            return false;
        }

        var pos = _go.transform.position;
        packet = new EnemyStatePacket
        {
            EnemyId = id,
            X = pos.x,
            Y = pos.y,
            ScaleXNegative = _go.transform.localScale.x < 0,
            Active = _go.activeSelf,
            Clip = CurrentClipId(),
            Hp = _hm.hp,
        };

        uint active = 0, colliderEnabled = 0, damaging = 0;
        for (var i = 0; i < _hitboxObjects.Count; i++)
        {
            var obj = _hitboxObjects[i];
            if (obj != null && obj.activeSelf)
            {
                active |= 1u << i;
            }

            var col = _hitboxColliders[i];
            if (col != null && col.enabled)
            {
                colliderEnabled |= 1u << i;
            }

            var damager = _hitboxDamagers[i];
            if (damager != null && damager.enabled && damager.damageDealt > 0)
            {
                damaging |= 1u << i;
            }
        }

        packet.HitboxActive = active;
        packet.HitboxColliderEnabled = colliderEnabled;
        packet.HitboxDamaging = damaging;
        packet.Visible = _renderer == null || _renderer.enabled;
        packet.Kinematic = _body != null && _body.bodyType == RigidbodyType2D.Kinematic;
        packet.SpriteColor = _sprite != null ? PackColor(_sprite.color) : 0xFFFFFFFF;

        uint visualActive = 0, visualRenderer = 0, visualParticles = 0;
        for (var i = 0; i < _visualObjects.Count; i++)
        {
            var obj = _visualObjects[i];
            if (obj == null)
            {
                continue;
            }

            if (obj.activeSelf)
            {
                visualActive |= 1u << i;
            }

            var r = _visualRenderers[i];
            if (r != null && r.enabled)
            {
                visualRenderer |= 1u << i;
            }

            // Emit() bursts don't set isEmitting, so live particles count too
            var ps = _visualParticles[i];
            if (ps != null && obj.activeInHierarchy && (ps.isEmitting || ps.particleCount > 0))
            {
                visualParticles |= 1u << i;
            }
        }

        packet.VisualActive = visualActive;
        packet.VisualRenderer = visualRenderer;
        packet.VisualParticles = visualParticles;
        packet.FsmStates = CaptureFsmStates();

        if (_lastSent != null && now - _lastSentTime < heartbeat && SameState(_lastSent, packet))
        {
            return false;
        }

        _lastSent = packet;
        _lastSentTime = now;
        return true;
    }

    private static bool SameState(EnemyStatePacket a, EnemyStatePacket b) =>
        Mathf.Abs(a.X - b.X) < PositionEpsilon && Mathf.Abs(a.Y - b.Y) < PositionEpsilon &&
        a.ScaleXNegative == b.ScaleXNegative && a.Active == b.Active && a.Clip == b.Clip && a.Hp == b.Hp &&
        a.HitboxActive == b.HitboxActive && a.HitboxColliderEnabled == b.HitboxColliderEnabled &&
        a.HitboxDamaging == b.HitboxDamaging && a.Visible == b.Visible && a.Kinematic == b.Kinematic &&
        a.VisualActive == b.VisualActive && a.VisualRenderer == b.VisualRenderer &&
        a.VisualParticles == b.VisualParticles && a.SpriteColor == b.SpriteColor &&
        a.FsmStates.AsSpan().SequenceEqual(b.FsmStates);

    private ushort[] CaptureFsmStates()
    {
        if (_fsms.Length == 0)
        {
            return [];
        }

        var states = new ushort[_fsms.Length];
        for (var i = 0; i < _fsms.Length; i++)
        {
            states[i] = ActiveStateIndex(_fsms[i]);
        }

        return states;
    }

    private static ushort ActiveStateIndex(PlayMakerFSM? fsm)
    {
        if (fsm == null)
        {
            return EnemyStatePacket.NoFsmState;
        }

        var name = fsm.ActiveStateName;
        var states = fsm.FsmStates;
        if (string.IsNullOrEmpty(name) || states == null)
        {
            return EnemyStatePacket.NoFsmState;
        }

        for (var i = 0; i < states.Length && i < EnemyStatePacket.NoFsmState; i++)
        {
            if (states[i] != null && states[i].Name == name)
            {
                return (ushort) i;
            }
        }

        return EnemyStatePacket.NoFsmState;
    }

    /// <summary>
    /// Move each FSM to the owner's last known state, so the AI carries on with what the enemy was
    /// doing (chasing, attacking) instead of starting over (often asleep or idle).
    /// </summary>
    private void ResumeOwnerFsmStates()
    {
        for (var i = 0; i < _fsms.Length && i < _ownerFsmStates.Length; i++)
        {
            var fsm = _fsms[i];
            var index = _ownerFsmStates[i];
            if (fsm == null || !fsm.enabled || index == EnemyStatePacket.NoFsmState)
            {
                continue;
            }

            var states = fsm.FsmStates;
            if (states == null || index >= states.Length || states[index] == null)
            {
                continue;
            }

            var name = states[index].Name;
            if (fsm.ActiveStateName == name)
            {
                continue;
            }

            try
            {
                fsm.SetState(name);
            }
            catch (Exception e)
            {
                Utils.Log.Debug($"Could not resume {_go.name} FSM '{fsm.FsmName}' in state '{name}': {e.Message}");
            }
        }
    }

    /// <summary>Called every frame while we own the enemy.</summary>
    internal void TickOwned()
    {
        if (_resumeFsmFrame < 0 || Time.frameCount < _resumeFsmFrame)
        {
            return;
        }

        _resumeFsmFrame = -1;
        var restartClip = _animator != null ? _animator.CurrentClip : null;
        ApplyOwnerVars();
        ResumeOwnerFsmStates();
        FixRestartClip(restartClip);
        RestoreOwnerContact();
        RestoreOwnerLook();

        // The restart or the resumed state may have stopped the body; carry on at the owner's speed
        if (_body != null && _body.bodyType == RigidbodyType2D.Dynamic &&
            _body.linearVelocity.sqrMagnitude < 0.0025f && _ownerVelocity.sqrMagnitude > 0.04f)
        {
            _body.linearVelocity = _ownerVelocity;
            Utils.Log.Debug($"{_go.name}: gave back the owner's velocity {_ownerVelocity} after resuming");
        }
    }

    /// <summary>
    /// Owner side, called a few times per second: whether the enemy has been trying to move
    /// (horizontal velocity or a walk/run animation) without actually moving for a while.
    /// That happens when its movement didn't start up again after a takeover.
    /// </summary>
    internal bool CheckStuck(float now)
    {
        Vector2 pos = _go.transform.position;
        var tryingToMove = !IsAttacking &&
                           ((_body != null && Mathf.Abs(_body.linearVelocity.x) > 0.1f) || IsMoveClipPlaying());

        if (!tryingToMove || _stuckSince < 0f || (pos - _stuckRefPos).sqrMagnitude > StuckMoveThreshold * StuckMoveThreshold)
        {
            ResetStuck(now);
            return false;
        }

        return now - _stuckSince >= StuckSeconds;
    }

    internal void ResetStuck(float now)
    {
        _stuckRefPos = _go.transform.position;
        _stuckSince = now;
    }

    private bool IsMoveClipPlaying()
    {
        var clip = _animator != null && _animator.Playing ? _animator.CurrentClip : null;
        if (clip == null || string.IsNullOrEmpty(clip.name))
        {
            return false;
        }

        return clip.name.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0 ||
               clip.name.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Owner side recovery for a stuck enemy: send its FSMs back to their start states, as if the
    /// enemy had just been loaded, so they set up and start its movement again.
    /// </summary>
    internal void RestartAi()
    {
        foreach (var fsm in _fsms)
        {
            var start = fsm != null && fsm.enabled && fsm.Fsm != null ? fsm.Fsm.StartState : null;
            if (string.IsNullOrEmpty(start))
            {
                continue;
            }

            try
            {
                fsm!.SetState(start);
            }
            catch (Exception e)
            {
                Utils.Log.Debug($"Could not restart {_go.name} FSM '{fsm!.FsmName}': {e.Message}");
            }
        }
    }

    /// <summary>
    /// The restart runs the FSMs' start state, which for sleeping enemies switches the body's
    /// collider or contact damage off; jumping to the owner's state skips the wake-up step that
    /// switches them back on, and the enemy would chase the player without being able to touch
    /// them. Switch back on what the owner had on. Only switches things on, and only on objects
    /// that are active, so attack hitboxes are never turned on outside their attack.
    /// </summary>
    private void RestoreOwnerContact()
    {
        for (var i = 0; i < _hitboxObjects.Count; i++)
        {
            var obj = _hitboxObjects[i];
            if (obj == null || !obj.activeInHierarchy)
            {
                continue;
            }

            var col = _hitboxColliders[i];
            if (col != null && !col.enabled && (_ownerColliderEnabled & (1u << i)) != 0)
            {
                col.enabled = true;
                Utils.Log.Debug($"{_go.name}: re-enabled collider on '{obj.name}' after resuming");
            }

            var damager = _hitboxDamagers[i];
            if (damager == null || (_ownerDamaging & (1u << i)) == 0)
            {
                continue;
            }

            if (!damager.enabled)
            {
                damager.enabled = true;
                Utils.Log.Debug($"{_go.name}: re-enabled contact damage on '{obj.name}' after resuming");
            }

            if (damager.damageDealt <= 0)
            {
                damager.damageDealt = _initialDamage.TryGetValue(damager, out var initial) ? initial : 1;
                Utils.Log.Debug($"{_go.name}: restored contact damage {damager.damageDealt} on '{obj.name}' after resuming");
            }
        }
    }

    /// <summary>
    /// The restart runs the FSMs' start states, which for arena and ambush enemies hide the enemy
    /// (renderer off, sprite made transparent, "Dormant") until an entrance shows it. Jumping to
    /// the owner's state skips that entrance, so the enemy would stay invisible for its new owner
    /// and, through the snapshots, for everyone else. Show it the way the old owner did, and give
    /// the body the old owner's type again in case a start state changed it.
    /// </summary>
    private void RestoreOwnerLook()
    {
        if (_renderer != null && _ownerVisible == true && !_renderer.enabled)
        {
            _renderer.enabled = true;
            Utils.Log.Debug($"{_go.name}: shown again after resuming (the restart had hidden it)");
        }

        if (_sprite != null && _ownerSpriteColor.HasValue && PackColor(_sprite.color) != _ownerSpriteColor.Value)
        {
            _sprite.color = UnpackColor(_ownerSpriteColor.Value);
            Utils.Log.Debug($"{_go.name}: restored the owner's sprite colour after resuming");
        }

        if (_body != null && _ownerKinematic.HasValue && _bodyType != RigidbodyType2D.Static)
        {
            var type = _ownerKinematic.Value ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
            if (_body.bodyType != type)
            {
                _body.bodyType = type;
                Utils.Log.Debug($"{_go.name}: restored the owner's body type {type} after resuming");
            }
        }
    }

    /// <summary>
    /// If the enemy still shows the animation its restart put on (e.g. asleep) after jumping to the
    /// owner's states, show the owner's animation instead. Some states don't play a clip of their
    /// own because the skipped wake-up state already did.
    /// </summary>
    private void FixRestartClip(tk2dSpriteAnimationClip? restartClip)
    {
        if (_animator == null || restartClip == null || _animator.CurrentClip != restartClip ||
            _ownerClip == EnemyStatePacket.NoClip || _animator.Library == null)
        {
            return;
        }

        var clips = _animator.Library.clips;
        if (clips == null || _ownerClip >= clips.Length || clips[_ownerClip] == null ||
            clips[_ownerClip] == restartClip)
        {
            return;
        }

        Utils.Log.Debug($"{_go.name} still showed '{restartClip.name}' after resuming, playing the owner's " +
                        $"'{clips[_ownerClip].name}'");
        _animator.Play(clips[_ownerClip]);
    }

    /// <summary>
    /// Whether an attack hitbox (a child object, not the body) is active and can deal damage.
    /// Enemies aren't handed over mid-attack, which would cut the attack off.
    /// </summary>
    internal bool IsAttacking
    {
        get
        {
            for (var i = 0; i < _hitboxObjects.Count; i++)
            {
                var obj = _hitboxObjects[i];
                var damager = _hitboxDamagers[i];
                if (obj == null || obj == _go || damager == null || !obj.activeInHierarchy ||
                    !damager.enabled || damager.damageDealt <= 0)
                {
                    continue;
                }

                var col = _hitboxColliders[i];
                if (col == null || col.enabled)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private ushort CurrentClipId()
    {
        if (_animator == null)
        {
            return EnemyStatePacket.NoClip;
        }

        var clip = _animator.CurrentClip;
        return clip != null && _clipIds.TryGetValue(clip, out var id) ? id : EnemyStatePacket.NoClip;
    }

    // ------------------------------------------------------------------ puppet side

    /// <summary>Switch off the enemy's own AI so it only follows the host's snapshots.</summary>
    internal void Puppetize()
    {
        if (IsPuppet || _go == null)
        {
            return;
        }

        IsPuppet = true;
        _resumeFsmFrame = -1;
        _lastSnapshotTime = -1f;
        _ownerVelocity = Vector2.zero;
        _stuckSince = -1f;
        _visualsSynced = false;
        _ownerKinematic = null;
        _ownerVisible = null;
        _ownerSpriteColor = null;
        _disabledBehaviours.Clear();
        _savedDamage.Clear();

        // FSMs drive almost all enemy behaviour, including the ones on attack child objects
        foreach (var fsm in _go.GetComponentsInChildren<PlayMakerFSM>(true))
        {
            Disable(fsm);
        }

        // Script-driven behaviour on the enemy itself (movement components, FSM activators, ...)
        foreach (var behaviour in _go.GetComponents<MonoBehaviour>())
        {
            if (behaviour is not PlayMakerFSM && !KeepOnPuppet(behaviour))
            {
                Disable(behaviour);
            }
        }

        _body = _go.GetComponent<Rigidbody2D>();
        if (_body != null)
        {
            _bodyType = _body.bodyType;
            _body.bodyType = RigidbodyType2D.Kinematic;
            _body.linearVelocity = Vector2.zero;
            _body.angularVelocity = 0f;
        }

        Utils.Log.Debug($"Puppetized {_go.name} ({_disabledBehaviours.Count} behaviours disabled)");
    }

    private void Disable(Behaviour behaviour)
    {
        if (behaviour == null || !behaviour.enabled)
        {
            return;
        }

        behaviour.enabled = false;
        // Coroutines keep running on disabled behaviours; AI loops often live there
        if (behaviour is MonoBehaviour mb)
        {
            mb.StopAllCoroutines();
        }

        _disabledBehaviours.Add(behaviour);
    }

    /// <summary>Components a puppet still needs: health, rendering, animation, hit/death effects, contact damage.</summary>
    private static bool KeepOnPuppet(MonoBehaviour behaviour)
    {
        if (behaviour is HealthManager or DamageHero)
        {
            return true;
        }

        var name = behaviour.GetType().Name;
        return name.StartsWith("tk2d", StringComparison.Ordinal) ||
               name == "Recoil" ||
               name.Contains("Effect") ||
               name.Contains("Flash") ||
               name.Contains("Audio") ||
               name.Contains("Sound") ||
               name.Contains("Corpse");
    }

    /// <summary>
    /// Give the enemy its own AI back (we became its owner, its owner went quiet, or it is about to
    /// die). Its FSMs restart, which runs their setup (finding the hero, movement speeds). With
    /// <paramref name="resumeOwnerState"/>, on the next frame they jump to the owner's last known
    /// states, so the enemy carries on (chasing, attacking) instead of starting over asleep.
    /// Entering those states fresh also sets their movement again.
    /// </summary>
    internal void Unpuppetize(bool resumeOwnerState = true)
    {
        if (!IsPuppet)
        {
            return;
        }

        IsPuppet = false;
        _hasTarget = false;
        // As the (new) owner, always send the first snapshot
        _lastSent = null;

        foreach (var behaviour in _disabledBehaviours)
        {
            if (behaviour != null)
            {
                behaviour.enabled = true;
            }
        }

        _disabledBehaviours.Clear();

        // Damage the puppet switched off goes back; the enemy's own FSMs manage it from now on
        foreach (var pair in _savedDamage)
        {
            if (pair.Key != null && pair.Key.damageDealt <= 0)
            {
                pair.Key.damageDealt = pair.Value;
            }
        }

        _savedDamage.Clear();

        _resumeFsmFrame = resumeOwnerState && _ownerFsmStates.Length > 0 ? Time.frameCount + ResumeSetupFrames : -1;
        _stuckSince = -1f;

        if (_body != null)
        {
            // The owner's body type, not the one from when this became a puppet: FSMs make enemies
            // kinematic while burrowed (with their collider off), and a dynamic body would then
            // fall through the floor. Static bodies stay static.
            _body.bodyType = resumeOwnerState && _ownerKinematic.HasValue && _bodyType != RigidbodyType2D.Static
                ? _ownerKinematic.Value ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic
                : _bodyType;

            // Movement scripts that set the velocity once when a walk starts would otherwise keep
            // walking at the puppet's zero velocity
            if (resumeOwnerState && _body.bodyType == RigidbodyType2D.Dynamic)
            {
                _body.linearVelocity = _ownerVelocity;
            }
        }

        if (_go != null)
        {
            Utils.Log.Debug($"Unpuppetized {_go.name}");
        }
    }

    /// <summary>Apply a snapshot from the enemy's owner.</summary>
    internal void Apply(EnemyStatePacket state)
    {
        if (_go == null)
        {
            return;
        }

        Puppetize();

        if (_go.activeSelf != state.Active)
        {
            _go.SetActive(state.Active);
        }

        _target = new Vector2(state.X, state.Y);
        SampleOwnerVelocity(_target);
        if (!_hasTarget)
        {
            _hasTarget = true;
            SnapToTarget();
        }

        var scale = _go.transform.localScale;
        if (scale.x < 0 != state.ScaleXNegative)
        {
            scale.x = -scale.x;
            _go.transform.localScale = scale;
        }

        PlayClip(state.Clip);

        // Hidden while the owner's is (burrowed, dormant, before its arena entrance), shown when it emerges
        if (_renderer != null && _renderer.enabled != state.Visible)
        {
            _renderer.enabled = state.Visible;
        }

        if (_sprite != null && PackColor(_sprite.color) != state.SpriteColor)
        {
            _sprite.color = UnpackColor(state.SpriteColor);
        }

        if (_body != null)
        {
            _ownerKinematic = state.Kinematic;
        }

        _ownerVisible = state.Visible;
        _ownerSpriteColor = state.SpriteColor;

        ApplyVisuals(state);
        _ownerFsmStates = state.FsmStates;
        _ownerClip = state.Clip;
        _ownerColliderEnabled = state.HitboxColliderEnabled;
        _ownerDamaging = state.HitboxDamaging;

        // Puppets never die on their own; the host's death packet does that
        _hm.hp = Math.Max(1, state.Hp);

        // Index 0 can be the enemy itself (empty path sorts first); its active state is handled above
        for (var i = 0; i < _hitboxObjects.Count; i++)
        {
            var obj = _hitboxObjects[i];
            if (obj == null)
            {
                continue;
            }

            var shouldBeActive = (state.HitboxActive & (1u << i)) != 0;
            if (obj != _go && obj.activeSelf != shouldBeActive)
            {
                obj.SetActive(shouldBeActive);
            }

            var col = _hitboxColliders[i];
            var colliderEnabled = (state.HitboxColliderEnabled & (1u << i)) != 0;
            if (col != null && col.enabled != colliderEnabled)
            {
                col.enabled = colliderEnabled;
            }

            ApplyDamaging(_hitboxDamagers[i], (state.HitboxDamaging & (1u << i)) != 0);
        }
    }

    /// <summary>
    /// Copy the owner's effects: objects switched on/off, renderers, and particles (e.g. the dust that
    /// shows where an enemy will burst out of the ground). Applied when the owner's bit changes (and
    /// fully on the first snapshot), because effects end on their own when their animation finishes
    /// and re-applying the owner's still-on bit would restart them.
    /// </summary>
    private void ApplyVisuals(EnemyStatePacket state)
    {
        var first = !_visualsSynced;
        _visualsSynced = true;

        for (var i = 0; i < _visualObjects.Count; i++)
        {
            var obj = _visualObjects[i];
            if (obj == null)
            {
                continue;
            }

            var bit = 1u << i;
            var active = (state.VisualActive & bit) != 0;
            if (!_visualIsHitbox[i] && (first || active != ((_prevVisualActive & bit) != 0)) && obj.activeSelf != active)
            {
                obj.SetActive(active);
            }

            var r = _visualRenderers[i];
            var rendererOn = (state.VisualRenderer & bit) != 0;
            if (r != null && (first || rendererOn != ((_prevVisualRenderer & bit) != 0)) && r.enabled != rendererOn)
            {
                r.enabled = rendererOn;
            }

            var ps = _visualParticles[i];
            var particles = (state.VisualParticles & bit) != 0;
            var wasParticles = (_prevVisualParticles & bit) != 0;
            if (ps == null || (!first && particles == wasParticles))
            {
                continue;
            }

            try
            {
                if (particles && !ps.isEmitting && obj.activeInHierarchy)
                {
                    PlayParticles(ps);
                }
                else if (!particles && ps.isEmitting)
                {
                    // Let the particles already out fade naturally
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                }
            }
            catch (Exception e)
            {
                Utils.Log.Debug($"{_go.name}: could not sync particles on '{obj.name}': {e.Message}");
            }
        }

        _prevVisualActive = state.VisualActive;
        _prevVisualRenderer = state.VisualRenderer;
        _prevVisualParticles = state.VisualParticles;
    }

    private static uint PackColor(Color c)
    {
        Color32 c32 = c;
        return c32.r | ((uint) c32.g << 8) | ((uint) c32.b << 16) | ((uint) c32.a << 24);
    }

    private static Color UnpackColor(uint v) =>
        new Color32((byte) v, (byte) (v >> 8), (byte) (v >> 16), (byte) (v >> 24));

    /// <summary>
    /// Start a particle effect the owner started. Systems driven only by Emit() bursts have no
    /// emission of their own, so give those a burst too.
    /// </summary>
    private static void PlayParticles(ParticleSystem ps)
    {
        ps.Play(false);
        var emission = ps.emission;
        if (!emission.enabled || (emission.rateOverTimeMultiplier <= 0f && emission.burstCount == 0))
        {
            ps.Emit(15);
        }
    }

    /// <summary>
    /// Make a puppet's contact damage match the owner's: the owner's FSMs switch it on and off
    /// (asleep, waking, attacking), and the puppet's FSMs are off.
    /// </summary>
    private void ApplyDamaging(DamageHero? damager, bool damaging)
    {
        if (damager == null)
        {
            return;
        }

        if (damaging)
        {
            if (!damager.enabled)
            {
                damager.enabled = true;
            }

            if (damager.damageDealt <= 0)
            {
                damager.damageDealt = _savedDamage.TryGetValue(damager, out var saved) && saved > 0 ? saved
                    : _initialDamage.TryGetValue(damager, out var initial) ? initial
                    : 1;
            }
        }
        else if (damager.damageDealt > 0)
        {
            _savedDamage[damager] = damager.damageDealt;
            damager.damageDealt = 0;
        }
    }

    private void PlayClip(ushort clipId)
    {
        if (_animator == null || clipId == EnemyStatePacket.NoClip || _animator.Library == null)
        {
            return;
        }

        var clips = _animator.Library.clips;
        if (clips == null || clipId >= clips.Length || clips[clipId] == null)
        {
            return;
        }

        var clip = clips[clipId];
        if (_animator.CurrentClip != clip || !_animator.Playing)
        {
            _animator.Play(clip);
        }
    }

    /// <summary>Called every frame on puppets: glide toward the latest host position.</summary>
    internal void Tick(float deltaTime)
    {
        if (!IsPuppet || !_hasTarget || _go == null || !_go.activeInHierarchy)
        {
            return;
        }

        var pos = _go.transform.position;
        var target = new Vector3(_target.x, _target.y, pos.z);
        if ((target - pos).sqrMagnitude > SnapDistance * SnapDistance)
        {
            SnapToTarget();
            return;
        }

        _go.transform.position = Vector3.Lerp(pos, target, 1f - Mathf.Exp(-Smoothing * deltaTime));
        if (_body != null)
        {
            _body.linearVelocity = Vector2.zero;
        }
    }

    /// <summary>Estimate the owner's velocity from consecutive snapshot positions.</summary>
    private void SampleOwnerVelocity(Vector2 pos)
    {
        var now = Time.unscaledTime;
        if (_lastSnapshotTime >= 0f)
        {
            var dt = now - _lastSnapshotTime;
            if (dt > MaxVelocitySampleGap)
            {
                // Unchanged enemies are only re-sent twice a second: a long gap means it stood still
                _ownerVelocity = Vector2.zero;
            }
            else if (dt > 0.005f)
            {
                // Light smoothing against network jitter
                _ownerVelocity = Vector2.Lerp(_ownerVelocity, (pos - _lastSnapshotPos) / dt, 0.6f);
            }
            else
            {
                return;
            }
        }

        _lastSnapshotPos = pos;
        _lastSnapshotTime = now;
    }

    private void SnapToTarget()
    {
        var pos = _go.transform.position;
        _go.transform.position = new Vector3(_target.x, _target.y, pos.z);
    }
}
