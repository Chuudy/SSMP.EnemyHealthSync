using HarmonyLib;
using HutongGames.PlayMaker;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Every PlayMaker event an FSM receives goes through Fsm.ProcessEvent (SendEvent, SendEventByName,
/// EventRegister, broadcasts, arena waves). Lets <see cref="EnemyTracker.OnFsmEvent"/> drop events
/// on puppets and send the ones the owner's enemy is waiting for to the owner.
/// </summary>
[HarmonyPatch(typeof(Fsm), nameof(Fsm.ProcessEvent))]
internal static class FsmProcessEventPatch
{
    private static bool Prefix(Fsm __instance, FsmEvent fsmEvent)
    {
        if (fsmEvent == null)
        {
            return true;
        }

        try
        {
            return EnemyTracker.OnFsmEvent(__instance.FsmComponent, fsmEvent.Name);
        }
        catch (System.Exception e)
        {
            Utils.Log.Debug($"FSM event hook failed: {e.Message}");
            return true;
        }
    }
}

/// <summary>
/// EventRegister (global events such as one enemy calling another in) delivers through this
/// extension, which returns early for FSMs that never started. A puppet's FSMs may never have
/// started, so its events are routed here as well.
/// </summary>
[HarmonyPatch(typeof(Extensions), nameof(Extensions.SendEventRecursive), typeof(Fsm), typeof(string))]
internal static class FsmSendEventRecursivePatch
{
    private static bool Prefix(Fsm fsm, string eventName, ref bool __result)
    {
        if (fsm == null)
        {
            return true;
        }

        try
        {
            if (EnemyTracker.OnFsmEvent(fsm.FsmComponent, eventName))
            {
                return true;
            }
        }
        catch (System.Exception e)
        {
            Utils.Log.Debug($"FSM event hook failed: {e.Message}");
            return true;
        }

        __result = false;
        return false;
    }
}
