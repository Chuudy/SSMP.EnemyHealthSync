using System;
using InControl;
using SSMPEnemyHealthSync.Utils;
using UnityEngine;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Triple-tap D-pad Left to go back through the door you came in by, e.g. to get out of a boss arena
/// that locked you in. Works offline too.
/// </summary>
internal static class TeleportBack
{
    private const int RequiredTaps = 3;

    private static int _taps;
    private static float _lastTapTime;

    internal static void Update()
    {
        if (!Config.TeleportBackDpad || !CanTeleport())
        {
            _taps = 0;
            return;
        }

        bool pressed;
        try
        {
            pressed = InputManager.ActiveDevice?.DPadLeft?.WasPressed == true;
        }
        catch (Exception)
        {
            return;
        }

        if (!pressed)
        {
            return;
        }

        var now = Time.unscaledTime;
        if (_taps > 0 && now - _lastTapTime > Config.TripleTapWindow)
        {
            _taps = 0;
        }

        _taps++;
        _lastTapTime = now;
        if (_taps < RequiredTaps)
        {
            return;
        }

        _taps = 0;
        Teleport();
    }

    /// <summary>Only during normal gameplay: not in menus, chat, transitions or while downed.</summary>
    private static bool CanTeleport()
    {
        var hero = HeroController.instance;
        var gm = GameManager.instance;
        if (hero == null || gm == null || !hero.acceptingInput || gm.IsInSceneTransition)
        {
            return false;
        }

        if (DownedState.IsDownedOrPending)
        {
            return false;
        }

        try
        {
            if (Client.Api?.UiManager?.ChatBox?.IsOpen == true)
            {
                return false;
            }
        }
        catch (Exception)
        {
            // Chat state unknown: allow
        }

        return true;
    }

    private static void Teleport()
    {
        var gm = GameManager.instance;
        if (!FindWayBack(gm, out var scene, out var gate))
        {
            Client.LocalChat("Can't find the way back from here.");
            Log.Info($"Teleport back: no door found (entry gate '{gm.entryGateName}', last scene '{gm.lastSceneName}')");
            return;
        }

        Log.Info($"Teleport back to {scene} through {gate}");
        Client.LocalChat($"Going back to {scene}...");

        gm.BeginSceneTransition(new GameManager.SceneLoadInfo
        {
            SceneName = scene,
            EntryGateName = gate,
            PreventCameraFadeOut = true,
            WaitForSceneTransitionCameraFade = false,
            AlwaysUnloadUnusedAssets = true,
            IsFirstLevelForPlayer = false
        });
    }

    /// <summary>
    /// The door you entered by leads back to the previous room. If you didn't come through a door
    /// (bench, cutscene, travel), use any door that leads to the previous room.
    /// </summary>
    private static bool FindWayBack(GameManager gm, out string scene, out string gate)
    {
        scene = gate = "";
        var points = TransitionPoint.TransitionPoints;
        if (points == null)
        {
            return false;
        }

        TransitionPoint? fallback = null;
        foreach (var point in points)
        {
            if (point == null || string.IsNullOrEmpty(point.targetScene) || string.IsNullOrEmpty(point.entryPoint))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(gm.entryGateName) && point.name == gm.entryGateName)
            {
                scene = point.targetScene;
                gate = point.entryPoint;
                return true;
            }

            if (fallback == null && point.targetScene == gm.lastSceneName)
            {
                fallback = point;
            }
        }

        if (fallback == null)
        {
            return false;
        }

        scene = fallback.targetScene;
        gate = fallback.entryPoint;
        return true;
    }
}
