using BepInEx;
using HarmonyLib;
using SSMP.Api.Client;
using SSMP.Api.Server;

namespace SSMPEnemyHealthSync;

[BepInAutoPlugin(id: "com.ssmp.enemyhealthsync", version: Utils.Config.Version)]
[BepInDependency("ssmp", BepInDependency.DependencyFlags.HardDependency)]
public partial class SSMPEnemyHealthSyncPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Utils.Config.Init(Config);
        Utils.Log.SetLogger(Logger);

        ClientAddon.RegisterAddon(new Client.Client());
        ServerAddon.RegisterAddon(new Server.Server());

        new Harmony(Id).PatchAll();

        Logger.LogInfo($"Plugin {Name} ({Id}) has loaded!");
    }

    private void Update()
    {
        Client.DownedState.Update();
        Client.EnemyTracker.Update();
        Client.TeleportBack.Update();
        Client.BellBeastProbe.Update();
    }

    private void OnGUI()
    {
        Client.ScreenOverlay.Draw();
    }
}
