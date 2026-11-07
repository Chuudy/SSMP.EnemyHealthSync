using System.Collections.Generic;
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
/// Server -> client: which enemies of the given scene this client owns (runs the AI of).
/// Enemies belong to the room host unless handed over, so the client owns an enemy when
/// <c>IsRoomHost XOR Exceptions.Contains(id)</c>: for the room host the exceptions are the enemies
/// handed to others, for everyone else they are the enemies handed to them.
/// The scene name lets clients ignore a late packet about a room they already left.
/// </summary>
public sealed class RoomOwnershipPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => true;

    public string SceneName { get; set; } = "";
    public bool IsRoomHost { get; set; }
    public List<uint> Exceptions { get; set; } = new();

    public void WriteData(IPacket packet)
    {
        packet.Write(SceneName ?? "");
        packet.Write(IsRoomHost);
        var count = (ushort) System.Math.Min(Exceptions.Count, ushort.MaxValue);
        packet.Write(count);
        for (var i = 0; i < count; i++)
        {
            packet.Write(Exceptions[i]);
        }
    }

    public void ReadData(IPacket packet)
    {
        SceneName = packet.ReadString();
        IsRoomHost = packet.ReadBool();
        var count = packet.ReadUShort();
        Exceptions = new List<uint>(count);
        for (var i = 0; i < count; i++)
        {
            Exceptions.Add(packet.ReadUInt());
        }
    }
}

/// <summary>Client -> server: the current owner of an enemy hands it to another player in the room.</summary>
public sealed class HandoverPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => false;

    public uint EnemyId { get; set; }
    public ushort ToPlayerId { get; set; }

    public void WriteData(IPacket packet)
    {
        packet.Write(EnemyId);
        packet.Write(ToPlayerId);
    }

    public void ReadData(IPacket packet)
    {
        EnemyId = packet.ReadUInt();
        ToPlayerId = packet.ReadUShort();
    }
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

    /// <summary>Hit direction in degrees, or NaN when the damage had no direction (e.g. extra damage ticks).</summary>
    public float Direction { get; set; } = float.NaN;

    /// <summary>The game's AttackTypes value of the hit.</summary>
    public byte AttackType { get; set; }

    public void WriteData(IPacket packet)
    {
        packet.Write(EnemyId);
        packet.Write(Damage);
        packet.Write(Direction);
        packet.Write(AttackType);
    }

    public void ReadData(IPacket packet)
    {
        EnemyId = packet.ReadUInt();
        Damage = packet.ReadInt();
        Direction = packet.ReadFloat();
        AttackType = packet.ReadByte();
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

/// <summary>
/// Snapshot of one enemy on the room host, applied to the puppet copies of the other players.
/// Unreliable: a newer snapshot always follows, so lost ones don't matter.
/// </summary>
public sealed class EnemyStatePacket : IPacketData
{
    public const ushort NoClip = ushort.MaxValue;

    private const byte FlagScaleXNegative = 1;
    private const byte FlagActive = 2;

    public bool IsReliable => false;
    public bool DropReliableDataIfNewerExists => false;

    public uint EnemyId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }

    /// <summary>Facing: whether the enemy's localScale.x is negative.</summary>
    public bool ScaleXNegative { get; set; }

    /// <summary>Whether the enemy's GameObject is active (activeSelf).</summary>
    public bool Active { get; set; }

    /// <summary>Index of the playing clip in the enemy's tk2d animation library, or <see cref="NoClip"/>.</summary>
    public ushort Clip { get; set; } = NoClip;

    public int Hp { get; set; }

    /// <summary>Bit i: hitbox object i is active (activeSelf).</summary>
    public uint HitboxActive { get; set; }

    /// <summary>Bit i: the Collider2D of hitbox object i is enabled.</summary>
    public uint HitboxColliderEnabled { get; set; }

    public void WriteData(IPacket packet)
    {
        packet.Write(EnemyId);
        packet.Write(X);
        packet.Write(Y);
        byte flags = 0;
        if (ScaleXNegative) flags |= FlagScaleXNegative;
        if (Active) flags |= FlagActive;
        packet.Write(flags);
        packet.Write(Clip);
        packet.Write(Hp);
        packet.Write(HitboxActive);
        packet.Write(HitboxColliderEnabled);
    }

    public void ReadData(IPacket packet)
    {
        EnemyId = packet.ReadUInt();
        X = packet.ReadFloat();
        Y = packet.ReadFloat();
        var flags = packet.ReadByte();
        ScaleXNegative = (flags & FlagScaleXNegative) != 0;
        Active = (flags & FlagActive) != 0;
        Clip = packet.ReadUShort();
        Hp = packet.ReadInt();
        HitboxActive = packet.ReadUInt();
        HitboxColliderEnabled = packet.ReadUInt();
    }
}

/// <summary>
/// A player went down (would have died, waits for a revive) or got back up / died for real.
/// From client to server PlayerId is ignored (the sender is used); from server to client it is the
/// player whose state changed.
/// </summary>
public sealed class PlayerDownedPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => false;

    public ushort PlayerId { get; set; }
    public bool Downed { get; set; }

    public void WriteData(IPacket packet)
    {
        packet.Write(PlayerId);
        packet.Write(Downed);
    }

    public void ReadData(IPacket packet)
    {
        PlayerId = packet.ReadUShort();
        Downed = packet.ReadBool();
    }
}

/// <summary>A teammate hit a downed player to revive them. TargetPlayerId is the downed player.</summary>
public sealed class ReviveHitPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => false;

    public ushort TargetPlayerId { get; set; }

    public void WriteData(IPacket packet) => packet.Write(TargetPlayerId);
    public void ReadData(IPacket packet) => TargetPlayerId = packet.ReadUShort();
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
            PacketId.RoomOwnership => new RoomOwnershipPacket(),
            PacketId.EnemyState => new PacketDataCollection<EnemyStatePacket>(),
            PacketId.Handover => new PacketDataCollection<HandoverPacket>(),
            PacketId.PlayerDowned => new PacketDataCollection<PlayerDownedPacket>(),
            PacketId.ReviveHit => new PacketDataCollection<ReviveHitPacket>(),
            _ => throw new System.ArgumentOutOfRangeException(nameof(packetId), packetId, null)
        };
    }
}
