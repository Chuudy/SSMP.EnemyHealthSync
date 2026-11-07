using System;
using SSMP.Api.Server;
using SSMP.Api.Server.Networking;
using SSMPEnemyHealthSync.Networking;
using SSMPEnemyHealthSync.Utils;

namespace SSMPEnemyHealthSync.Server;

internal class Server : ServerAddon
{
    protected override string Name => Config.ModName;
    protected override string Version => Config.Version;
    public override uint ApiVersion => Config.SSMPApiVersion;
    public override bool NeedsNetwork => true;

    private static IServerAddonNetworkSender<PacketId>? _sender;

    public override void Initialize(IServerApi serverApi)
    {
        _sender = serverApi.NetServer.GetNetworkSender<PacketId>(this);
        var receiver = serverApi.NetServer.GetNetworkReceiver<PacketId>(this, Packets.Instantiate);

        receiver.RegisterPacketHandler<SceneEnteredPacket>(PacketId.SceneEntered, EnemyRelay.OnSceneEntered);
        // Collections are unwrapped by SSMP: the handler is called once per instance.
        receiver.RegisterPacketHandler<EnemyDamagePacket>(PacketId.EnemyDamage, EnemyRelay.OnDamage);
        receiver.RegisterPacketHandler<EnemyDeathPacket>(PacketId.EnemyDeath, EnemyRelay.OnDeath);

        serverApi.ServerManager.PlayerDisconnectEvent += player => EnemyRelay.OnPlayerDisconnect(player.Id);
        serverApi.ServerManager.ServerShutdownEvent += EnemyRelay.Clear;

        Log.Info("Server addon initialized");
    }

    internal static void SendDamage(uint enemyId, int damage, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDamage,
            new EnemyDamagePacket { EnemyId = enemyId, Damage = damage }, playerId));
    }

    internal static void SendDeath(uint enemyId, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDeath,
            new EnemyDeathPacket { EnemyId = enemyId }, playerId));
    }

    private static void Send(Action send)
    {
        if (_sender == null)
        {
            return;
        }

        try
        {
            send();
        }
        catch (Exception e)
        {
            // e.g. the player disconnected between lookup and send
            Log.Debug($"Failed to send packet: {e.Message}");
        }
    }
}
