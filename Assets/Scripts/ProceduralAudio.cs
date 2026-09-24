using System;
using UnityEngine;

/// <summary>
/// Audio generated in code at startup (cheap PCM loops): kart engine, drift screech,
/// countdown beeps and a small chiptune music engine (square lead, triangle bass,
/// noise drums) with race and lobby arrangements.
/// </summary>
public static class ProceduralAudio
{
    public const int Rate = 22050;

    /// <summary>Engine hum: 1 s loop of a buzzy 110 Hz tone (integer cycles, loops seamlessly).</summary>
    public static AudioClip Engine(int seed)
    {
        var rng = new System.Random(seed);
        int n = Rate;
        var data = new float[n];
        float noise = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float p = t * 110f;
            float saw = 2f * (p - Mathf.Floor(p + 0.5f));
            float sub = Mathf.Sin(2f * Mathf.PI * 55f * t);
            float pulse = (p * 2f % 1f) < 0.3f ? 1f : -1f;
            noise = noise * 0.9f + ((float)rng.NextDouble() * 2f - 1f) * 0.1f;
            data[i] = 0.35f * saw + 0.35f * sub + 0.15f * pulse + 0.25f * noise;
        }
        return Make("Engine", data);
    }

    /// <summary>Tyre screech: band-limited noise with a wavering whistle, 1 s loop.</summary>
    public static AudioClip Screech()
    {
        var rng = new System.Random(7);
        int n = Rate;
        var data = new float[n];
        float lp = 0f, hp = 0f, prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float white = (float)rng.NextDouble() * 2f - 1f;
            lp += (white - lp) * 0.35f;
            hp = 0.8f * (hp + lp - prev);
            prev = lp;
            float whistle = Mathf.Sin(2f * Mathf.PI * (1800f + 60f * Mathf.Sin(2f * Mathf.PI * 3f * t)) * t);
            float fade = Mathf.Min(1f, Mathf.Min(i, n - i) / 400f); // soft loop seam
            data[i] = (0.6f * hp + 0.25f * whistle) * Mathf.Lerp(0.7f, 1f, fade);
        }
        return Make("Screech", data);
    }

    /// <summary>Countdown beep: a short sine blip with a quick decay.</summary>
    public static AudioClip Beep(string name, float freq, float seconds)
    {
        int n = (int)(Rate * seconds);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float env = Mathf.Min(1f, i / 80f) * Mathf.Exp(-t * 3f / seconds);
            data[i] = env * (0.7f * Mathf.Sin(2f * Mathf.PI * freq * t) + 0.3f * Square(freq * t, 0.5f));
        }
        return Make(name, data);
    }

    // ------------------------------------------------------------------------------------------
    // Chiptune: 8-bar loops in C major over I-V-vi-IV. Each voice is rendered note by note.

    static readonly int[] ChordRoots = { 0, 7, 9, 5 };           // C G Am F
    static readonly int[][] ChordTones = { new[] { 0, 4, 7 }, new[] { 7, 11, 14 }, new[] { 9, 12, 16 }, new[] { 5, 9, 12 } };

    // Lead melodies, 8 sixteenth-note slots per beat pair (-1 = rest). Two phrases alternate.
    static readonly int[][] RaceLead =
    {
        new[] { 12, -1, 16, 19, 24, -1, 19, 16, 14, -1, 16, 19, 23, -1, 19, 14 },
        new[] { 16, -1, 19, 21, 24, 21, 19, 16, 17, -1, 21, 24, 21, -1, 17, 16 },
    };
    static readonly int[][] LobbyLead =
    {
        new[] { 12, -1, -1, 16, -1, -1, 19, -1, 14, -1, -1, 17, -1, -1, 19, -1 },
        new[] { 16, -1, -1, 19, -1, -1, 21, -1, 17, -1, -1, 16, -1, -1, 12, -1 },
    };

    public static AudioClip RaceMusic() => Song("RaceLoop", 150f, RaceLead, true, 0.9f);
    public static AudioClip LobbyMusic() => Song("LobbyLoop", 108f, LobbyLead, false, 0.6f);

    static AudioClip Song(string name, float bpm, int[][] lead, bool drums, float leadVolume)
    {
        const int bars = 8;
        float beat = 60f / bpm;
        float sixteenth = beat / 4f;
        int n = Mathf.RoundToInt(bars * 4 * beat * Rate);
        var data = new float[n];
        var rng = new System.Random(3);

        for (int bar = 0; bar < bars; bar++)
        {
            int chord = bar % 4;
            float barStart = bar * 4 * beat;

            // Bass: root on every eighth, octave jump on the off-beats.
            for (int e = 0; e < 8; e++)
            {
                int note = 36 + ChordRoots[chord] + (e % 2 == 1 && drums ? 12 : 0);
                AddNote(data, barStart + e * beat / 2f, beat / 2f * 0.9f, Midi(note), 0.28f, Wave.Triangle);
            }

            // Lead: alternate the two phrases; the second half of the loop goes up an octave for lift.
            int[] phrase = lead[(bar / 2) % lead.Length];
            int transpose = bar >= 4 ? 12 : 0;
            for (int s = 0; s < 16; s++)
            {
                int step = phrase[s];
                if (step < 0) continue;
                int note = 60 + step - 12 + transpose;
                float len = sixteenth * (drums ? 1.6f : 2.8f);
                AddNote(data, barStart + s * sixteenth, len, Midi(note), 0.16f * leadVolume, Wave.Square25);
            }

            // Arpeggio pad (quiet) on chord tones.
            for (int s = 0; s < 16; s += 2)
            {
                int tone = ChordTones[chord][(s / 2) % 3];
                AddNote(data, barStart + s * sixteenth, sixteenth * 1.5f, Midi(60 + tone), 0.05f, Wave.Square50);
            }

            if (!drums) continue;
            for (int b = 0; b < 4; b++)
            {
                float t = barStart + b * beat;
                if (b % 2 == 0) AddKick(data, t);
                else AddNoise(data, t, 0.12f, 0.22f, rng, 0.5f); // snare
                AddNoise(data, t + beat / 2f, 0.03f, 0.08f, rng, 0.9f); // hat
            }
        }

        // Normalise gently.
        float peak = 0.001f;
        foreach (float v in data) peak = Mathf.Max(peak, Mathf.Abs(v));
        float gain = 0.8f / peak;
        for (int i = 0; i < n; i++) data[i] *= gain;
        return Make(name, data);
    }

    enum Wave { Square25, Square50, Triangle }

    static float Midi(int note) => 440f * Mathf.Pow(2f, (note - 69) / 12f);

    static void AddNote(float[] data, float start, float length, float freq, float volume, Wave wave)
    {
        int i0 = Mathf.RoundToInt(start * Rate);
        int count = Mathf.RoundToInt(length * Rate);
        for (int k = 0; k < count && i0 + k < data.Length; k++)
        {
            float t = k / (float)Rate;
            float phase = freq * t;
            float env = Mathf.Min(1f, k / 60f) * Mathf.Clamp01((count - k) / 300f) * (1f - 0.35f * k / count);
            float v = wave == Wave.Triangle ? 1f - 4f * Mathf.Abs(phase % 1f - 0.5f)
                    : Square(phase, wave == Wave.Square25 ? 0.25f : 0.5f);
            data[i0 + k] += v * volume * env;
        }
    }

    static void AddKick(float[] data, float start)
    {
        int i0 = Mathf.RoundToInt(start * Rate);
        int count = (int)(0.18f * Rate);
        float phase = 0f;
        for (int k = 0; k < count && i0 + k < data.Length; k++)
        {
            float t = k / (float)Rate;
            float freq = Mathf.Lerp(120f, 45f, t / 0.18f);
            phase += freq / Rate;
            data[i0 + k] += Mathf.Sin(2f * Mathf.PI * phase) * Mathf.Exp(-t * 18f) * 0.5f;
        }
    }

    static void AddNoise(float[] data, float start, float length, float volume, System.Random rng, float brightness)
    {
        int i0 = Mathf.RoundToInt(start * Rate);
        int count = (int)(length * Rate);
        float lp = 0f;
        for (int k = 0; k < count && i0 + k < data.Length; k++)
        {
            float white = (float)rng.NextDouble() * 2f - 1f;
            lp += (white - lp) * brightness;
            data[i0 + k] += lp * volume * Mathf.Exp(-k / (float)count * 4f);
        }
    }

    static float Square(float phase, float duty) => (phase % 1f) < duty ? 1f : -1f;

    static AudioClip Make(string name, float[] data)
    {
        var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
