using BepInEx.Configuration;

namespace SSMPEnemyHealthSync.Utils;

internal static class Config
{
    public const string ModName = "SSMP.EnemyHealthSync";
    public const string Version = "1.1.0";
    public const uint SSMPApiVersion = 1;

    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<bool>? _syncHealth;
    private static ConfigEntry<bool>? _syncDeath;
    private static ConfigEntry<bool>? _syncMovement;
    private static ConfigEntry<int>? _stateSendRate;
    private static ConfigEntry<bool>? _handover;
    private static ConfigEntry<float>? _handoverRange;
    private static ConfigEntry<int>? _bossHpThreshold;
    private static ConfigEntry<bool>? _debugLogging;

    public static bool Enabled => _enabled?.Value ?? true;
    public static bool SyncHealth => Enabled && (_syncHealth?.Value ?? true);
    public static bool SyncDeath => Enabled && (_syncDeath?.Value ?? true);
    public static bool SyncMovement => Enabled && (_syncMovement?.Value ?? true);
    public static int StateSendRate => _stateSendRate?.Value ?? 20;
    public static bool Handover => _handover?.Value ?? true;
    public static float HandoverRange => _handoverRange?.Value ?? 15f;
    public static int BossHpThreshold => _bossHpThreshold?.Value ?? 300;
    public static bool DebugLogging => _debugLogging?.Value ?? false;

    public static void Init(ConfigFile config)
    {
        _enabled = config.Bind("General", "Enabled", true, "Enable the enemy health sync mod");
        _syncHealth = config.Bind("General", "SyncHealth", true,
            "Share damage dealt to enemies with other players in the same room");
        _syncDeath = config.Bind("General", "SyncDeath", true,
            "When an enemy dies for one player, it dies for everyone in the same room");
        _syncMovement = config.Bind("General", "SyncMovement", true,
            "The first player in a room runs its enemies; everyone else sees copies that follow their " +
            "movement, animations (attacks, flinches) and attack hitboxes");
        _stateSendRate = config.Bind("Network", "StateSendRate", 20, new ConfigDescription(
            "How many times per second the room host sends enemy movement/animation updates",
            new AcceptableValueRange<int>(5, 60)));
        _handover = config.Bind("Ownership", "Handover", true,
            "Hand each enemy's AI to the player nearest to it, so enemies chase and attack every player. " +
            "Off: the first player in a room runs every enemy (enemies only target that player)");
        _handoverRange = config.Bind("Ownership", "HandoverRange", 15f,
            "An enemy is only handed to a player within this distance of it");
        _bossHpThreshold = config.Bind("Ownership", "BossHpThreshold", 300,
            "Enemies with at least this much starting HP (or a boss-style death) count as bosses and " +
            "are handed over more cautiously");
        _debugLogging = config.Bind("Debug", "DebugLogging", false,
            "Log every enemy damage/death packet sent and received, and ownership changes");
    }
}
