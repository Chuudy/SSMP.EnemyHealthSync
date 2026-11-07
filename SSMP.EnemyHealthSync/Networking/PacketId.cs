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
}
