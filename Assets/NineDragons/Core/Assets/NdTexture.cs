using System;

namespace NineDragons.Assets
{
    public enum NdTextureKind { Unknown, Dds, Tga, Bmp, Jpg }

    public enum NdTextureFormat
    {
        Rgba32,     // decoded, 4 bytes/pixel, TOP-DOWN row-major, R,G,B,A byte order
        Dxt1,       // raw BC1 blocks as stored in the DDS (top-down)
        Dxt3,       // raw BC2 blocks
        Dxt5        // raw BC3 blocks
    }

    /// <summary>
    /// Texture loader for the client's texture packages (TR_*.XP, SCR_BASE.XP UI art, ...).
    ///
    /// The packages store textures under a <c>.tga</c> name, but MOST entries are actually DDS
    /// files (magic "DDS ") holding DXT1/DXT3/DXT5 — the client feeds the raw bytes to
    /// <c>D3DXCreateTextureFromFileInMemory</c>, which sniffs the real format and ignores the
    /// extension. Measured over the real 2026 data (tgastat/ddsstat over every TR_* package):
    /// DDS-DXT5 ≈ 11.5k, DDS-DXT1 ≈ 5.8k, DDS-DXT3 ≈ 15, real TGA type 2 ≈ 1.0k,
    /// TGA type 10 (RLE) ≈ 210, TGA 16bpp ≈ 2; plus a handful of .jpg/.bmp entries.
    ///
    /// <see cref="Load"/> therefore sniffs first (<see cref="Sniff"/>) and returns:
    ///  - DDS DXT1/DXT5 → raw block data (mip 0) so Unity can upload it compressed as-is
    ///    (TextureFormat.DXT1/DXT5); call <see cref="ToRgba32"/> for a software decode.
    ///  - DDS DXT3 → decoded RGBA32 (Unity has no DXT3 format).
    ///  - TGA (type 2/3/10; 16/24/32 bpp) → decoded RGBA32.
    ///  - BMP/JPG → not parsed here; extract the entry bytes and hand them to the consumer
    ///    (Unity ImageConversion.LoadImage handles JPG; the 14 BMPs are debug art).
    ///
    /// RGBA32 output is TOP-DOWN (first byte row = top image row), matching the DDS/D3D
    /// convention; bottom-up TGAs are flipped during decode. The Unity layer flips or adjusts
    /// UVs once, in one place.
    /// </summary>
    public sealed class NdTexture
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public NdTextureFormat Format { get; private set; }

        /// <summary>Pixel payload; see <see cref="Format"/> for layout.</summary>
        public byte[] Data { get; private set; }

        /// <summary>Mip levels stored after mip 0 in the source DDS (0 = only mip 0). Data holds mip 0 only.</summary>
        public int ExtraMipCount { get; private set; }

        public static NdTextureKind Sniff(byte[] data)
        {
            if (data == null || data.Length < 18) return NdTextureKind.Unknown;
            if (data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ') return NdTextureKind.Dds;
            if (data[0] == 0xFF && data[1] == 0xD8) return NdTextureKind.Jpg;
            if (data[0] == 'B' && data[1] == 'M') return NdTextureKind.Bmp;
            // TGA has no magic; accept the image types the data actually uses
            byte type = data[2];
            if ((type == 1 || type == 2 || type == 3 || type == 9 || type == 10) &&
                (data[1] == 0 || data[1] == 1))
                return NdTextureKind.Tga;
            return NdTextureKind.Unknown;
        }

        /// <summary>Parse a texture entry (see class remarks). Throws <see cref="InvalidDataException"/> for BMP/JPG/unknown.</summary>
        public static NdTexture Load(byte[] data, string name = null)
        {
            switch (Sniff(data))
            {
                case NdTextureKind.Dds: return LoadDds(data, name ?? "DDS");
                case NdTextureKind.Tga: return LoadTga(data, name ?? "TGA");
                case NdTextureKind.Jpg: throw new InvalidDataException($"{name}: JPG — pass the raw bytes to the image decoder of the host (Unity LoadImage)");
                case NdTextureKind.Bmp: throw new InvalidDataException($"{name}: BMP — pass the raw bytes to the image decoder of the host");
                default: throw new InvalidDataException($"{name}: unrecognized texture format");
            }
        }

        /// <summary>Decoded RGBA32 (top-down). Decompresses DXT in software when needed.</summary>
        public byte[] ToRgba32()
        {
            switch (Format)
            {
                case NdTextureFormat.Rgba32: return Data;
                case NdTextureFormat.Dxt1: return DecodeDxt(Data, Width, Height, 1);
                case NdTextureFormat.Dxt3: return DecodeDxt(Data, Width, Height, 3);
                case NdTextureFormat.Dxt5: return DecodeDxt(Data, Width, Height, 5);
                default: throw new InvalidOperationException("unreachable");
            }
        }

        // ---------------------------------------------------------------- DDS

        private static NdTexture LoadDds(byte[] d, string name)
        {
            if (d.Length < 128) throw new InvalidDataException($"{name}: DDS shorter than header");
            int height = BitConverter.ToInt32(d, 12);
            int width = BitConverter.ToInt32(d, 16);
            int mips = BitConverter.ToInt32(d, 28);          // 0 on all sampled files
            uint pfFlags = BitConverter.ToUInt32(d, 80);
            if ((pfFlags & 0x4) == 0)                        // DDPF_FOURCC
                throw new InvalidDataException($"{name}: uncompressed DDS not used by the client data");
            string fourcc = System.Text.Encoding.ASCII.GetString(d, 84, 4);

            int blockSize;
            NdTextureFormat fmt;
            switch (fourcc)
            {
                case "DXT1": blockSize = 8; fmt = NdTextureFormat.Dxt1; break;
                case "DXT3": blockSize = 16; fmt = NdTextureFormat.Dxt3; break;
                case "DXT5": blockSize = 16; fmt = NdTextureFormat.Dxt5; break;
                default: throw new InvalidDataException($"{name}: unsupported DDS fourCC '{fourcc}'");
            }

            int mip0 = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockSize;
            if (128 + mip0 > d.Length)
                throw new InvalidDataException($"{name}: DDS truncated (need {mip0} block bytes, have {d.Length - 128})");

            var blocks = new byte[mip0];
            Buffer.BlockCopy(d, 128, blocks, 0, mip0);

            var t = new NdTexture
            {
                Width = width,
                Height = height,
                Format = fmt,
                Data = blocks,
                ExtraMipCount = Math.Max(0, mips - 1)
            };
            // DXT3 has no Unity equivalent — hand it over decoded
            if (fmt == NdTextureFormat.Dxt3)
            {
                t.Data = t.ToRgba32();
                t.Format = NdTextureFormat.Rgba32;
            }
            return t;
        }

        // BC1/BC2/BC3 software decode (top-down output).
        private static byte[] DecodeDxt(byte[] blocks, int width, int height, int dxt)
        {
            int blockSize = dxt == 1 ? 8 : 16;
            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
            var outPx = new byte[width * height * 4];
            var c = new byte[4][] { new byte[4], new byte[4], new byte[4], new byte[4] };
            var alpha = new byte[16];

            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int src = (by * bw + bx) * blockSize;
                    int colorOfs = dxt == 1 ? src : src + 8;

                    // ---- alpha block
                    if (dxt == 3)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            int nib = (blocks[src + i / 2] >> ((i & 1) * 4)) & 0xF;
                            alpha[i] = (byte)(nib * 17);
                        }
                    }
                    else if (dxt == 5)
                    {
                        byte a0 = blocks[src], a1 = blocks[src + 1];
                        var pal = new byte[8];
                        pal[0] = a0; pal[1] = a1;
                        if (a0 > a1)
                            for (int i = 1; i < 7; i++) pal[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                        else
                        {
                            for (int i = 1; i < 5; i++) pal[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                            pal[6] = 0; pal[7] = 255;
                        }
                        ulong bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (ulong)blocks[src + 2 + i] << (8 * i);
                        for (int i = 0; i < 16; i++) alpha[i] = pal[(int)((bits >> (3 * i)) & 0x7)];
                    }

                    // ---- color block
                    ushort c0 = (ushort)(blocks[colorOfs] | blocks[colorOfs + 1] << 8);
                    ushort c1 = (ushort)(blocks[colorOfs + 2] | blocks[colorOfs + 3] << 8);
                    Rgb565(c0, c[0]); Rgb565(c1, c[1]);
                    bool opaqueMode = dxt != 1 || c0 > c1;
                    if (opaqueMode)
                    {
                        for (int i = 0; i < 3; i++) c[2][i] = (byte)((2 * c[0][i] + c[1][i]) / 3);
                        for (int i = 0; i < 3; i++) c[3][i] = (byte)((c[0][i] + 2 * c[1][i]) / 3);
                        c[2][3] = 255; c[3][3] = 255;
                    }
                    else
                    {
                        for (int i = 0; i < 3; i++) c[2][i] = (byte)((c[0][i] + c[1][i]) / 2);
                        c[2][3] = 255;
                        c[3][0] = c[3][1] = c[3][2] = c[3][3] = 0;    // DXT1 1-bit transparent
                    }

                    uint idx = BitConverter.ToUInt32(blocks, colorOfs + 4);
                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;
                            var col = c[(int)((idx >> (2 * (py * 4 + px))) & 0x3)];
                            int o = (y * width + x) * 4;
                            outPx[o] = col[0]; outPx[o + 1] = col[1]; outPx[o + 2] = col[2];
                            outPx[o + 3] = dxt == 1 ? col[3] : alpha[py * 4 + px];
                        }
                    }
                }
            return outPx;
        }

        private static void Rgb565(ushort v, byte[] rgba)
        {
            int r = (v >> 11) & 0x1F, g = (v >> 5) & 0x3F, b = v & 0x1F;
            rgba[0] = (byte)(r << 3 | r >> 2);
            rgba[1] = (byte)(g << 2 | g >> 4);
            rgba[2] = (byte)(b << 3 | b >> 2);
            rgba[3] = 255;
        }

        // ---------------------------------------------------------------- TGA

        private static NdTexture LoadTga(byte[] d, string name)
        {
            int idLen = d[0];
            int cmapType = d[1];
            int type = d[2];
            int cmapLen = d[5] | d[6] << 8;
            int cmapBpp = d[7];
            int width = d[12] | d[13] << 8;
            int height = d[14] | d[15] << 8;
            int bpp = d[16];
            int desc = d[17];
            bool topDown = (desc & 0x20) != 0;

            if (type != 2 && type != 3 && type != 10)
                throw new InvalidDataException($"{name}: TGA type {type} not used by the client data");
            if (bpp != 16 && bpp != 24 && bpp != 32 && !(type == 3 && bpp == 8))
                throw new InvalidDataException($"{name}: TGA bpp {bpp} unsupported");
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                throw new InvalidDataException($"{name}: implausible TGA size {width}x{height}");

            int p = 18 + idLen + (cmapType != 0 ? cmapLen * ((cmapBpp + 7) / 8) : 0);
            int bytesPp = bpp / 8;
            var px = new byte[width * height * 4];    // top-down RGBA

            void PutPixel(int i, byte[] bgra)
            {
                int x = i % width, row = i / width;
                int y = topDown ? row : height - 1 - row;
                int o = (y * width + x) * 4;
                px[o] = bgra[2]; px[o + 1] = bgra[1]; px[o + 2] = bgra[0]; px[o + 3] = bgra[3];
            }

            var b = new byte[4];
            void ReadPixel()
            {
                if (bpp == 32) { b[0] = d[p]; b[1] = d[p + 1]; b[2] = d[p + 2]; b[3] = d[p + 3]; }
                else if (bpp == 24) { b[0] = d[p]; b[1] = d[p + 1]; b[2] = d[p + 2]; b[3] = 255; }
                else if (bpp == 16)
                {
                    int v = d[p] | d[p + 1] << 8;      // A1R5G5B5
                    int r = (v >> 10) & 0x1F, g = (v >> 5) & 0x1F, bl = v & 0x1F;
                    b[2] = (byte)(r << 3 | r >> 2); b[1] = (byte)(g << 3 | g >> 2); b[0] = (byte)(bl << 3 | bl >> 2);
                    b[3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                }
                else { b[0] = b[1] = b[2] = d[p]; b[3] = 255; }   // type 3 grayscale
                p += bytesPp;
            }

            int total = width * height;
            try
            {
                if (type == 10)      // RLE
                {
                    int i = 0;
                    while (i < total)
                    {
                        byte hdr = d[p++];
                        int count = (hdr & 0x7F) + 1;
                        if ((hdr & 0x80) != 0)
                        {
                            ReadPixel();
                            for (int k = 0; k < count; k++) PutPixel(i++, b);
                        }
                        else
                            for (int k = 0; k < count; k++) { ReadPixel(); PutPixel(i++, b); }
                    }
                }
                else
                    for (int i = 0; i < total; i++) { ReadPixel(); PutPixel(i, b); }
            }
            catch (IndexOutOfRangeException)
            {
                throw new InvalidDataException($"{name}: TGA truncated at byte {p}");
            }

            return new NdTexture { Width = width, Height = height, Format = NdTextureFormat.Rgba32, Data = px };
        }
    }
}
