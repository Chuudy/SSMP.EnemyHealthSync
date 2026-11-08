using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GlobalEnums;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SSMPEnemyHealthSync.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Discovery logging for syncing the bell beast (Bellway fast travel). Only runs with DebugLogging on.
/// Logs which FSMs drive the Bellway station and the beast, their states and every state change, and
/// which destination gets picked, so the sync can be built on the real names.
/// </summary>
internal static class BellBeastProbe
{
    private const string Tag = "[BellBeast]";
    private const float RescanInterval = 3f;

    private static readonly Regex NamePattern = new("bell|beast|travel|toll|stag", RegexOptions.IgnoreCase);

    private static readonly Dictionary<PlayMakerFSM, string> Watched = new();
    private static readonly List<PlayMakerFSM> Dead = new();

    private static string _scene = "";
    private static float _nextScan;
    private static FastTravelLocations? _lastNpcLocation;

    internal static void Update()
    {
        if (!Config.DebugLogging)
        {
            return;
        }

        try
        {
            var scene = SceneManager.GetActiveScene().name;
            if (scene != _scene)
            {
                _scene = scene;
                Watched.Clear();
                _nextScan = Time.unscaledTime + 1f; // Let the scene finish setting up
                Log.Info($"{Tag} Entered scene {scene}");
            }

            LogNpcLocation();

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + RescanInterval;
                Scan();
            }

            PollStates();
        }
        catch (Exception e)
        {
            Log.Debug($"{Tag} probe failed: {e.Message}");
        }
    }

    private static void LogNpcLocation()
    {
        var pd = GameManager.SilentInstance != null ? GameManager.SilentInstance.playerData : null;
        if (pd == null)
        {
            return;
        }

        var location = pd.FastTravelNPCLocation;
        if (_lastNpcLocation == location)
        {
            return;
        }

        _lastNpcLocation = location;
        Log.Info($"{Tag} PlayerData.FastTravelNPCLocation = {location} (unlocked: {pd.UnlockedFastTravel}, " +
                 $"travelling: {pd.travelling})");
    }

    private static void Scan()
    {
        foreach (var fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
        {
            if (fsm == null || Watched.ContainsKey(fsm) || !fsm.gameObject.scene.IsValid())
            {
                continue;
            }

            var hasTravelAction = HasTravelAction(fsm);
            var path = PathOf(fsm.transform);
            if (!hasTravelAction && !NamePattern.IsMatch(path) && !NamePattern.IsMatch(fsm.FsmName ?? ""))
            {
                continue;
            }

            var state = SafeState(fsm);
            Watched[fsm] = state;
            Log.Info($"{Tag} FSM '{fsm.FsmName}' on {path} (scene {fsm.gameObject.scene.name}, " +
                     $"active: {fsm.gameObject.activeInHierarchy}, enabled: {fsm.enabled}, " +
                     $"travel actions: {hasTravelAction}) state: {state}");
            Log.Info($"{Tag}   states: {DescribeStates(fsm, hasTravelAction)}");
        }
    }

    private static void PollStates()
    {
        Dead.Clear();
        foreach (var pair in Watched.ToList())
        {
            var fsm = pair.Key;
            if (fsm == null)
            {
                Dead.Add(fsm!);
                continue;
            }

            var state = SafeState(fsm);
            if (state == pair.Value)
            {
                continue;
            }

            Watched[fsm] = state;
            Log.Info($"{Tag} t={Time.time:F2} '{fsm.FsmName}' on {PathOf(fsm.transform)}: {pair.Value} -> {state}");
        }

        foreach (var fsm in Dead)
        {
            Watched.Remove(fsm);
        }
    }

    private static bool HasTravelAction(PlayMakerFSM fsm)
    {
        try
        {
            foreach (var state in fsm.FsmStates)
            {
                foreach (var action in state.Actions)
                {
                    if (action is OpenFastTravelUI or GetFastTravelScene or FindFastTravelTeleportPoint)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Some FSMs fail to load their actions outside play: ignore
        }

        return false;
    }

    private static string DescribeStates(PlayMakerFSM fsm, bool withActions)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var state in fsm.FsmStates)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(state.Name);
                if (withActions)
                {
                    sb.Append(" [");
                    sb.Append(string.Join(",", state.Actions.Select(a => a?.GetType().Name ?? "null")));
                    sb.Append("]");
                }

                var transitions = state.Transitions;
                if (transitions is { Length: > 0 })
                {
                    sb.Append(" {");
                    sb.Append(string.Join(",", transitions.Select(t => $"{t.EventName}->{t.ToState}")));
                    sb.Append("}");
                }
            }
        }
        catch (Exception e)
        {
            sb.Append($" (error: {e.Message})");
        }

        return sb.ToString();
    }

    private static string SafeState(PlayMakerFSM fsm)
    {
        try
        {
            return fsm.ActiveStateName ?? "(none)";
        }
        catch (Exception)
        {
            return "(unknown)";
        }
    }

    internal static string PathOf(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }

        return path;
    }

    // ------------------------------------------------------------------ patches

    [HarmonyPatch(typeof(OpenFastTravelUI), nameof(OpenFastTravelUI.OnEnter))]
    private static class OpenFastTravelUIEnterPatch
    {
        private static void Postfix(OpenFastTravelUI __instance)
        {
            if (!Config.DebugLogging) return;
            try
            {
                Log.Info($"{Tag} Travel map opened by FSM '{__instance.Fsm?.Name}' on " +
                         $"{(__instance.Owner != null ? PathOf(__instance.Owner.transform) : "?")}, state " +
                         $"'{__instance.State?.Name}', this location: {__instance.ThisLocation?.Value}");
            }
            catch (Exception e)
            {
                Log.Debug($"{Tag} log failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(OpenFastTravelUI), nameof(OpenFastTravelUI.OnLocationConfirmed))]
    private static class OpenFastTravelUIConfirmPatch
    {
        private static void Postfix(OpenFastTravelUI __instance, FastTravelLocations targetLocation)
        {
            if (!Config.DebugLogging) return;
            try
            {
                Log.Info($"{Tag} Destination picked: {targetLocation} -> scene " +
                         $"{FastTravelScenes.GetSceneName(targetLocation)} (FSM '{__instance.Fsm?.Name}', state " +
                         $"'{__instance.State?.Name}')");
            }
            catch (Exception e)
            {
                Log.Debug($"{Tag} log failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(GetFastTravelScene), nameof(GetFastTravelScene.OnEnter))]
    private static class GetFastTravelScenePatch
    {
        private static void Postfix(GetFastTravelScene __instance)
        {
            if (!Config.DebugLogging) return;
            try
            {
                Log.Info($"{Tag} GetFastTravelScene: {__instance.Location?.Value} -> " +
                         $"{__instance.StoreSceneName?.Value} (FSM '{__instance.Fsm?.Name}' on " +
                         $"{(__instance.Owner != null ? PathOf(__instance.Owner.transform) : "?")})");
            }
            catch (Exception e)
            {
                Log.Debug($"{Tag} log failed: {e.Message}");
            }
        }
    }
}
