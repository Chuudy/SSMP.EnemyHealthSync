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

    /// <summary>
    /// After taking over, the FSMs restart (which sets up their variables); a moment later they jump
    /// to the owner's states. Unscaled time, or negative when nothing is pending.
    /// </summary>
    private float _resumeFsmAt = -1f;

    /// <summary>Let restarted FSMs run their setup states before moving them to the owner's states.</summary>
    private const float FsmResumeDelay = 0.1f;

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
        _fsms = _go.GetComponentsInChildren<PlayMakerFSM>(true).Take(EnemyStatePacket.MaxFsms).ToArray();
    }

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
            _hitboxDamagers.Add(obj.GetComponent<DamageHero>());
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
        a.HitboxDamaging == b.HitboxDamaging &&
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
        if (_resumeFsmAt >= 0f && Time.unscaledTime >= _resumeFsmAt)
        {
            _resumeFsmAt = -1f;
            ResumeOwnerFsmStates();
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
        _resumeFsmAt = -1f;
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
    /// <paramref name="resumeOwnerState"/>, a moment later they jump to the owner's last known
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

        _resumeFsmAt = resumeOwnerState && _ownerFsmStates.Length > 0
            ? Time.unscaledTime + FsmResumeDelay
            : -1f;

        if (_body != null)
        {
            _body.bodyType = _bodyType;
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
        _ownerFsmStates = state.FsmStates;

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
                damager.damageDealt = _savedDamage.TryGetValue(damager, out var saved) && saved > 0 ? saved : 1;
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

    private void SnapToTarget()
    {
        var pos = _go.transform.position;
        _go.transform.position = new Vector3(_target.x, _target.y, pos.z);
    }
}
