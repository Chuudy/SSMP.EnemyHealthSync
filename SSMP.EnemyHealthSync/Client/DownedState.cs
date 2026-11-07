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
/// A downed player lies face down ("Prostrate"), flinches on every revive hit ("Taunt CollapseHit")
/// and gets up with "Prostrate Rise". SSMP syncs all three hero clips to other players, so they need
/// no extra networking. Downed states and revive progress are shared through the server so other
/// players know whom they can revive, can see the progress (<see cref="ReviveIndicators"/>), and
/// enemies are never handed to a downed player.
/// </summary>
internal static class DownedState
{
    /// <summary>Downed pose, best first; the first one the hero has is used.</summary>
    private static readonly string[] DownedClipNames = ["Prostrate", "Sitting Asleep"];

    private const string HitClipName = "Taunt CollapseHit";
    private const string RiseClipName = "Prostrate Rise";

    private const float GiveUpHoldSeconds = 2f;
    private const float NoTeammateGraceSeconds = 2f;
    private const float ReviveGraceSeconds = 1.5f;
    private const float ReviveAttackCooldown = 0.25f;
    private const float MaxHitFlinchSeconds = 0.8f;
    private const float MaxRiseSeconds = 2f;

    /// <summary>Downed players in our room, by SSMP player ID, with their revive progress.</summary>
    private static readonly Dictionary<ushort, (int Hits, int Needed)> RemoteDowned = new();

    private static bool _initialized;

    /// <summary>A fatal hit was turned into 1 HP; enter the downed state once the hit (and any hazard respawn) is over.</summary>
    private static bool _pendingDown;

    private static float _downedSince;
    private static int _reviveHits;
    private static float _giveUpHeldSince = -1f;
    private static float _noTeammateSince = -1f;
    private static float _graceUntil = -1f;
    private static float _lastReviveAttack = -999f;

    /// <summary>Until when a revive-hit flinch plays instead of the downed pose.</summary>
    private static float _poseLockUntil = -1f;

    /// <summary>While positive: revived and playing the get-up animation, control comes back at this time.</summary>
    private static float _risingUntil = -1f;

    /// <summary>Whether the local player is downed.</summary>
    internal static bool IsDowned { get; private set; }

    /// <summary>
    /// Whether the local player should take no damage: while downed, between the fatal hit and
    /// going down, and for a moment after being revived (including the get-up animation).
    /// </summary>
    internal static bool BlocksDamage =>
        IsDowned || _pendingDown || Time.time < _graceUntil;

    /// <summary>Downed or about to be: enemies should be handed to standing teammates.</summary>
    internal static bool IsDownedOrPending => IsDowned || _pendingDown;

    internal static bool IsRemoteDowned(ushort playerId) => RemoteDowned.ContainsKey(playerId);

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
        if (_risingUntil > 0f)
        {
            FinishRise(hero);
        }

        _pendingDown = false;
        IsDowned = true;
        _downedSince = Time.time;
        _reviveHits = 0;
        _giveUpHeldSince = -1f;
        _noTeammateSince = -1f;
        _poseLockUntil = -1f;

        hero.RelinquishControl();
        hero.StopAnimationControl();
        PlayClip(hero, Clips.Downed);
        if (hero.rb2d != null)
        {
            var velocity = hero.rb2d.linearVelocity;
            hero.rb2d.linearVelocity = new Vector2(0f, Mathf.Min(0f, velocity.y));
        }

        Client.SendDowned(true, 0, Config.ReviveHits);
        Client.LocalChat(Config.BleedOutSeconds > 0f
            ? $"You are down! Teammates can revive you by attacking next to you. Hold Quick Map to give up " +
              $"(you bleed out in {Config.BleedOutSeconds:0} s)."
            : "You are down! Teammates can revive you by attacking next to you. Hold Quick Map to give up.");
        Log.Info("Local player is down");
    }

    // ------------------------------------------------------------------ getting up / dying

    /// <summary>
    /// Stand back up. With <paramref name="heal"/> (a real revive), health is raised to ReviveHealth
    /// and the get-up animation plays before control comes back.
    /// </summary>
    private static void Revive(bool heal)
    {
        var hero = HeroController.SilentInstance;
        var riseSeconds = heal && hero != null && !hero.cState.dead && !hero.cState.transitioning
            ? Mathf.Min(ClipSeconds(Clips.Rise), MaxRiseSeconds)
            : 0f;

        LeaveDowned(hero, giveControlBack: riseSeconds <= 0f);

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

            if (riseSeconds > 0f)
            {
                PlayClip(hero, Clips.Rise);
                _risingUntil = Time.time + riseSeconds;
            }

            _graceUntil = Time.time + riseSeconds + ReviveGraceSeconds;
            Client.LocalChat("You were revived!");
        }

        Log.Info($"Local player got back up (healed: {heal})");
    }

    /// <summary>End the get-up animation and give control back.</summary>
    private static void FinishRise(HeroController? hero)
    {
        _risingUntil = -1f;
        if (hero != null && !hero.cState.dead)
        {
            hero.StartAnimationControl();
            hero.RegainControl();
        }
    }

    /// <summary>Die for real (gave up, bled out, or nobody left standing).</summary>
    private static void DieForReal(string reason)
    {
        var hero = HeroController.SilentInstance;
        LeaveDowned(hero, giveControlBack: true);

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
    private static void LeaveDowned(HeroController? hero, bool giveControlBack)
    {
        var wasDowned = IsDowned;
        IsDowned = false;
        _pendingDown = false;
        _giveUpHeldSince = -1f;
        _noTeammateSince = -1f;
        _poseLockUntil = -1f;

        if (!wasDowned)
        {
            return;
        }

        if (hero != null && giveControlBack)
        {
            hero.StartAnimationControl();
            hero.RegainControl();
        }

        Client.SendDowned(false);
    }

    // ------------------------------------------------------------------ per frame

    internal static void Update()
    {
        UpdateLocal();
        UpdateIndicators();
    }

    private static void UpdateLocal()
    {
        var hero = HeroController.SilentInstance;

        if (_risingUntil > 0f)
        {
            UpdateRising(hero);
        }

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
            LeaveDowned(hero, giveControlBack: true);
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

    /// <summary>Hold still while the get-up animation plays, then give control back.</summary>
    private static void UpdateRising(HeroController? hero)
    {
        if (hero == null || hero.cState.dead || !Client.IsConnected || Time.time >= _risingUntil ||
            hero.cState.hazardRespawning || hero.cState.transitioning)
        {
            FinishRise(hero);
            return;
        }

        if (hero.acceptingInput)
        {
            hero.RelinquishControl();
        }

        if (hero.rb2d != null && Mathf.Abs(hero.rb2d.linearVelocity.x) > 0.01f)
        {
            hero.rb2d.linearVelocity = new Vector2(0f, hero.rb2d.linearVelocity.y);
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

        // A revive-hit flinch is playing; the downed pose comes back when it's over
        if (Time.time >= _poseLockUntil && Clips.Downed != null)
        {
            var animator = GetAnimator(hero);
            if (animator != null && (animator.CurrentClip == null || animator.CurrentClip.name != Clips.Downed))
            {
                hero.StopAnimationControl();
                PlayClip(hero, Clips.Downed);
            }
        }

        if (hero.rb2d != null && Mathf.Abs(hero.rb2d.linearVelocity.x) > 0.01f)
        {
            hero.rb2d.linearVelocity = new Vector2(0f, hero.rb2d.linearVelocity.y);
        }
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

    /// <summary>Show revive progress above every downed player in the room, including ourselves.</summary>
    private static void UpdateIndicators()
    {
        ReviveIndicators.BeginFrame();

        if (IsDowned)
        {
            var hero = HeroController.SilentInstance;
            if (hero != null)
            {
                ReviveIndicators.Show(ReviveIndicators.LocalKey, hero.transform, _reviveHits, Config.ReviveHits);
            }
        }

        if (RemoteDowned.Count > 0)
        {
            var players = Client.Api?.ClientManager?.Players;
            if (players != null)
            {
                foreach (var player in players)
                {
                    if (player.IsInLocalScene && player.PlayerObject != null &&
                        RemoteDowned.TryGetValue(player.Id, out var progress))
                    {
                        var needed = progress.Needed > 0 ? progress.Needed : Config.ReviveHits;
                        ReviveIndicators.Show(player.Id, player.PlayerObject.transform, progress.Hits, needed);
                    }
                }
            }
        }

        ReviveIndicators.EndFrame();
    }

    // ------------------------------------------------------------------ hero animations

    /// <summary>Hero clip names, resolved once against the hero's animation library (null = missing).</summary>
    private static class Clips
    {
        private static bool _resolved;

        internal static string? Downed { get; private set; }
        internal static string? Hit { get; private set; }
        internal static string? Rise { get; private set; }

        internal static void Resolve(tk2dSpriteAnimator animator)
        {
            if (_resolved || animator.Library == null)
            {
                return;
            }

            _resolved = true;
            var library = animator.Library;

            foreach (var name in DownedClipNames)
            {
                if (library.GetClipByName(name) != null)
                {
                    Downed = name;
                    break;
                }
            }

            Hit = library.GetClipByName(HitClipName) != null ? HitClipName : null;
            Rise = library.GetClipByName(RiseClipName) != null ? RiseClipName : null;

            if (Downed == null)
            {
                Log.Warn("No downed hero animation found; downed players keep their current pose");
            }
            if (Hit == null || Rise == null)
            {
                Log.Warn($"Hero animation(s) not found:{(Hit == null ? " " + HitClipName : "")}" +
                         $"{(Rise == null ? " " + RiseClipName : "")}");
            }

            Log.Debug($"Downed clips: pose '{Downed}', hit '{Hit}', rise '{Rise}'");
        }
    }

    /// <summary>The hero's sprite animator: the one SSMP watches to send the hero's animations.</summary>
    private static tk2dSpriteAnimator? GetAnimator(HeroController hero)
    {
        var animator = hero.GetComponent<tk2dSpriteAnimator>();
        if (animator != null)
        {
            Clips.Resolve(animator);
        }

        return animator;
    }

    /// <summary>
    /// Play a hero clip directly on the sprite animator. HeroAnimationController.PlayClip does
    /// nothing while animation control is stopped, which it is for the whole downed state.
    /// </summary>
    private static void PlayClip(HeroController hero, string? clip)
    {
        var animator = GetAnimator(hero);
        if (clip == null || animator == null)
        {
            return;
        }

        animator.Play(clip);
    }

    /// <summary>Length of one play-through of a hero clip, 0 if unknown.</summary>
    private static float ClipSeconds(string? clip)
    {
        var hero = HeroController.SilentInstance;
        var animator = hero != null ? GetAnimator(hero) : null;
        if (clip == null || animator == null || animator.Library == null)
        {
            return 0f;
        }

        var data = animator.Library.GetClipByName(clip);
        if (data == null || data.frames == null || data.fps <= 0f)
        {
            return 0f;
        }

        return data.frames.Length / data.fps;
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
            if (player.IsInLocalScene && !RemoteDowned.ContainsKey(player.Id))
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
            if (!player.IsInLocalScene || !RemoteDowned.ContainsKey(player.Id) || player.PlayerObject == null)
            {
                continue;
            }

            if (Vector2.Distance(heroPos, player.PlayerObject.transform.position) <= Config.ReviveRange)
            {
                _lastReviveAttack = now;
                Log.Debug($"Revive hit on player {player.Id}");
                Client.SendReviveHit(player.Id);

                // Immediate feedback; the filled pip follows once the downed player confirms the hit
                ReviveIndicators.Pulse(player.Id);
            }
        }
    }

    // ------------------------------------------------------------------ network events

    internal static void OnRemoteDowned(ushort playerId, bool downed, int reviveHits, int reviveHitsNeeded)
    {
        var name = Client.Api?.ClientManager?.GetPlayer(playerId)?.Username ?? $"Player {playerId}";

        if (downed)
        {
            var isNew = !RemoteDowned.ContainsKey(playerId);
            RemoteDowned[playerId] = (reviveHits, reviveHitsNeeded);
            if (isNew)
            {
                Client.LocalChat($"{name} is down! Attack next to them to revive them.");
            }

            return;
        }

        if (RemoteDowned.Remove(playerId))
        {
            Client.LocalChat($"{name} is back up.");
        }
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
            return;
        }

        // Share the progress with the room and flinch, so the attacker sees the hit land
        Client.SendDowned(true, _reviveHits, Config.ReviveHits);

        var hero = HeroController.SilentInstance;
        if (hero == null || hero.cState.hazardRespawning || hero.cState.transitioning)
        {
            return;
        }

        var flinch = Mathf.Min(ClipSeconds(Clips.Hit), MaxHitFlinchSeconds);
        if (flinch > 0f)
        {
            PlayClip(hero, Clips.Hit);
            _poseLockUntil = Time.time + flinch;
        }
    }

    internal static void OnPlayerLeft(ushort playerId) => RemoteDowned.Remove(playerId);

    private static void OnSceneChanged()
    {
        // The server sends the downed players of the new room
        RemoteDowned.Clear();
        ReviveIndicators.Clear();

        if (_risingUntil > 0f)
        {
            FinishRise(HeroController.SilentInstance);
        }

        if (IsDownedOrPending)
        {
            // Should not happen (no control while downed), but never stay stuck downed in another room
            Revive(heal: false);
        }
    }

    internal static void OnDisconnected()
    {
        RemoteDowned.Clear();
        ReviveIndicators.Clear();

        if (_risingUntil > 0f)
        {
            FinishRise(HeroController.SilentInstance);
        }

        if (IsDownedOrPending)
        {
            Revive(heal: false);
        }

        _graceUntil = -1f;
    }
}
