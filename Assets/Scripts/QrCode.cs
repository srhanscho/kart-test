using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Minimal QR code encoder: byte mode, error correction level M, versions 1-6
/// (up to 106 bytes), automatic mask selection. Enough for a LAN URL.
/// </summary>
public static class QrCode
{
    // Level M block structure per version: total codewords, EC codewords per block, block count.
    static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172 };
    static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16 };
    static readonly int[] BlockCount = { 0, 1, 1, 1, 2, 2, 4 };
    static readonly int[][] AlignmentPositions =
    {
        null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 }
    };
    const int MaxVersion = 6;
    const int EclFormatBits = 0; // M

    /// <summary>Returns modules[y, x] (true = dark), or null if the text is too long.</summary>
    public static bool[,] Encode(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        while (version <= MaxVersion && DataCapacityBits(version) < 4 + 8 + data.Length * 8) version++;
        if (version > MaxVersion) return null;

        // Bit stream: mode, length, data, terminator, padding.
        var bits = new List<bool>();
        AppendBits(bits, 0x4, 4);
        AppendBits(bits, data.Length, 8);
        foreach (byte b in data) AppendBits(bits, b, 8);
        int capacity = DataCapacityBits(version);
        AppendBits(bits, 0, Math.Min(4, capacity - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) AppendBits(bits, pad, 8);

        var dataCodewords = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) dataCodewords[i >> 3] |= (byte)(1 << (7 - (i & 7)));

        byte[] allCodewords = AddEccAndInterleave(version, dataCodewords);

        int size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];
        DrawFunctionPatterns(version, size, modules, isFunction);
        DrawCodewords(size, allCodewords, modules, isFunction);

        int bestMask = 0;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            ApplyMask(size, mask, modules, isFunction);
            DrawFormatBits(size, mask, modules, isFunction);
            int penalty = Penalty(size, modules);
            if (penalty < bestPenalty) { bestPenalty = penalty; bestMask = mask; }
            ApplyMask(size, mask, modules, isFunction); // undo (XOR)
        }
        ApplyMask(size, bestMask, modules, isFunction);
        DrawFormatBits(size, bestMask, modules, isFunction);
        return modules;
    }

    /// <summary>Renders modules to a point-filtered texture with a quiet zone.</summary>
    public static Texture2D ToTexture(bool[,] modules, int pixelsPerModule = 8, int border = 4)
    {
        int size = modules.GetLength(0);
        int px = (size + border * 2) * pixelsPerModule;
        var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[px * px];
        var dark = new Color32(0, 0, 0, 255);
        var light = new Color32(255, 255, 255, 255);
        for (int py = 0; py < px; py++)
        {
            int my = size - 1 - (py / pixelsPerModule - border); // texture rows go bottom-up
            for (int pxx = 0; pxx < px; pxx++)
            {
                int mx = pxx / pixelsPerModule - border;
                bool isDark = mx >= 0 && my >= 0 && mx < size && my < size && modules[my, mx];
                pixels[py * px + pxx] = isDark ? dark : light;
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false);
        return tex;
    }

    static int DataCapacityBits(int version)
    {
        int ecc = EccPerBlock[version] * BlockCount[version];
        return (TotalCodewords[version] - ecc) * 8;
    }

    static void AppendBits(List<bool> bits, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    static byte[] AddEccAndInterleave(int version, byte[] data)
    {
        int blocks = BlockCount[version];
        int eccLen = EccPerBlock[version];
        int blockDataLen = data.Length / blocks; // all level-M blocks are equal for v1-6
        byte[] divisor = ReedSolomonDivisor(eccLen);

        var dataBlocks = new byte[blocks][];
        var eccBlocks = new byte[blocks][];
        for (int b = 0; b < blocks; b++)
        {
            dataBlocks[b] = new byte[blockDataLen];
            Array.Copy(data, b * blockDataLen, dataBlocks[b], 0, blockDataLen);
            eccBlocks[b] = ReedSolomonRemainder(dataBlocks[b], divisor);
        }

        var result = new List<byte>(TotalCodewords[version]);
        for (int i = 0; i < blockDataLen; i++)
            for (int b = 0; b < blocks; b++) result.Add(dataBlocks[b][i]);
        for (int i = 0; i < eccLen; i++)
            for (int b = 0; b < blocks; b++) result.Add(eccBlocks[b][i]);
        return result.ToArray();
    }

    static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)GfMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = GfMultiply(root, 0x02);
        }
        return result;
    }

    static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[result.Length - 1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)GfMultiply(divisor[i], factor);
        }
        return result;
    }

    static int GfMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    static void SetFunction(bool[,] m, bool[,] f, int x, int y, bool dark)
    {
        m[y, x] = dark;
        f[y, x] = true;
    }

    static void DrawFunctionPatterns(int version, int size, bool[,] m, bool[,] f)
    {
        for (int i = 0; i < size; i++)
        {
            SetFunction(m, f, 6, i, i % 2 == 0);
            SetFunction(m, f, i, 6, i % 2 == 0);
        }

        DrawFinder(size, m, f, 3, 3);
        DrawFinder(size, m, f, size - 4, 3);
        DrawFinder(size, m, f, 3, size - 4);

        int[] align = AlignmentPositions[version];
        int n = align.Length;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        SetFunction(m, f, align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

        DrawFormatBits(size, 0, m, f); // reserve format areas (overwritten after masking)
    }

    static void DrawFinder(int size, bool[,] m, bool[,] f, int cx, int cy)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                SetFunction(m, f, x, y, dist != 2 && dist != 4);
            }
    }

    static void DrawFormatBits(int size, int mask, bool[,] m, bool[,] f)
    {
        int data = (EclFormatBits << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFunction(m, f, 8, i, Bit(bits, i));
        SetFunction(m, f, 8, 7, Bit(bits, 6));
        SetFunction(m, f, 8, 8, Bit(bits, 7));
        SetFunction(m, f, 7, 8, Bit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunction(m, f, 14 - i, 8, Bit(bits, i));

        for (int i = 0; i < 8; i++) SetFunction(m, f, size - 1 - i, 8, Bit(bits, i));
        for (int i = 8; i < 15; i++) SetFunction(m, f, 8, size - 15 + i, Bit(bits, i));
        SetFunction(m, f, 8, size - 8, true); // dark module
    }

    static bool Bit(int value, int i) => ((value >> i) & 1) != 0;

    static void DrawCodewords(int size, byte[] data, bool[,] m, bool[,] f)
    {
        int i = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (!f[y, x] && i < data.Length * 8)
                    {
                        m[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                        i++;
                    }
                }
            }
        }
    }

    static void ApplyMask(int size, int mask, bool[,] m, bool[,] f)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool invert;
                switch (mask)
                {
                    case 0: invert = (x + y) % 2 == 0; break;
                    case 1: invert = y % 2 == 0; break;
                    case 2: invert = x % 3 == 0; break;
                    case 3: invert = (x + y) % 3 == 0; break;
                    case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                    case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                    case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                    default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                }
                if (invert && !f[y, x]) m[y, x] = !m[y, x];
            }
    }

    /// <summary>Standard penalty rules N1-N4 (used only to pick a readable mask).</summary>
    static int Penalty(int size, bool[,] m)
    {
        int penalty = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < size; a++)
            {
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? m[a, b] : m[b, a];
                    bool prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                    if (cur == prev)
                    {
                        run++;
                        if (run == 5) penalty += 3;
                        else if (run > 5) penalty++;
                    }
                    else run = 1;
                }
                for (int b = 0; b + 10 < size; b++)
                {
                    bool Get(int k) => pass == 0 ? m[a, b + k] : m[b + k, a];
                    bool core = Get(0) && !Get(1) && Get(2) && Get(3) && Get(4) && !Get(5) && Get(6);
                    bool pattern1 = core && !Get(7) && !Get(8) && !Get(9) && !Get(10);
                    bool pattern2 = !Get(0) && !Get(1) && !Get(2) && !Get(3) && Get(4) && !Get(5) && Get(6) && Get(7) && Get(8) && !Get(9) && Get(10);
                    if (pattern1 || pattern2) penalty += 40;
                }
            }
        }
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
            {
                bool c = m[y, x];
                if (c == m[y, x + 1] && c == m[y + 1, x] && c == m[y + 1, x + 1]) penalty += 3;
            }
        int dark = 0;
        foreach (bool b in m) if (b) dark++;
        int total = size * size;
        int k2 = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        penalty += Math.Max(0, k2) * 10;
        return penalty;
    }
}
