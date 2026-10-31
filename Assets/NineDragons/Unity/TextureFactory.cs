using System;
using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// NdTexture (M6 core decoder) → UnityEngine.Texture2D.
    ///
    /// Everything is uploaded RAW and TOP-DOWN: Unity's row 0 is the bottom, so the image
    /// lands vertically flipped in texture space — which exactly matches sampling with the
    /// unmodified D3D UVs stored in the meshes (see UnityConvert remarks). DXT1/DXT5 blocks
    /// go up unmodified (no decode cost); DXT3 and TGA arrive from the core already as RGBA32.
    /// </summary>
    public static class TextureFactory
    {
        public static Texture2D Create(NdTexture t, string name)
        {
            Texture2D tex;
            switch (t.Format)
            {
                case NdTextureFormat.Dxt1:
                    tex = new Texture2D(t.Width, t.Height, TextureFormat.DXT1, false);
                    tex.LoadRawTextureData(t.Data);
                    break;
                case NdTextureFormat.Dxt5:
                    tex = new Texture2D(t.Width, t.Height, TextureFormat.DXT5, false);
                    tex.LoadRawTextureData(t.Data);
                    break;
                case NdTextureFormat.Rgba32:
                    tex = new Texture2D(t.Width, t.Height, TextureFormat.RGBA32, false);
                    tex.LoadRawTextureData(t.Data);
                    break;
                default:
                    throw new InvalidOperationException($"unhandled format {t.Format}");
            }
            tex.name = name;
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.Apply(false, true);   // no mips in the source data; free the CPU copy
            return tex;
        }

        /// <summary>Load raw entry bytes (sniffs DDS/TGA; JPG via Unity's decoder). Null when undecodable.</summary>
        public static Texture2D FromBytes(byte[] raw, string name)
        {
            switch (NdTexture.Sniff(raw))
            {
                case NdTextureKind.Dds:
                case NdTextureKind.Tga:
                    return Create(NdTexture.Load(raw, name), name);
                case NdTextureKind.Jpg:
                {
                    // Unity decodes JPG bottom-up (standard Unity orientation). JPGs are
                    // loading screens, not mesh textures, so the orientation mismatch with
                    // our raw-UV convention does not matter here.
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    if (!tex.LoadImage(raw)) { UnityEngine.Object.Destroy(tex); return null; }
                    tex.name = name;
                    return tex;
                }
                default:
                    return null;
            }
        }
    }
}
