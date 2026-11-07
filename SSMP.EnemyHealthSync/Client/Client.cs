using System;
using SSMP.Api.Client;
using SSMP.Api.Client.Networking;
using SSMPEnemyHealthSync.Networking;
using SSMPEnemyHealthSync.Utils;

namespace SSMPEnemyHealthSync.Client;

internal class Client : ClientAddon
{
    protected override string Name => Config.ModName;
    protected override string Version => Config.Version;
    public override uint ApiVersion => Config.SSMPApiVersion;
    public override bool NeedsNetwork => true;

    internal static IClientApi? Api { get; private set; }
    private static IClientAddonNetworkSender<PacketId>? _sender;

    internal static bool IsConnected => Api?.NetClient?.IsConnected == true;

    public override void Initialize(IClientApi clientApi)
    {
        Api = clientApi;

        _sender = clientApi.NetClient.GetNetworkSender<PacketId>(this);
        var receiver = clientApi.NetClient.GetNetworkReceiver<PacketId>(this, Packets.Instantiate);

        // Collections are unwrapped by SSMP: the handler is called once per instance.
        receiver.RegisterPacketHandler<EnemyDamagePacket>(PacketId.EnemyDamage,
            p => EnemyTracker.OnRemoteDamage(p.EnemyId, p.Damage));
        receiver.RegisterPacketHandler<EnemyDeathPacket>(PacketId.EnemyDeath,
            p => EnemyTracker.OnRemoteDeath(p.EnemyId));

        clientApi.ClientManager.ConnectEvent += EnemyTracker.OnConnected;
        clientApi.ClientManager.DisconnectEvent += EnemyTracker.OnDisconnected;

        EnemyTracker.Init();

        Log.Info("Client addon initialized");
    }

    internal static void SendSceneEntered(string sceneName)
    {
        Send(() => _sender!.SendSingleData(PacketId.SceneEntered, new SceneEnteredPacket { SceneName = sceneName }));
    }

    internal static void SendDamage(uint enemyId, int damage)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDamage,
            new EnemyDamagePacket { EnemyId = enemyId, Damage = damage }));
    }

    internal static void SendDeath(uint enemyId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDeath, new EnemyDeathPacket { EnemyId = enemyId }));
    }

    private static void Send(Action send)
    {
        if (_sender == null || !IsConnected)
        {
            return;
        }

        try
        {
            send();
        }
        catch (Exception e)
        {
            Log.Error($"Failed to send packet: {e.Message}");
        }
    }
}
