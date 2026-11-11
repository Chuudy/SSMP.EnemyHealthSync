namespace SSMPEnemyHealthSync.Networking;

/// <summary>
/// Packet IDs for the SSMP.EnemyHealthSync addon. The same IDs are used in both directions.
/// </summary>
public enum PacketId : byte
{
    /// <summary>Client -> server: the client's active scene changed (or it just connected).</summary>
    SceneEntered = 0,

    /// <summary>Both directions: a player dealt damage to an enemy (sent as a collection).</summary>
    EnemyDamage = 1,

    /// <summary>Both directions: an enemy died (sent as a collection).</summary>
    EnemyDeath = 2,

    /// <summary>Server -> client: which enemies of its current room the client owns (runs the AI of).</summary>
    RoomOwnership = 3,

    /// <summary>Enemy owner -> server -> others in the room: enemy position/animation/hitboxes (unreliable collection).</summary>
    EnemyState = 4,

    /// <summary>Client -> server: the owner of an enemy hands it over to another player (collection).</summary>
    Handover = 5,

    /// <summary>Client -> server -> others in the room: a player went down or got back up (collection).</summary>
    PlayerDowned = 6,

    /// <summary>Client -> server -> downed player: a teammate hit the downed player to revive them (collection).</summary>
    ReviveHit = 7,

    /// <summary>
    /// Client -> server -> the enemy's owner: a PlayMaker event reached our puppet copy of an
    /// enemy that its owner's copy is waiting for (e.g. another enemy calling it in) (collection).
    /// </summary>
    EnemyFsmEvent = 8,

    /// <summary>
    /// Enemy owner -> server -> others in the room: the owner's copy of an enemy was removed without
    /// dying (e.g. it dug away for good), so everyone removes theirs (collection).
    /// </summary>
    EnemyDespawn = 9,

    /// <summary>Client -> server -> others in the room: an arena (BattleScene) was cleared (collection).</summary>
    ArenaCleared = 10,

    /// <summary>
    /// Enemy owner -> server -> others in the room: the values of an enemy's FSM variables, so a
    /// new owner continues with the old owner's (collection).
    /// </summary>
    EnemyFsmVars = 11,
}
