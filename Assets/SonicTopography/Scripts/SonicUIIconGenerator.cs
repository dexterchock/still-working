using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SonicTopography
{
    public enum SonicIconType
    {
        MusicNote,
        Folder,
        SettingsSliders,
        Play,
        Pause,
        Next,
        Previous,
        Shuffle,
        Repeat,
        RepeatOne,
        Search,
        VolumeHigh,
        VolumeMuted,
        UpFolder,
        Check,
        Refresh,
        ChevronLeft,
        ChevronRight,
        CloseX,
        Globe,
        Keyboard,
        ReturnArrow
    }

    public enum SonicSurfaceMode
    {
        FrostedBlurWindow = 0,
        SdfShape = 1,
        RoundedTexture = 2,
        OuterGlow = 3
    }

    /// <summary>
    /// Feeds exact RectTransform dimensions and analytical SDF parameters into UV1/UV2/UV3
    /// so SonicVisionGlassUI.shader renders razor-sharp, infinitely smooth Apple squircles,
    /// capsules, circles, and rounded album covers on simple 2-triangle quads without 9-slice distortion.
    /// </summary>
    [RequireComponent(typeof(Graphic))]
    public class SonicGlassSurface : BaseMeshEffect
    {
        public SonicSurfaceMode mode = SonicSurfaceMode.SdfShape;
        public float cornerRadius = 20f;
        public bool isPill = false;
        [Range(0f, 0.99f)] public float borderIntensity = 0.26f;
        public bool autoSquareCropUV = false;
        public float glowMargin = 16f;

        public void Configure(
            SonicSurfaceMode surfaceMode,
            float radius,
            bool pill,
            float border,
            bool squareCrop = false,
            float margin = 16f)
        {
            mode = surfaceMode;
            cornerRadius = radius;
            isPill = pill;
            borderIntensity = Mathf.Clamp(border, 0f, 0.99f);
            autoSquareCropUV = squareCrop;
            glowMargin = margin;
            SetVerticesDirty();
        }

        public void SetBorder(float border)
        {
            borderIntensity = Mathf.Clamp(border, 0f, 0.99f);
            SetVerticesDirty();
        }

        public void SetVerticesDirty()
        {
            if (graphic != null)
            {
                graphic.SetVerticesDirty();
            }
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            SetVerticesDirty();
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || graphic == null) return;

            int count = vh.currentVertCount;
            if (count == 0) return;

            Rect r = graphic.rectTransform.rect;
            float w = Mathf.Max(1f, r.width);
            float h = Mathf.Max(1f, r.height);
            float halfMin = Mathf.Min(w, h) * 0.5f;
            float rad = isPill ? halfMin : Mathf.Clamp(cornerRadius, 0f, halfMin);
            float packed = ((int)mode) * 10f + Mathf.Clamp(borderIntensity, 0f, 0.99f);

            // Optional 1:1 center-crop UV adjustment for non-square album cover textures
            float uScale = 1f, vScale = 1f, uOffset = 0f, vOffset = 0f;
            if (autoSquareCropUV && graphic is Image img && img.sprite != null && img.sprite.texture != null)
            {
                Texture2D tex = img.sprite.texture;
                if (tex.width > 0 && tex.height > 0 && tex.width != tex.height)
                {
                    float aspect = tex.width / (float)tex.height;
                    if (aspect > 1f)
                    {
                        uScale = 1f / aspect;
                        uOffset = (1f - uScale) * 0.5f;
                    }
                    else
                    {
                        vScale = aspect;
                        vOffset = (1f - vScale) * 0.5f;
                    }
                }
            }

            UIVertex v = default;
            for (int i = 0; i < count; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                float normX = Mathf.Clamp01((v.position.x - r.xMin) / w);
                float normY = Mathf.Clamp01((v.position.y - r.yMin) / h);

                if (autoSquareCropUV)
                {
                    v.uv0 = new Vector4(uOffset + normX * uScale, vOffset + normY * vScale, 0f, 0f);
                }

                v.uv1 = new Vector4(normX, normY, 0f, 0f);
                v.uv2 = new Vector4(w, h, 0f, 0f);
                v.uv3 = new Vector4(rad, packed, glowMargin, 0f);
                vh.SetUIVertex(v, i);
            }
        }
    }

    public static class SonicUIIconGenerator
    {
        private static Sprite _whiteQuadSprite;
        private static readonly Dictionary<SonicIconType, Sprite> _iconCache = new Dictionary<SonicIconType, Sprite>();

        public static Sprite GetWhiteSprite()
        {
            if (_whiteQuadSprite != null) return _whiteQuadSprite;

            Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Color[] cols = new Color[16];
            for (int i = 0; i < 16; i++) cols[i] = Color.white;
            tex.SetPixels(cols);
            tex.Apply();

            _whiteQuadSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
            return _whiteQuadSprite;
        }

        public static Sprite GetIcon(SonicIconType type)
        {
            if (_iconCache.TryGetValue(type, out Sprite cached) && cached != null)
            {
                return cached;
            }

            const int size = 96;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2((x + 0.5f) / size * 2f - 1f, (y + 0.5f) / size * 2f - 1f);
                    float sd = SampleIconSdf(type, p);
                    float alpha = 1f - SmoothStep(-0.024f, 0.024f, sd);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            Sprite spr = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _iconCache[type] = spr;
            return spr;
        }

        private static float SampleIconSdf(SonicIconType type, Vector2 p)
        {
            switch (type)
            {
                case SonicIconType.Play:
                {
                    Vector2 q = p - new Vector2(0.06f, 0f);
                    return SdTriangle(q, new Vector2(-0.38f, -0.46f), new Vector2(-0.38f, 0.46f), new Vector2(0.46f, 0.0f)) - 0.065f;
                }

                case SonicIconType.Pause:
                {
                    float leftBar = SdRoundedBox(p - new Vector2(-0.22f, 0f), new Vector2(0.105f, 0.44f), 0.06f);
                    float rightBar = SdRoundedBox(p - new Vector2(0.22f, 0f), new Vector2(0.105f, 0.44f), 0.06f);
                    return Mathf.Min(leftBar, rightBar);
                }

                case SonicIconType.Next:
                {
                    // Next track icon (>>|) with double right triangle and vertical end bar
                    float t1 = SdTriangle(p, new Vector2(-0.48f, -0.34f), new Vector2(-0.48f, 0.34f), new Vector2(-0.04f, 0f)) - 0.04f;
                    float t2 = SdTriangle(p, new Vector2(-0.04f, -0.34f), new Vector2(-0.04f, 0.34f), new Vector2(0.40f, 0f)) - 0.04f;
                    float bar = SdRoundedBox(p - new Vector2(0.48f, 0f), new Vector2(0.055f, 0.36f), 0.035f);
                    return Mathf.Min(bar, Mathf.Min(t1, t2));
                }

                case SonicIconType.Previous:
                {
                    // Previous track icon (|<<) with vertical start bar and double left triangle
                    float bar = SdRoundedBox(p - new Vector2(-0.48f, 0f), new Vector2(0.055f, 0.36f), 0.035f);
                    float t1 = SdTriangle(p, new Vector2(0.04f, -0.34f), new Vector2(0.04f, 0.34f), new Vector2(-0.40f, 0f)) - 0.04f;
                    float t2 = SdTriangle(p, new Vector2(0.48f, -0.34f), new Vector2(0.48f, 0.34f), new Vector2(0.04f, 0f)) - 0.04f;
                    return Mathf.Min(bar, Mathf.Min(t1, t2));
                }

                case SonicIconType.MusicNote:
                {
                    float head1 = Vector2.Distance(p, new Vector2(-0.30f, -0.34f)) - 0.18f;
                    float head2 = Vector2.Distance(p, new Vector2(0.24f, -0.24f)) - 0.18f;
                    float stem1 = SdSegment(p, new Vector2(-0.15f, -0.32f), new Vector2(-0.15f, 0.38f)) - 0.06f;
                    float stem2 = SdSegment(p, new Vector2(0.39f, -0.22f), new Vector2(0.39f, 0.48f)) - 0.06f;
                    float beam = SdSegment(p, new Vector2(-0.15f, 0.35f), new Vector2(0.39f, 0.45f)) - 0.09f;
                    return Mathf.Min(Mathf.Min(head1, head2), Mathf.Min(Mathf.Min(stem1, stem2), beam));
                }

                case SonicIconType.Folder:
                {
                    float body = SdRoundedBox(p - new Vector2(0f, -0.06f), new Vector2(0.60f, 0.40f), 0.11f);
                    float tab = SdRoundedBox(p - new Vector2(-0.25f, 0.32f), new Vector2(0.26f, 0.14f), 0.07f);
                    return Mathf.Min(body, tab);
                }

                case SonicIconType.SettingsSliders:
                {
                    float line1 = SdSegment(p, new Vector2(-0.52f, 0.34f), new Vector2(0.52f, 0.34f)) - 0.055f;
                    float knob1 = Vector2.Distance(p, new Vector2(-0.16f, 0.34f)) - 0.145f;

                    float line2 = SdSegment(p, new Vector2(-0.52f, 0.0f), new Vector2(0.52f, 0.0f)) - 0.055f;
                    float knob2 = Vector2.Distance(p, new Vector2(0.20f, 0.0f)) - 0.145f;

                    float line3 = SdSegment(p, new Vector2(-0.52f, -0.34f), new Vector2(0.52f, -0.34f)) - 0.055f;
                    float knob3 = Vector2.Distance(p, new Vector2(-0.10f, -0.34f)) - 0.145f;

                    return Mathf.Min(Mathf.Min(line1, knob1), Mathf.Min(Mathf.Min(line2, knob2), Mathf.Min(line3, knob3)));
                }

                case SonicIconType.Search:
                {
                    float ring = Mathf.Abs(Vector2.Distance(p, new Vector2(-0.08f, 0.08f)) - 0.36f) - 0.07f;
                    float handle = SdSegment(p, new Vector2(0.18f, -0.18f), new Vector2(0.52f, -0.52f)) - 0.075f;
                    return Mathf.Min(ring, handle);
                }

                case SonicIconType.Shuffle:
                {
                    float diag1 = SdSegment(p, new Vector2(-0.48f, -0.28f), new Vector2(0.38f, 0.28f)) - 0.055f;
                    float diag2 = SdSegment(p, new Vector2(-0.48f, 0.28f), new Vector2(0.38f, -0.28f)) - 0.055f;
                    float head1 = SdTriangle(p, new Vector2(0.26f, 0.10f), new Vector2(0.26f, 0.46f), new Vector2(0.56f, 0.28f)) - 0.02f;
                    float head2 = SdTriangle(p, new Vector2(0.26f, -0.46f), new Vector2(0.26f, -0.10f), new Vector2(0.56f, -0.28f)) - 0.02f;
                    return Mathf.Min(Mathf.Min(diag1, diag2), Mathf.Min(head1, head2));
                }

                case SonicIconType.Repeat:
                case SonicIconType.RepeatOne:
                {
                    float boxDist = Mathf.Abs(SdRoundedBox(p, new Vector2(0.50f, 0.30f), 0.24f)) - 0.06f;
                    float topArrow = SdTriangle(p, new Vector2(0.16f, 0.14f), new Vector2(0.16f, 0.46f), new Vector2(0.44f, 0.30f)) - 0.02f;
                    float botArrow = SdTriangle(p, new Vector2(-0.16f, -0.46f), new Vector2(-0.16f, -0.14f), new Vector2(-0.44f, -0.30f)) - 0.02f;
                    float baseIcon = Mathf.Min(boxDist, Mathf.Min(topArrow, botArrow));

                    if (type == SonicIconType.RepeatOne)
                    {
                        float oneStem = SdSegment(p, new Vector2(0.02f, -0.13f), new Vector2(0.02f, 0.14f)) - 0.05f;
                        float oneFlag = SdSegment(p, new Vector2(-0.07f, 0.07f), new Vector2(0.02f, 0.14f)) - 0.045f;
                        baseIcon = Mathf.Min(baseIcon, Mathf.Min(oneStem, oneFlag));
                    }
                    return baseIcon;
                }

                case SonicIconType.VolumeHigh:
                {
                    float cone = SdTriangle(p, new Vector2(-0.50f, 0f), new Vector2(-0.08f, -0.40f), new Vector2(-0.08f, 0.40f)) - 0.04f;
                    float box = SdRoundedBox(p - new Vector2(-0.36f, 0f), new Vector2(0.15f, 0.17f), 0.04f);
                    float w1 = Mathf.Abs(Vector2.Distance(p, new Vector2(-0.06f, 0f)) - 0.26f) - 0.05f;
                    float w2 = Mathf.Abs(Vector2.Distance(p, new Vector2(-0.06f, 0f)) - 0.46f) - 0.05f;
                    if (p.x < 0.02f)
                    {
                        w1 = 1f;
                        w2 = 1f;
                    }
                    return Mathf.Min(Mathf.Min(cone, box), Mathf.Min(w1, w2));
                }

                case SonicIconType.VolumeMuted:
                {
                    float cone = SdTriangle(p, new Vector2(-0.50f, 0f), new Vector2(-0.08f, -0.40f), new Vector2(-0.08f, 0.40f)) - 0.04f;
                    float box = SdRoundedBox(p - new Vector2(-0.36f, 0f), new Vector2(0.15f, 0.17f), 0.04f);
                    float x1 = SdSegment(p, new Vector2(0.10f, -0.20f), new Vector2(0.46f, 0.20f)) - 0.055f;
                    float x2 = SdSegment(p, new Vector2(0.10f, 0.20f), new Vector2(0.46f, -0.20f)) - 0.055f;
                    return Mathf.Min(Mathf.Min(cone, box), Mathf.Min(x1, x2));
                }

                case SonicIconType.ChevronLeft:
                case SonicIconType.UpFolder:
                {
                    float topArm = SdSegment(p, new Vector2(-0.18f, 0.0f), new Vector2(0.20f, 0.38f)) - 0.075f;
                    float botArm = SdSegment(p, new Vector2(-0.18f, 0.0f), new Vector2(0.20f, -0.38f)) - 0.075f;
                    return Mathf.Min(topArm, botArm);
                }

                case SonicIconType.ChevronRight:
                {
                    float topArm = SdSegment(p, new Vector2(0.18f, 0.0f), new Vector2(-0.20f, 0.38f)) - 0.075f;
                    float botArm = SdSegment(p, new Vector2(0.18f, 0.0f), new Vector2(-0.20f, -0.38f)) - 0.075f;
                    return Mathf.Min(topArm, botArm);
                }

                case SonicIconType.CloseX:
                {
                    float d1 = SdSegment(p, new Vector2(-0.32f, -0.32f), new Vector2(0.32f, 0.32f)) - 0.075f;
                    float d2 = SdSegment(p, new Vector2(-0.32f, 0.32f), new Vector2(0.32f, -0.32f)) - 0.075f;
                    return Mathf.Min(d1, d2);
                }

                case SonicIconType.Check:
                {
                    float s1 = SdSegment(p, new Vector2(-0.40f, -0.02f), new Vector2(-0.10f, -0.32f)) - 0.08f;
                    float s2 = SdSegment(p, new Vector2(-0.10f, -0.32f), new Vector2(0.44f, 0.30f)) - 0.08f;
                    return Mathf.Min(s1, s2);
                }

                case SonicIconType.Globe:
                {
                    float r = p.magnitude;
                    float ring = Mathf.Abs(r - 0.48f) - 0.052f;
                    float meridian = Mathf.Max(r - 0.49f, Mathf.Abs(new Vector2(p.x * 1.95f, p.y).magnitude - 0.48f) * 0.52f - 0.038f);
                    float equator = SdSegment(p, new Vector2(-0.46f, 0f), new Vector2(0.46f, 0f)) - 0.038f;
                    float latTop = SdSegment(p, new Vector2(-0.39f, 0.23f), new Vector2(0.39f, 0.23f)) - 0.034f;
                    float latBot = SdSegment(p, new Vector2(-0.39f, -0.23f), new Vector2(0.39f, -0.23f)) - 0.034f;
                    return Mathf.Min(ring, Mathf.Min(meridian, Mathf.Min(equator, Mathf.Min(latTop, latBot))));
                }

                case SonicIconType.Keyboard:
                {
                    float outer = Mathf.Abs(SdRoundedBox(p, new Vector2(0.56f, 0.36f), 0.10f)) - 0.050f;
                    float keys = 10f;
                    float[] xs = { -0.33f, -0.11f, 0.11f, 0.33f };
                    for (int i = 0; i < 4; i++)
                    {
                        float k1 = SdRoundedBox(p - new Vector2(xs[i], 0.15f), new Vector2(0.062f, 0.055f), 0.025f);
                        float k2 = SdRoundedBox(p - new Vector2(xs[i], -0.01f), new Vector2(0.062f, 0.055f), 0.025f);
                        keys = Mathf.Min(keys, Mathf.Min(k1, k2));
                    }
                    float space = SdRoundedBox(p - new Vector2(0f, -0.18f), new Vector2(0.28f, 0.052f), 0.03f);
                    return Mathf.Min(outer, Mathf.Min(keys, space));
                }

                case SonicIconType.ReturnArrow:
                {
                    float stem = SdSegment(p, new Vector2(0.36f, 0.32f), new Vector2(0.36f, -0.10f)) - 0.068f;
                    float shaft = SdSegment(p, new Vector2(0.36f, -0.10f), new Vector2(-0.32f, -0.10f)) - 0.068f;
                    float headUp = SdSegment(p, new Vector2(-0.34f, -0.10f), new Vector2(-0.10f, 0.14f)) - 0.068f;
                    float headDn = SdSegment(p, new Vector2(-0.34f, -0.10f), new Vector2(-0.10f, -0.34f)) - 0.068f;
                    return Mathf.Min(Mathf.Min(stem, shaft), Mathf.Min(headUp, headDn));
                }

                default: // Refresh
                {
                    float ring = Mathf.Abs(Vector2.Distance(p, Vector2.zero) - 0.42f) - 0.065f;
                    if (p.x > 0.15f && p.y < 0.05f && p.y > -0.35f) ring = 1f;
                    float head = SdTriangle(p, new Vector2(0.20f, 0.05f), new Vector2(0.55f, 0.05f), new Vector2(0.38f, 0.35f)) - 0.02f;
                    return Mathf.Min(ring, head);
                }
            }
        }

        private static float SdRoundedBox(Vector2 p, Vector2 b, float r)
        {
            Vector2 q = new Vector2(Mathf.Abs(p.x) - b.x + r, Mathf.Abs(p.y) - b.y + r);
            return Mathf.Min(Mathf.Max(q.x, q.y), 0f) + Vector2.Distance(Vector2.Max(q, Vector2.zero), Vector2.zero) - r;
        }

        private static float SdSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a;
            Vector2 ba = b - a;
            float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(0.0001f, Vector2.Dot(ba, ba)));
            return (pa - ba * h).magnitude;
        }

        private static float SdTriangle(Vector2 p, Vector2 p0, Vector2 p1, Vector2 p2)
        {
            Vector2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
            Vector2 v0 = p - p0, v1 = p - p1, v2 = p - p2;
            Vector2 pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0));
            Vector2 pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1));
            Vector2 pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2));
            float s = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
            Vector2 d = Vector2.Min(
                Vector2.Min(
                    new Vector2(Vector2.Dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                    new Vector2(Vector2.Dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))
                ),
                new Vector2(Vector2.Dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x))
            );
            return -Mathf.Sqrt(d.x) * Mathf.Sign(d.y);
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
