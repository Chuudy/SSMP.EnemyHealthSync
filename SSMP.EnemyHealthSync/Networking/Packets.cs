using SSMP.Networking.Packet;
using SSMP.Networking.Packet.Data;

namespace SSMPEnemyHealthSync.Networking;

/// <summary>Client -> server: which scene the client is now in.</summary>
public sealed class SceneEnteredPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => true;

    public string SceneName { get; set; } = "";

    public void WriteData(IPacket packet) => packet.Write(SceneName ?? "");
    public void ReadData(IPacket packet) => SceneName = packet.ReadString();
}

/// <summary>
/// Damage dealt to one enemy. Sent as a delta (not an absolute health value) so that simultaneous
/// hits from several players add up instead of overwriting each other.
/// </summary>
public sealed class EnemyDamagePacket : IPacketData
{
    // Every instance matters, so never drop older ones. Use SendCollectionData so several
    // instances can be sent in one network tick.
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => false;

    public uint EnemyId { get; set; }
    public int Damage { get; set; }

    public void WriteData(IPacket packet)
    {
        packet.Write(EnemyId);
        packet.Write(Damage);
    }

    public void ReadData(IPacket packet)
    {
        EnemyId = packet.ReadUInt();
        Damage = packet.ReadInt();
    }
}

/// <summary>One enemy died.</summary>
public sealed class EnemyDeathPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => false;

    public uint EnemyId { get; set; }

    public void WriteData(IPacket packet) => packet.Write(EnemyId);
    public void ReadData(IPacket packet) => EnemyId = packet.ReadUInt();
}

internal static class Packets
{
    /// <summary>Packet instantiator shared by the client and server receivers.</summary>
    internal static IPacketData Instantiate(PacketId packetId)
    {
        return packetId switch
        {
            PacketId.SceneEntered => new SceneEnteredPacket(),
            PacketId.EnemyDamage => new PacketDataCollection<EnemyDamagePacket>(),
            PacketId.EnemyDeath => new PacketDataCollection<EnemyDeathPacket>(),
            _ => throw new System.ArgumentOutOfRangeException(nameof(packetId), packetId, null)
        };
    }
}
