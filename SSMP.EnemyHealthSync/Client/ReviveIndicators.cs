using System.Collections.Generic;
using UnityEngine;

namespace SSMPEnemyHealthSync.Client;

/// <summary>
/// A row of pips above each downed player showing their revive progress: one hollow pip per revive
/// hit needed, filled in as hits land. The row pops briefly on every hit, so the attacker sees their
/// swing counted. Sprites are generated at runtime, so no game assets are needed.
/// </summary>
internal static class ReviveIndicators
{
    /// <summary>Key used for the local player's own indicator (SSMP player IDs are ushorts).</summary>
    internal const int LocalKey = -1;

    private const float HeightAboveTarget = 1.9f;
    private const float PipSize = 0.24f;
    private const float PipSpacing = 0.32f;
    private const float PulseSeconds = 0.18f;
    private const float PulseScale = 1.45f;
    private const int TextureSize = 32;

    private static readonly Color FilledColor = new(1f, 0.93f, 0.75f, 1f);
    private static readonly Color EmptyColor = new(1f, 1f, 1f, 0.75f);

    private sealed class Indicator
    {
        internal GameObject? Root;
        internal readonly List<SpriteRenderer> Pips = new();
        internal Transform? Target;
        internal int Hits = -1;
        internal int Needed = -1;
        internal float PulseStart = -999f;
    }

    private static readonly Dictionary<int, Indicator> Indicators = new();
    private static readonly List<int> ToRemove = new();
    private static readonly HashSet<int> Seen = new();

    private static Sprite? _filledSprite;
    private static Sprite? _ringSprite;

    /// <summary>Start a frame: indicators not <see cref="Show"/>n before <see cref="EndFrame"/> are removed.</summary>
    internal static void BeginFrame() => Seen.Clear();

    /// <summary>Show (or update) the indicator above <paramref name="target"/>.</summary>
    internal static void Show(int key, Transform target, int hits, int needed)
    {
        if (needed <= 0)
        {
            return;
        }

        Seen.Add(key);
        if (!Indicators.TryGetValue(key, out var indicator))
        {
            indicator = new Indicator();
            Indicators[key] = indicator;
        }

        // The root lives in the scene, so a scene change destroys it; rebuild when that happened
        if (indicator.Root == null || indicator.Needed != needed || indicator.Target != target)
        {
            Build(indicator, target, needed);
        }

        if (indicator.Hits != hits)
        {
            if (indicator.Hits >= 0 && hits > indicator.Hits)
            {
                indicator.PulseStart = Time.time;
            }

            indicator.Hits = hits;
            for (var i = 0; i < indicator.Pips.Count; i++)
            {
                var filled = i < hits;
                indicator.Pips[i].sprite = filled ? _filledSprite : _ringSprite;
                indicator.Pips[i].color = filled ? FilledColor : EmptyColor;
            }
        }

        Place(indicator);
    }

    /// <summary>Make an indicator pop without changing its progress (the local attacker's own swing).</summary>
    internal static void Pulse(int key)
    {
        if (Indicators.TryGetValue(key, out var indicator))
        {
            indicator.PulseStart = Time.time;
        }
    }

    /// <summary>Remove the indicators that were not shown since <see cref="BeginFrame"/>.</summary>
    internal static void EndFrame()
    {
        ToRemove.Clear();
        foreach (var key in Indicators.Keys)
        {
            if (!Seen.Contains(key))
            {
                ToRemove.Add(key);
            }
        }

        foreach (var key in ToRemove)
        {
            Destroy(Indicators[key]);
            Indicators.Remove(key);
        }
    }

    internal static void Clear()
    {
        foreach (var indicator in Indicators.Values)
        {
            Destroy(indicator);
        }

        Indicators.Clear();
    }

    private static void Build(Indicator indicator, Transform target, int needed)
    {
        Destroy(indicator);
        EnsureSprites();

        indicator.Target = target;
        indicator.Needed = needed;
        indicator.Hits = -1;
        indicator.Root = new GameObject("EnemyHealthSync Revive Indicator");

        // Draw on the same sorting layer as the player, just in front of them
        var targetRenderer = target.GetComponentInChildren<Renderer>();
        var layerId = targetRenderer != null ? targetRenderer.sortingLayerID : 0;
        var order = targetRenderer != null ? targetRenderer.sortingOrder + 50 : 32000;

        var width = (needed - 1) * PipSpacing;
        for (var i = 0; i < needed; i++)
        {
            var pip = new GameObject($"Pip {i}");
            pip.transform.SetParent(indicator.Root.transform, false);
            pip.transform.localPosition = new Vector3(i * PipSpacing - width / 2f, 0f, 0f);

            var sprite = pip.AddComponent<SpriteRenderer>();
            sprite.sprite = _ringSprite;
            sprite.color = EmptyColor;
            sprite.sortingLayerID = layerId;
            sprite.sortingOrder = order;
            indicator.Pips.Add(sprite);
        }
    }

    private static void Place(Indicator indicator)
    {
        if (indicator.Root == null || indicator.Target == null)
        {
            return;
        }

        var pos = indicator.Target.position;
        indicator.Root.transform.position = new Vector3(pos.x, pos.y + HeightAboveTarget, pos.z - 0.01f);

        var t = (Time.time - indicator.PulseStart) / PulseSeconds;
        var scale = t is >= 0f and < 1f ? Mathf.Lerp(PulseScale, 1f, t) : 1f;
        indicator.Root.transform.localScale = new Vector3(scale, scale, 1f);
    }

    private static void Destroy(Indicator indicator)
    {
        if (indicator.Root != null)
        {
            Object.Destroy(indicator.Root);
        }

        indicator.Root = null;
        indicator.Pips.Clear();
    }

    private static void EnsureSprites()
    {
        _filledSprite ??= CreateCircleSprite(ring: false);
        _ringSprite ??= CreateCircleSprite(ring: true);
    }

    /// <summary>An anti-aliased white disc, or a ring with a faint dark fill so it shows on light backgrounds.</summary>
    private static Sprite CreateCircleSprite(bool ring)
    {
        var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var center = (TextureSize - 1) / 2f;
        var outer = TextureSize / 2f - 1f;
        var inner = outer - TextureSize * 0.14f;
        var pixels = new Color32[TextureSize * TextureSize];
        for (var y = 0; y < TextureSize; y++)
        {
            for (var x = 0; x < TextureSize; x++)
            {
                var d = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                var outside = Mathf.Clamp01(d - outer + 0.5f);
                Color color;
                if (!ring)
                {
                    color = new Color(1f, 1f, 1f, 1f - outside);
                }
                else
                {
                    var edge = Mathf.Clamp01(d - inner + 0.5f);
                    // White ring, dark translucent inside
                    color = Color.Lerp(new Color(0f, 0f, 0f, 0.45f), Color.white, edge);
                    color.a *= 1f - outside;
                }

                pixels[y * TextureSize + x] = color;
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        Object.DontDestroyOnLoad(texture);

        var sprite = Sprite.Create(texture, new Rect(0, 0, TextureSize, TextureSize), new Vector2(0.5f, 0.5f),
            TextureSize / PipSize);
        Object.DontDestroyOnLoad(sprite);
        return sprite;
    }
}
