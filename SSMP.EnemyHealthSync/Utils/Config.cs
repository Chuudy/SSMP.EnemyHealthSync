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
    private static ConfigEntry<bool>? _debugLogging;

    public static bool Enabled => _enabled?.Value ?? true;
    public static bool SyncHealth => Enabled && (_syncHealth?.Value ?? true);
    public static bool SyncDeath => Enabled && (_syncDeath?.Value ?? true);
    public static bool DebugLogging => _debugLogging?.Value ?? false;

    public static void Init(ConfigFile config)
    {
        _enabled = config.Bind("General", "Enabled", true, "Enable the enemy health sync mod");
        _syncHealth = config.Bind("General", "SyncHealth", true,
            "Share damage dealt to enemies with other players in the same room");
        _syncDeath = config.Bind("General", "SyncDeath", true,
            "When an enemy dies for one player, it dies for everyone in the same room");
        _debugLogging = config.Bind("Debug", "DebugLogging", false,
            "Log every enemy damage/death packet sent and received");
    }
}
