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
        receiver.RegisterPacketHandler<EnemyDamagePacket>(PacketId.EnemyDamage, EnemyTracker.OnRemoteDamage);
        receiver.RegisterPacketHandler<EnemyDeathPacket>(PacketId.EnemyDeath,
            p => EnemyTracker.OnRemoteDeath(p.EnemyId));
        receiver.RegisterPacketHandler<EnemyStatePacket>(PacketId.EnemyState, EnemyTracker.OnRemoteState);
        receiver.RegisterPacketHandler<RoomOwnershipPacket>(PacketId.RoomOwnership, EnemyTracker.OnRoomOwnership);
        receiver.RegisterPacketHandler<PlayerDownedPacket>(PacketId.PlayerDowned,
            p => DownedState.OnRemoteDowned(p.PlayerId, p.Downed, p.ReviveHits, p.ReviveHitsNeeded));
        receiver.RegisterPacketHandler<ReviveHitPacket>(PacketId.ReviveHit, _ => DownedState.OnReviveHit());
        receiver.RegisterPacketHandler<EnemyFsmEventPacket>(PacketId.EnemyFsmEvent, EnemyTracker.OnRemoteFsmEvent);

        clientApi.ClientManager.ConnectEvent += EnemyTracker.OnConnected;
        clientApi.ClientManager.DisconnectEvent += EnemyTracker.OnDisconnected;
        clientApi.ClientManager.DisconnectEvent += DownedState.OnDisconnected;
        clientApi.ClientManager.PlayerLeaveSceneEvent += p => DownedState.OnPlayerLeft(p.Id);
        clientApi.ClientManager.PlayerDisconnectEvent += p => DownedState.OnPlayerLeft(p.Id);

        EnemyTracker.Init();
        DownedState.Init();

        Log.Info("Client addon initialized");
    }

    internal static void SendSceneEntered(string sceneName)
    {
        Send(() => _sender!.SendSingleData(PacketId.SceneEntered, new SceneEnteredPacket { SceneName = sceneName }));
    }

    internal static void SendDamage(uint enemyId, int damage, float direction, byte attackType)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDamage, new EnemyDamagePacket
        {
            EnemyId = enemyId, Damage = damage, Direction = direction, AttackType = attackType
        }));
    }

    internal static void SendState(EnemyStatePacket state)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyState, state));
    }

    internal static void SendHandover(uint enemyId, ushort toPlayerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.Handover,
            new HandoverPacket { EnemyId = enemyId, ToPlayerId = toPlayerId }));
    }

    internal static void SendFsmEvent(uint enemyId, int fsmIndex, string eventName)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyFsmEvent, new EnemyFsmEventPacket
        {
            EnemyId = enemyId, FsmIndex = (byte) fsmIndex, EventName = eventName
        }));
    }

    internal static void SendDeath(uint enemyId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.EnemyDeath, new EnemyDeathPacket { EnemyId = enemyId }));
    }

    /// <summary>Tell the room the local player went down / got up, or how far their revive has come.</summary>
    internal static void SendDowned(bool downed, int reviveHits = 0, int reviveHitsNeeded = 0)
    {
        Send(() => _sender!.SendCollectionData(PacketId.PlayerDowned, new PlayerDownedPacket
        {
            Downed = downed,
            ReviveHits = (byte) Math.Clamp(reviveHits, 0, 255),
            ReviveHitsNeeded = (byte) Math.Clamp(reviveHitsNeeded, 0, 255)
        }));
    }

    internal static void SendReviveHit(ushort targetPlayerId)
    {
        Send(() => _sender!.SendCollectionData(PacketId.ReviveHit,
            new ReviveHitPacket { TargetPlayerId = targetPlayerId }));
    }

    /// <summary>Show a message in the local chat box only.</summary>
    internal static void LocalChat(string message)
    {
        try
        {
            Api?.UiManager?.ChatBox?.AddMessage(message);
        }
        catch (Exception e)
        {
            Log.Debug($"Could not show chat message: {e.Message}");
        }
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
