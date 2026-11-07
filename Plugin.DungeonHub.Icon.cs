using System;
using System.IO;
using System.IO.Compression;

namespace Stellar.DungeonHub;

// The hub header's accent tile shows the Esc-menu gate icon (dungeons-esc-icon.png) in WHITE, like every house icon
// tile (Stellar-Design-Language.md §4 "Icon tile": live accent fill + white flat glyph). The launcher tile and the Esc
// rail button keep the original grey PNG.
//
// The PNG is grey stone (luminance ~144–191) with dark mortar / bar lines (~0–80). Making it white keeps the shape only
// if the lines become GAPS, not white: alpha_out = alpha × ramp(luminance, DhIconLo..DhIconHi), RGB = white. So the
// gate still reads as arch + portcullis instead of a solid blob. Baked once (managed decode → recolour → the Dungeon
// Confirm PNG writer, DcEncodePng in .Art.cs), cached; null on any failure (the tile just shows empty).
public sealed partial class Plugin
{
    private const float DhIconLo = 80f, DhIconHi = 140f;   // luminance (0–255): ≤ Lo = gap, ≥ Hi = solid white

    private static byte[]? _dhGateWhite;
    private static bool _dhGateWhiteTried;

    private static byte[]? DhGateWhitePng()
    {
        if (_dhGateWhiteTried) return _dhGateWhite;
        _dhGateWhiteTried = true;
        try
        {
            var src = CachedPng("dungeons-esc-icon.png");
            if (src == null || !DhDecodeRgba8(src, out int w, out int h, out var px)) return null;
            for (int i = 0; i < px.Length; i += 4)
            {
                float lum = 0.2126f * px[i] + 0.7152f * px[i + 1] + 0.0722f * px[i + 2];
                float t = Math.Clamp((lum - DhIconLo) / (DhIconHi - DhIconLo), 0f, 1f);
                px[i + 3] = (byte)MathF.Round(px[i + 3] * t);
                px[i] = px[i + 1] = px[i + 2] = 255;
            }
            return _dhGateWhite = DcEncodePng(w, h, px);
        }
        catch { return null; }
    }

    // Minimal PNG reader for what we ship: 8-bit RGBA (colour type 6), non-interlaced. Concatenates the IDAT chunks,
    // inflates the zlib stream and undoes the five per-row filters. Anything else → false.
    private static bool DhDecodeRgba8(byte[] png, out int w, out int h, out byte[] rgba)
    {
        w = h = 0; rgba = Array.Empty<byte>();
        if (png.Length < 33 || png[0] != 137 || png[1] != 80) return false;
        using var idat = new MemoryStream();
        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            int d = p + 8;
            if (len < 0 || d + len > png.Length) return false;
            if (type == "IHDR")
            {
                w = (png[d] << 24) | (png[d + 1] << 16) | (png[d + 2] << 8) | png[d + 3];
                h = (png[d + 4] << 24) | (png[d + 5] << 16) | (png[d + 6] << 8) | png[d + 7];
                if (png[d + 8] != 8 || png[d + 9] != 6 || png[d + 12] != 0) return false;   // RGBA8, no interlace
            }
            else if (type == "IDAT") idat.Write(png, d, len);
            else if (type == "IEND") break;
            p = d + len + 4;   // + CRC
        }
        if (w <= 0 || h <= 0) return false;

        int stride = w * 4;
        var raw = new byte[h * (stride + 1)];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0, n;
            while (got < raw.Length && (n = z.Read(raw, got, raw.Length - got)) > 0) got += n;
            if (got < raw.Length) return false;
        }

        rgba = new byte[h * stride];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)], s = y * (stride + 1) + 1, o = y * stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= 4 ? rgba[o + x - 4] : 0;               // left
                int b = y > 0 ? rgba[o - stride + x] : 0;           // up
                int c = x >= 4 && y > 0 ? rgba[o - stride + x - 4] : 0;   // up-left
                int v = raw[s + x];
                v += f switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => DhPaeth(a, b, c),
                    _ => 0,
                };
                rgba[o + x] = (byte)v;
            }
        }
        return true;
    }

    private static int DhPaeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
