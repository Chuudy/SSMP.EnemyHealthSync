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
/// Snapshot of one enemy on its owner, applied to the puppet copies of the other players.
/// Unreliable: a newer snapshot always follows, so lost ones don't matter.
/// </summary>
public sealed class EnemyStatePacket : IPacketData
{
    public const ushort NoClip = ushort.MaxValue;

    private const byte FlagScaleXNegative = 1;
    private const byte FlagActive = 2;
    private const byte FlagVisible = 4;
    private const byte FlagKinematic = 8;

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

    /// <summary>
    /// Bit i: the DamageHero of hitbox object i can hurt the hero (enabled, damage above 0).
    /// Enemy FSMs switch contact damage on and off (asleep, waking, attacking); puppets copy it.
    /// </summary>
    public uint HitboxDamaging { get; set; }

    /// <summary>
    /// Whether the enemy's own renderer is on. FSMs hide enemies that are burrowed, dormant or waiting
    /// for an arena wave, and show them when they emerge or enter; puppets copy it.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Whether the enemy's Rigidbody2D is kinematic on the owner (e.g. while burrowed). A new owner
    /// takes this over, so an enemy taken over underground doesn't fall through the floor.
    /// </summary>
    public bool Kinematic { get; set; }

    /// <summary>
    /// The enemy's tk2d sprite colour as RGBA bytes (R in the lowest byte). FSMs fade enemies in by
    /// tweening it from transparent (e.g. flying in at an arena wave); puppets copy it.
    /// </summary>
    public uint SpriteColor { get; set; } = 0xFFFFFFFF;

    /// <summary>Bit i: visual object i (effects, particle emitters) is active (activeSelf).</summary>
    public uint VisualActive { get; set; }

    /// <summary>Bit i: the renderer of visual object i is enabled.</summary>
    public uint VisualRenderer { get; set; }

    /// <summary>Bit i: the particle system of visual object i is emitting or still has particles (e.g. dust cues).</summary>
    public uint VisualParticles { get; set; }

    /// <summary>
    /// Active state of each of the enemy's PlayMaker FSMs (in hierarchy order), as an index into
    /// that FSM's state list, or <see cref="NoFsmState"/>. A new owner resumes the AI from these
    /// states instead of the FSM's start state (which is often "asleep").
    /// </summary>
    public ushort[] FsmStates { get; set; } = [];

    public const ushort NoFsmState = ushort.MaxValue;
    public const int MaxFsms = 16;

    public void WriteData(IPacket packet)
    {
        packet.Write(EnemyId);
        packet.Write(X);
        packet.Write(Y);
        byte flags = 0;
        if (ScaleXNegative) flags |= FlagScaleXNegative;
        if (Active) flags |= FlagActive;
        if (Visible) flags |= FlagVisible;
        if (Kinematic) flags |= FlagKinematic;
        packet.Write(flags);
        packet.Write(Clip);
        packet.Write(Hp);
        packet.Write(HitboxActive);
        packet.Write(HitboxColliderEnabled);
        packet.Write(HitboxDamaging);
        packet.Write(VisualActive);
        packet.Write(VisualRenderer);
        packet.Write(VisualParticles);
        packet.Write(SpriteColor);

        var fsmCount = (byte) System.Math.Min(FsmStates.Length, MaxFsms);
        packet.Write(fsmCount);
        for (var i = 0; i < fsmCount; i++)
        {
            packet.Write(FsmStates[i]);
        }
    }

    public void ReadData(IPacket packet)
    {
        EnemyId = packet.ReadUInt();
        X = packet.ReadFloat();
        Y = packet.ReadFloat();
        var flags = packet.ReadByte();
        ScaleXNegative = (flags & FlagScaleXNegative) != 0;
        Active = (flags & FlagActive) != 0;
        Visible = (flags & FlagVisible) != 0;
        Kinematic = (flags & FlagKinematic) != 0;
        Clip = packet.ReadUShort();
        Hp = packet.ReadInt();
        HitboxActive = packet.ReadUInt();
        HitboxColliderEnabled = packet.ReadUInt();
        HitboxDamaging = packet.ReadUInt();
        VisualActive = packet.ReadUInt();
        VisualRenderer = packet.ReadUInt();
        VisualParticles = packet.ReadUInt();
        SpriteColor = packet.ReadUInt();

        var fsmCount = System.Math.Min((int) packet.ReadByte(), MaxFsms);
        FsmStates = new ushort[fsmCount];
        for (var i = 0; i < fsmCount; i++)
        {
            FsmStates[i] = packet.ReadUShort();
        }
    }
}

/// <summary>
/// A player went down (would have died, waits for a revive) or got back up / died for real.
/// From client to server PlayerId is ignored (the sender is used); from server to client it is the
/// player whose state changed. While downed, the downed player re-sends it after every revive hit
/// so everyone in the room can show the revive progress.
/// </summary>
public sealed class PlayerDownedPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => false;

    public ushort PlayerId { get; set; }
    public bool Downed { get; set; }

    /// <summary>Revive hits received so far.</summary>
    public byte ReviveHits { get; set; }

    /// <summary>Revive hits needed in total (the downed player's ReviveHits setting).</summary>
    public byte ReviveHitsNeeded { get; set; }

    public void WriteData(IPacket packet)
    {
        packet.Write(PlayerId);
        packet.Write(Downed);
        packet.Write(ReviveHits);
        packet.Write(ReviveHitsNeeded);
    }

    public void ReadData(IPacket packet)
    {
        PlayerId = packet.ReadUShort();
        Downed = packet.ReadBool();
        ReviveHits = packet.ReadByte();
        ReviveHitsNeeded = packet.ReadByte();
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

/// <summary>
/// A PlayMaker event that reached a puppet's FSM, for the enemy's owner to deliver to its copy.
/// Enemies call each other with events (e.g. a scout calling in a flying Skarr, an arena starting
/// a wave); those only fire on the game that runs the sender, where the receiver may be a puppet.
/// </summary>
public sealed class EnemyFsmEventPacket : IPacketData
{
    public bool IsReliable => true;
    public bool DropReliableDataIfNewerExists => false;

    public uint EnemyId { get; set; }

    /// <summary>Index of the FSM among the enemy's FSMs (hierarchy order).</summary>
    public byte FsmIndex { get; set; }

    public string EventName { get; set; } = "";

    public void WriteData(IPacket packet)
    {
        packet.Write(EnemyId);
        packet.Write(FsmIndex);
        packet.Write(EventName ?? "");
    }

    public void ReadData(IPacket packet)
    {
        EnemyId = packet.ReadUInt();
        FsmIndex = packet.ReadByte();
        EventName = packet.ReadString();
    }
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
            PacketId.EnemyFsmEvent => new PacketDataCollection<EnemyFsmEventPacket>(),
            _ => throw new System.ArgumentOutOfRangeException(nameof(packetId), packetId, null)
        };
    }
}
