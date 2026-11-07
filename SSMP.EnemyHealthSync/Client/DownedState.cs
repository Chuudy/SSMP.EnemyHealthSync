using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SSMPEnemyHealthSync.Utils;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Downed / revive: a hit that would kill the local player while a standing teammate is in the room
/// knocks them down instead (1 HP, no control, no damage taken). Teammates revive them by attacking
/// next to them. A downed player dies for real when they give up (hold Quick Map), bleed out, or
/// when nobody in the room is left standing.
///
/// The downed pose is the hero's "Sitting Asleep" clip, which SSMP already syncs to other players,
/// so no extra visuals need networking. Downed states are shared through the server so other
/// players know whom they can revive and enemies are never handed to a downed player.
/// </summary>
internal static class DownedState
{
    private const string DownedClip = "Sitting Asleep";
    private const float GiveUpHoldSeconds = 2f;
    private const float NoTeammateGraceSeconds = 2f;
    private const float ReviveGraceSeconds = 1.5f;
    private const float ReviveAttackCooldown = 0.25f;

    /// <summary>Downed players in our room, by SSMP player ID.</summary>
    private static readonly HashSet<ushort> RemoteDowned = new();

    private static bool _initialized;

    /// <summary>A fatal hit was turned into 1 HP; enter the downed state once the hit (and any hazard respawn) is over.</summary>
    private static bool _pendingDown;

    private static float _downedSince;
    private static int _reviveHits;
    private static float _giveUpHeldSince = -1f;
    private static float _noTeammateSince = -1f;
    private static float _graceUntil = -1f;
    private static float _lastReviveAttack = -999f;

    /// <summary>Whether the local player is downed.</summary>
    internal static bool IsDowned { get; private set; }

    /// <summary>
    /// Whether the local player should take no damage: while downed, between the fatal hit and
    /// going down, and for a moment after being revived.
    /// </summary>
    internal static bool BlocksDamage =>
        IsDowned || _pendingDown || Time.time < _graceUntil;

    /// <summary>Downed or about to be: enemies should be handed to standing teammates.</summary>
    internal static bool IsDownedOrPending => IsDowned || _pendingDown;

    internal static bool IsRemoteDowned(ushort playerId) => RemoteDowned.Contains(playerId);

    internal static void Init()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SceneManager.activeSceneChanged += (_, _) => OnSceneChanged();
    }

    // ------------------------------------------------------------------ going down

    /// <summary>
    /// Whether a fatal hit right now should knock the local player down instead of killing them:
    /// connected, enabled, alive, and at least one standing teammate in the room.
    /// </summary>
    internal static bool CanGoDown()
    {
        if (!Config.DownedEnabled || !Client.IsConnected || IsDownedOrPending)
        {
            return false;
        }

        var hero = HeroController.SilentInstance;
        return hero != null && !hero.cState.dead && AnyStandingTeammate();
    }

    /// <summary>Called by the TakeHealth patch after it turned a fatal hit into 1 HP.</summary>
    internal static void MarkPendingDown()
    {
        _pendingDown = true;
        Log.Debug("Fatal hit turned into a down");
    }

    private static void EnterDowned(HeroController hero)
    {
        _pendingDown = false;
        IsDowned = true;
        _downedSince = Time.time;
        _reviveHits = 0;
        _giveUpHeldSince = -1f;
        _noTeammateSince = -1f;

        hero.RelinquishControl();
        hero.StopAnimationControl();
        if (hero.animCtrl != null)
        {
            hero.animCtrl.PlayClip(DownedClip);
        }
        if (hero.rb2d != null)
        {
            var velocity = hero.rb2d.linearVelocity;
            hero.rb2d.linearVelocity = new Vector2(0f, Mathf.Min(0f, velocity.y));
        }

        Client.SendDowned(true);
        Client.LocalChat(Config.BleedOutSeconds > 0f
            ? $"You are down! Teammates can revive you by attacking next to you. Hold Quick Map to give up " +
              $"(you bleed out in {Config.BleedOutSeconds:0} s)."
            : "You are down! Teammates can revive you by attacking next to you. Hold Quick Map to give up.");
        Log.Info("Local player is down");
    }

    // ------------------------------------------------------------------ getting up / dying

    /// <summary>Stand back up. With <paramref name="heal"/>, health is raised to ReviveHealth.</summary>
    private static void Revive(bool heal)
    {
        var hero = HeroController.SilentInstance;
        LeaveDowned(hero);

        if (hero == null)
        {
            return;
        }

        if (heal)
        {
            var target = Math.Min(Config.ReviveHealth, hero.playerData.CurrentMaxHealth);
            var missing = target - hero.playerData.health;
            if (missing > 0)
            {
                hero.AddHealth(missing);
            }

            _graceUntil = Time.time + ReviveGraceSeconds;
            Client.LocalChat("You were revived!");
        }

        Log.Info($"Local player got back up (healed: {heal})");
    }

    /// <summary>Die for real (gave up, bled out, or nobody left standing).</summary>
    private static void DieForReal(string reason)
    {
        var hero = HeroController.SilentInstance;
        LeaveDowned(hero);

        if (hero == null || hero.cState.dead)
        {
            return;
        }

        Log.Info($"Downed player dies: {reason}");
        Client.LocalChat(reason);

        hero.playerData.healthBlue = 0;
        hero.playerData.health = 0;
        hero.StartCoroutine(hero.Die(false, false));
    }

    /// <summary>Common exit from the downed state: give control and animations back, tell the others.</summary>
    private static void LeaveDowned(HeroController? hero)
    {
        var wasDowned = IsDowned;
        IsDowned = false;
        _pendingDown = false;
        _giveUpHeldSince = -1f;
        _noTeammateSince = -1f;

        if (!wasDowned)
        {
            return;
        }

        if (hero != null)
        {
            hero.StartAnimationControl();
            hero.RegainControl();
        }

        Client.SendDowned(false);
    }

    // ------------------------------------------------------------------ per frame

    internal static void Update()
    {
        var hero = HeroController.SilentInstance;

        if (_pendingDown)
        {
            if (hero == null || hero.cState.dead || !Client.IsConnected)
            {
                _pendingDown = false;
            }
            else if (!hero.cState.hazardRespawning && !hero.cState.transitioning)
            {
                // Wait for the hit (and a hazard respawn, e.g. spikes) to finish before lying down
                EnterDowned(hero);
            }

            return;
        }

        if (!IsDowned)
        {
            return;
        }

        if (hero == null || hero.cState.dead)
        {
            LeaveDowned(hero);
            return;
        }

        if (!Client.IsConnected)
        {
            // Nobody can revive us any more
            Revive(heal: false);
            return;
        }

        KeepDownedPose(hero);

        var now = Time.time;
        if (IsGiveUpHeld())
        {
            if (_giveUpHeldSince < 0f)
            {
                _giveUpHeldSince = now;
            }
            else if (now - _giveUpHeldSince >= GiveUpHoldSeconds)
            {
                DieForReal("You gave up.");
                return;
            }
        }
        else
        {
            _giveUpHeldSince = -1f;
        }

        if (Config.BleedOutSeconds > 0f && now - _downedSince >= Config.BleedOutSeconds)
        {
            DieForReal("You bled out.");
            return;
        }

        if (AnyStandingTeammate())
        {
            _noTeammateSince = -1f;
        }
        else if (_noTeammateSince < 0f)
        {
            _noTeammateSince = now;
        }
        else if (now - _noTeammateSince >= NoTeammateGraceSeconds)
        {
            DieForReal("Nobody is left standing.");
        }
    }

    /// <summary>Things like a hazard respawn can give control or animations back; undo that while downed.</summary>
    private static void KeepDownedPose(HeroController hero)
    {
        // Let a hazard respawn (spikes, acid, pits) finish moving us to safe ground first
        if (hero.cState.hazardRespawning || hero.cState.transitioning)
        {
            return;
        }

        if (hero.acceptingInput)
        {
            hero.RelinquishControl();
        }

        var animCtrl = hero.animCtrl;
        var animator = animCtrl != null ? animCtrl.GetComponent<tk2dSpriteAnimator>() : null;
        if (animCtrl != null && animator != null && HasDownedClip(animator) &&
            (animator.CurrentClip == null || animator.CurrentClip.name != DownedClip))
        {
            hero.StopAnimationControl();
            animCtrl.PlayClip(DownedClip);
        }

        if (hero.rb2d != null && Mathf.Abs(hero.rb2d.linearVelocity.x) > 0.01f)
        {
            hero.rb2d.linearVelocity = new Vector2(0f, hero.rb2d.linearVelocity.y);
        }
    }

    private static bool? _hasDownedClip;

    /// <summary>
    /// Whether the hero's animation library has the downed clip. Checked once, so a missing clip
    /// doesn't make <see cref="KeepDownedPose"/> retry (and log) every frame.
    /// </summary>
    private static bool HasDownedClip(tk2dSpriteAnimator animator)
    {
        if (_hasDownedClip == null)
        {
            _hasDownedClip = animator.Library != null && animator.Library.GetClipByName(DownedClip) != null;
            if (_hasDownedClip == false)
            {
                Log.Warn($"Hero animation '{DownedClip}' not found; downed players keep their current pose");
            }
        }

        return _hasDownedClip.Value;
    }

    private static bool IsGiveUpHeld()
    {
        try
        {
            var actions = InputHandler.Instance != null ? InputHandler.Instance.inputActions : null;
            return actions != null && actions.QuickMap.IsPressed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ teammates

    /// <summary>Other players in our room that are not downed.</summary>
    internal static bool AnyStandingTeammate()
    {
        var players = Client.Api?.ClientManager?.Players;
        if (players == null)
        {
            return false;
        }

        foreach (var player in players)
        {
            if (player.IsInLocalScene && !RemoteDowned.Contains(player.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Called when the local player attacks: every downed teammate close enough gets a revive hit.
    /// </summary>
    internal static void OnLocalAttack()
    {
        if (RemoteDowned.Count == 0 || IsDownedOrPending || !Client.IsConnected)
        {
            return;
        }

        var now = Time.time;
        if (now - _lastReviveAttack < ReviveAttackCooldown)
        {
            return;
        }

        var hero = HeroController.SilentInstance;
        var players = Client.Api?.ClientManager?.Players;
        if (hero == null || players == null)
        {
            return;
        }

        Vector2 heroPos = hero.transform.position;
        foreach (var player in players)
        {
            if (!player.IsInLocalScene || !RemoteDowned.Contains(player.Id) || player.PlayerObject == null)
            {
                continue;
            }

            if (Vector2.Distance(heroPos, player.PlayerObject.transform.position) <= Config.ReviveRange)
            {
                _lastReviveAttack = now;
                Log.Debug($"Revive hit on player {player.Id}");
                Client.SendReviveHit(player.Id);
            }
        }
    }

    // ------------------------------------------------------------------ network events

    internal static void OnRemoteDowned(ushort playerId, bool downed)
    {
        var changed = downed ? RemoteDowned.Add(playerId) : RemoteDowned.Remove(playerId);
        if (!changed)
        {
            return;
        }

        var name = Client.Api?.ClientManager?.GetPlayer(playerId)?.Username ?? $"Player {playerId}";
        Client.LocalChat(downed
            ? $"{name} is down! Attack next to them to revive them."
            : $"{name} is back up.");
    }

    internal static void OnReviveHit()
    {
        if (!IsDowned)
        {
            return;
        }

        _reviveHits++;
        Log.Debug($"Revive hit {_reviveHits}/{Config.ReviveHits}");
        if (_reviveHits >= Config.ReviveHits)
        {
            Revive(heal: true);
        }
    }

    internal static void OnPlayerLeft(ushort playerId) => RemoteDowned.Remove(playerId);

    private static void OnSceneChanged()
    {
        // The server sends the downed players of the new room
        RemoteDowned.Clear();

        if (IsDownedOrPending)
        {
            // Should not happen (no control while downed), but never stay stuck downed in another room
            Revive(heal: false);
        }
    }

    internal static void OnDisconnected()
    {
        RemoteDowned.Clear();
        if (IsDownedOrPending)
        {
            Revive(heal: false);
        }

        _graceUntil = -1f;
    }
}
