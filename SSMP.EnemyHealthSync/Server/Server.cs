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
        receiver.RegisterPacketHandler<EnemyStatePacket>(PacketId.EnemyState, EnemyRelay.OnState);
        receiver.RegisterPacketHandler<HandoverPacket>(PacketId.Handover, EnemyRelay.OnHandover);
        receiver.RegisterPacketHandler<PlayerDownedPacket>(PacketId.PlayerDowned, EnemyRelay.OnDowned);
        receiver.RegisterPacketHandler<ReviveHitPacket>(PacketId.ReviveHit, EnemyRelay.OnReviveHit);
        receiver.RegisterPacketHandler<EnemyFsmEventPacket>(PacketId.EnemyFsmEvent, EnemyRelay.OnFsmEvent);
        receiver.RegisterPacketHandler<EnemyDespawnPacket>(PacketId.EnemyDespawn, EnemyRelay.OnDespawn);
        receiver.RegisterPacketHandler<ArenaClearedPacket>(PacketId.ArenaCleared, EnemyRelay.OnArenaCleared);
        receiver.RegisterPacketHandler<EnemyFsmVarsPacket>(PacketId.EnemyFsmVars, EnemyRelay.OnFsmVars);
        receiver.RegisterPacketHandler<EnemyProjectilePacket>(PacketId.EnemyProjectile, EnemyRelay.OnProjectile);

        serverApi.ServerManager.PlayerDisconnectEvent += player => EnemyRelay.OnPlayerDisconnect(player.Id);
        serverApi.ServerManager.ServerShutdownEvent += EnemyRelay.Clear;

        Log.Info("Server addon initialized");
    }

    // Received packets are relayed as-is; they are only read when written out again.
    internal static void SendDamage(EnemyDamagePacket packet, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDamage, packet, playerId));
    }

    internal static void SendState(EnemyStatePacket packet, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyState, packet, playerId));
    }

    internal static void SendFsmEvent(EnemyFsmEventPacket packet, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyFsmEvent, packet, playerId));
    }

    internal static void SendDespawn(EnemyDespawnPacket packet, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDespawn, packet, playerId));
    }

    internal static void SendArenaCleared(ArenaClearedPacket packet, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.ArenaCleared, packet, playerId));
    }

    internal static void SendFsmVars(EnemyFsmVarsPacket packet, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyFsmVars, packet, playerId));
    }

    internal static void SendProjectile(EnemyProjectilePacket packet, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyProjectile, packet, playerId));
    }

    internal static void SendOwnership(RoomOwnershipPacket packet, ushort playerId)
    {
        Send(() => _sender!.SendSingleData(PacketId.RoomOwnership, packet, playerId));
    }

    internal static void SendDeath(uint enemyId, ushort playerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDeath,
            new EnemyDeathPacket { EnemyId = enemyId }, playerId));
    }

    /// <summary>
    /// Tell <paramref name="toPlayerId"/> that <paramref name="downedPlayerId"/> went down, got up,
    /// or how far their revive has come.
    /// </summary>
    internal static void SendDowned(ushort downedPlayerId, bool downed, byte reviveHits, byte reviveHitsNeeded,
        ushort toPlayerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.PlayerDowned,
            new PlayerDownedPacket
            {
                PlayerId = downedPlayerId,
                Downed = downed,
                ReviveHits = reviveHits,
                ReviveHitsNeeded = reviveHitsNeeded
            }, toPlayerId));
    }

    internal static void SendReviveHit(ushort downedPlayerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.ReviveHit,
            new ReviveHitPacket { TargetPlayerId = downedPlayerId }, downedPlayerId));
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
