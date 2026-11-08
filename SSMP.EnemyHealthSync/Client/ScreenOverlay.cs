using System;
using GlobalEnums;
using SSMPEnemyHealthSync.Utils;
using UnityEngine;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// Screen-space hints drawn with IMGUI: a banner in the middle of the screen (e.g. "X is down!") and
/// arrows at the screen edge pointing to teammates in the room who are off screen.
/// </summary>
internal static class ScreenOverlay
{
    private const float EdgeMargin = 40f;
    private const float ArrowSize = 28f;
    private const float FadeSeconds = 0.4f;

    private static string? _bannerText;
    private static Color _bannerColor = Color.white;
    private static float _bannerSize;
    private static float _bannerUntil;
    private static float _bannerDuration;

    private static Texture2D? _arrow;
    private static Font? _font;
    private static bool _fontSearched;
    private static GUIStyle? _bannerStyle;
    private static GUIStyle? _nameStyle;

    /// <summary>Show a message in the middle of the screen, replacing the current one.</summary>
    internal static void ShowBanner(string text, Color color, float seconds, bool large = true)
    {
        _bannerText = text;
        _bannerColor = color;
        _bannerSize = large ? 0.055f : 0.035f;
        _bannerDuration = seconds;
        _bannerUntil = Time.unscaledTime + seconds;
    }

    internal static void ClearBanner() => _bannerText = null;

    /// <summary>Called from the plugin's OnGUI.</summary>
    internal static void Draw()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        try
        {
            EnsureStyles();
            DrawMarkers();
            DrawBanner();
        }
        catch (Exception e)
        {
            Log.Debug($"Overlay draw failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ banner

    private static void DrawBanner()
    {
        if (_bannerText == null)
        {
            return;
        }

        var left = _bannerUntil - Time.unscaledTime;
        if (left <= 0f)
        {
            _bannerText = null;
            return;
        }

        var alpha = Mathf.Clamp01(left / FadeSeconds);
        // Pop in slightly over the first moments
        var age = _bannerDuration - left;
        var scale = 1f + 0.15f * Mathf.Clamp01(1f - age / 0.25f);

        var style = _bannerStyle!;
        style.fontSize = Mathf.RoundToInt(Screen.height * _bannerSize * scale);
        // Near the top of the screen, below the HUD row, so it doesn't cover the action
        var rect = new Rect(0f, Screen.height * 0.08f, Screen.width, Screen.height * 0.1f);
        DrawOutlined(rect, _bannerText, style, WithAlpha(_bannerColor, alpha), new Color(0f, 0f, 0f, alpha * 0.85f),
            Mathf.Max(2f, style.fontSize / 18f));
    }

    // ------------------------------------------------------------------ edge markers

    private static void DrawMarkers()
    {
        if (!Config.OffscreenMarkers || !Client.IsConnected)
        {
            return;
        }

        var gm = GameManager.SilentInstance;
        if (gm == null || gm.GameState != GameState.PLAYING || gm.IsInSceneTransition)
        {
            return;
        }

        var cam = GameCameras.SilentInstance != null ? GameCameras.SilentInstance.mainCamera : Camera.main;
        var players = Client.Api?.ClientManager?.Players;
        if (cam == null || players == null)
        {
            return;
        }

        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var halfW = center.x - EdgeMargin;
        var halfH = center.y - EdgeMargin;

        foreach (var player in players)
        {
            if (!player.IsInLocalScene || player.PlayerObject == null)
            {
                continue;
            }

            var world = player.PlayerObject.transform.position;
            var screen = cam.WorldToScreenPoint(world);
            // GUI coordinates have y pointing down
            var p = new Vector2(screen.x, Screen.height - screen.y);
            var behind = screen.z < 0f;
            if (behind)
            {
                p = center - (p - center);
            }

            if (!behind && p.x >= 0f && p.x <= Screen.width && p.y >= 0f && p.y <= Screen.height)
            {
                continue; // On screen
            }

            var dir = p - center;
            if (dir.sqrMagnitude < 0.01f)
            {
                continue;
            }

            var t = Mathf.Min(
                Mathf.Abs(dir.x) > 0.001f ? halfW / Mathf.Abs(dir.x) : float.MaxValue,
                Mathf.Abs(dir.y) > 0.001f ? halfH / Mathf.Abs(dir.y) : float.MaxValue);
            var pos = center + dir * t;
            var downed = DownedState.IsRemoteDowned(player.Id);

            Color color;
            float size;
            if (downed)
            {
                var pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f);
                color = Color.Lerp(new Color(1f, 0.15f, 0.15f), new Color(1f, 0.6f, 0.6f), pulse);
                size = ArrowSize * (1.1f + 0.25f * pulse);
            }
            else
            {
                color = new Color(1f, 1f, 1f, 0.9f);
                size = ArrowSize;
            }

            DrawArrow(pos, dir, size * Screen.height / 1080f, color);

            // Name a little toward the middle of the screen
            var nameStyle = _nameStyle!;
            nameStyle.fontSize = Mathf.RoundToInt(Screen.height * 0.02f);
            var namePos = pos - dir.normalized * (size * 1.6f * Screen.height / 1080f);
            var label = downed ? $"{player.Username} (down)" : player.Username;
            var nameRect = new Rect(namePos.x - 150f, namePos.y - 20f, 300f, 40f);
            DrawOutlined(nameRect, label, nameStyle, color, new Color(0f, 0f, 0f, 0.8f), 1.5f);
        }
    }

    private static void DrawArrow(Vector2 pos, Vector2 dir, float size, Color color)
    {
        var angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        var matrix = GUI.matrix;
        var oldColor = GUI.color;
        try
        {
            GUIUtility.RotateAroundPivot(angle, pos);
            // Dark backing so the arrow shows on bright backgrounds too
            var back = size * 1.35f;
            GUI.color = new Color(0f, 0f, 0f, 0.75f * color.a);
            GUI.DrawTexture(new Rect(pos.x - back * 0.5f, pos.y - back * 0.5f, back, back), GetArrow());
            GUI.color = color;
            GUI.DrawTexture(new Rect(pos.x - size * 0.5f, pos.y - size * 0.5f, size, size), GetArrow());
        }
        finally
        {
            GUI.matrix = matrix;
            GUI.color = oldColor;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void DrawOutlined(Rect rect, string text, GUIStyle style, Color color, Color outline, float width)
    {
        var old = style.normal.textColor;
        style.normal.textColor = outline;
        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                GUI.Label(new Rect(rect.x + x * width, rect.y + y * width, rect.width, rect.height), text, style);
            }
        }

        style.normal.textColor = color;
        GUI.Label(rect, text, style);
        style.normal.textColor = old;
    }

    private static Color WithAlpha(Color c, float a) => new(c.r, c.g, c.b, c.a * a);

    private static void EnsureStyles()
    {
        if (!_fontSearched)
        {
            _fontSearched = true;
            _font = FindGameFont();
        }

        if (_bannerStyle == null)
        {
            _bannerStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                richText = false
            };
            if (_font != null) _bannerStyle.font = _font;
        }

        if (_nameStyle == null)
        {
            _nameStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                richText = false
            };
            if (_font != null) _nameStyle.font = _font;
        }
    }

    /// <summary>A dynamic font from the game, so the text fits its look; the IMGUI default otherwise.</summary>
    private static Font? FindGameFont()
    {
        try
        {
            Font? any = null;
            foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font == null || !font.dynamic)
                {
                    continue;
                }

                var name = font.name.ToLowerInvariant();
                if (name.Contains("trajan") || name.Contains("perpetua"))
                {
                    Log.Debug($"Overlay font: {font.name}");
                    return font;
                }

                any ??= font;
            }

            Log.Debug($"Overlay font: {(any != null ? any.name : "IMGUI default")}");
            return any;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A white triangle pointing right, with soft edges.</summary>
    private static Texture2D GetArrow()
    {
        if (_arrow != null)
        {
            return _arrow;
        }

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Triangle with its tip at the right middle and its base on the left
                var fx = (x + 0.5f) / size;
                var fy = Mathf.Abs((y + 0.5f) / size - 0.5f) * 2f; // 0 in the middle, 1 at the edges
                var limit = 1f - fx; // How wide the triangle is at this x
                var inside = Mathf.Clamp01((limit - fy) * size * 0.5f) * Mathf.Clamp01(fx * size * 0.5f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte) (inside * 255));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        _arrow = tex;
        return tex;
    }
}
