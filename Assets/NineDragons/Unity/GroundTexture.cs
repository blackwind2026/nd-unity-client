using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Composes the .xtv per-tile splat cache (a pre-rendered 32×32 DXT1 DDS per terrain tile,
    /// XtvTerrain.SplatImages) into one ground texture for the whole map — the client's actual
    /// terrain look (paths, grass, dirt, rock already blended), same as the blender plugin's
    /// build_ground_image. The terrain is then UV-mapped planar to this texture.
    /// </summary>
    public static class GroundTexture
    {
        private const int Ts = 32;   // _XDEF_SPLATIMAGESIZE

        public static Texture2D Build(XtvTerrain t)
        {
            if (t.SplatImages == null || t.SplatImages.Length == 0 || t.SplatImageSize < 128)
                return null;
            int tc = t.TileCount;
            int size = tc * Ts;
            var pixels = new Color32[size * size];

            for (int n = 0; n < t.SplatImages.Length && n < tc * tc; n++)
            {
                var blob = t.SplatImages[n];
                if (blob == null || blob.Length < 128 + 512) continue;
                if (!(blob[0] == 'D' && blob[1] == 'D' && blob[2] == 'S')) continue;
                NdTexture tex;
                try { tex = NdTexture.Load(blob, "splat"); } catch { continue; }
                var rgba = tex.ToRgba32();               // top-down, 32×32, 4 bytes/pixel
                int row = n / tc, col = n % tc;
                int baseX = col * Ts, baseY = row * Ts;
                for (int r = 0; r < Ts; r++)
                {
                    // DDS row r is top-down; map to Unity bottom-up so tile row 0 = low world z
                    int destY = baseY + r;
                    int srcRow = (Ts - 1 - r) * Ts * 4;
                    int dst = destY * size + baseX;
                    for (int x = 0; x < Ts; x++)
                    {
                        int s = srcRow + x * 4;
                        pixels[dst + x] = new Color32(rgba[s], rgba[s + 1], rgba[s + 2], 255);
                    }
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "ground" };
            texture.SetPixels32(pixels);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.Apply(true);
            return texture;
        }
    }
}
