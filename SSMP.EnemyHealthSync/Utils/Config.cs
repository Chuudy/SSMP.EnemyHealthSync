using BepInEx.Configuration;

namespace SSMPEnemyHealthSync.Utils;

internal static class Config
{
    public const string ModName = "SSMP.EnemyHealthSync";
    public const string Version = "1.3.3";
    public const uint SSMPApiVersion = 1;

    private static ConfigEntry<bool>? _enabled;
    private static ConfigEntry<bool>? _syncHealth;
    private static ConfigEntry<bool>? _syncDeath;
    private static ConfigEntry<bool>? _syncMovement;
    private static ConfigEntry<int>? _stateSendRate;
    private static ConfigEntry<bool>? _handover;
    private static ConfigEntry<float>? _handoverRange;
    private static ConfigEntry<int>? _bossHpThreshold;
    private static ConfigEntry<bool>? _downedEnabled;
    private static ConfigEntry<float>? _bleedOutSeconds;
    private static ConfigEntry<int>? _reviveHits;
    private static ConfigEntry<int>? _reviveHealth;
    private static ConfigEntry<float>? _reviveRange;
    private static ConfigEntry<bool>? _debugLogging;

    public static bool Enabled => _enabled?.Value ?? true;
    public static bool SyncHealth => Enabled && (_syncHealth?.Value ?? true);
    public static bool SyncDeath => Enabled && (_syncDeath?.Value ?? true);
    public static bool SyncMovement => Enabled && (_syncMovement?.Value ?? true);
    public static int StateSendRate => _stateSendRate?.Value ?? 20;
    public static bool Handover => _handover?.Value ?? true;
    public static float HandoverRange => _handoverRange?.Value ?? 15f;
    public static int BossHpThreshold => _bossHpThreshold?.Value ?? 300;
    public static bool DownedEnabled => Enabled && (_downedEnabled?.Value ?? true);
    public static float BleedOutSeconds => _bleedOutSeconds?.Value ?? 30f;
    public static int ReviveHits => _reviveHits?.Value ?? 3;
    public static int ReviveHealth => _reviveHealth?.Value ?? 3;
    public static float ReviveRange => _reviveRange?.Value ?? 3f;
    public static bool DebugLogging => _debugLogging?.Value ?? false;

    public static void Init(ConfigFile config)
    {
        _enabled = config.Bind("General", "Enabled", true, "Enable the enemy health sync mod");
        _syncHealth = config.Bind("General", "SyncHealth", true,
            "Share damage dealt to enemies with other players in the same room");
        _syncDeath = config.Bind("General", "SyncDeath", true,
            "When an enemy dies for one player, it dies for everyone in the same room");
        _syncMovement = config.Bind("General", "SyncMovement", true,
            "Each enemy's AI runs on one player's game; everyone else sees copies that follow its " +
            "movement, animations (attacks, flinches) and attack hitboxes");
        _stateSendRate = config.Bind("Network", "StateSendRate", 20, new ConfigDescription(
            "How many times per second enemy owners send enemy movement/animation updates",
            new AcceptableValueRange<int>(5, 60)));
        _handover = config.Bind("Ownership", "Handover", true,
            "Hand each enemy's AI to the player nearest to it, so enemies chase and attack every player. " +
            "Off: the first player in a room runs every enemy (enemies only target that player)");
        _handoverRange = config.Bind("Ownership", "HandoverRange", 15f,
            "An enemy is only handed to a player within this distance of it");
        _bossHpThreshold = config.Bind("Ownership", "BossHpThreshold", 300,
            "Enemies with at least this much starting HP (or a boss-style death) count as bosses and " +
            "are handed over more cautiously");
        _downedEnabled = config.Bind("Downed", "Enabled", true,
            "A hit that would kill you while a teammate is in the room knocks you down instead; " +
            "teammates revive you by attacking next to you");
        _bleedOutSeconds = config.Bind("Downed", "BleedOutSeconds", 30f,
            "Seconds a downed player waits for a revive before dying (0 = wait forever)");
        _reviveHits = config.Bind("Downed", "ReviveHits", 3, new ConfigDescription(
            "Attacks next to a downed player needed to revive them",
            new AcceptableValueRange<int>(1, 20)));
        _reviveHealth = config.Bind("Downed", "ReviveHealth", 3, new ConfigDescription(
            "Masks a revived player gets back up with",
            new AcceptableValueRange<int>(1, 20)));
        _reviveRange = config.Bind("Downed", "ReviveRange", 3f,
            "How close you must be to a downed teammate for your attacks to count toward reviving them");
        _debugLogging = config.Bind("Debug", "DebugLogging", false,
            "Log every enemy damage/death packet sent and received, and ownership changes");
    }
}
