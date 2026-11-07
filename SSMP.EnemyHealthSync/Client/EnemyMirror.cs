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

    // Index-aligned: hitbox i is _hitboxObjects[i] with optional collider _hitboxColliders[i]
    private readonly List<GameObject> _hitboxObjects = new();
    private readonly List<Collider2D?> _hitboxColliders = new();

    // What Puppetize changed, so Unpuppetize can restore it exactly
    private readonly List<Behaviour> _disabledBehaviours = new();
    private Rigidbody2D? _body;
    private RigidbodyType2D _bodyType;

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

        uint active = 0, colliderEnabled = 0;
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
        }

        packet.HitboxActive = active;
        packet.HitboxColliderEnabled = colliderEnabled;

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
        a.HitboxActive == b.HitboxActive && a.HitboxColliderEnabled == b.HitboxColliderEnabled;

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
        _disabledBehaviours.Clear();

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

    /// <summary>Give the enemy its own AI back (we became its owner, its owner went quiet, or it is about to die).</summary>
    internal void Unpuppetize()
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
