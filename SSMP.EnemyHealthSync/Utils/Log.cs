using BepInEx.Logging;

namespace SSMPEnemyHealthSync.Utils;

internal static class Log
{
    private static ManualLogSource? _logger;

    internal static void SetLogger(ManualLogSource logger) => _logger = logger;

    internal static void Info(string message) => _logger?.LogInfo(message);
    internal static void Warn(string message) => _logger?.LogWarning(message);
    internal static void Error(string message) => _logger?.LogError(message);

    /// <summary>Only logged when the "DebugLogging" config option is on.</summary>
    internal static void Debug(string message)
    {
        if (Config.DebugLogging)
        {
            _logger?.LogInfo(message);
        }
    }
}
