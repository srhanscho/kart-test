using System;
using System.Collections.Generic;
using UnityEngine;

public enum Sfx
{
    Hop, Land, ItemBox, RouletteTick, RouletteDing, BananaDrop, BananaSlip, RocketLaunch, Explosion,
    ShieldUp, ShieldBreak, Turbo, MiniTurboReady, MiniTurboRelease, WallCrash, SpinOut, ObstacleHit,
    Rescue, LapComplete, FinalLap, Finish1st, FinishPodium, FinishOther, UiMove, UiSelect, UiReady, UiBack,
    CountBeep, CountGo
}

public enum MusicState { None, Lobby, Race, FinalLap }

/// <summary>
/// Central audio: SFX (Kenney clips + generated beeps), procedural chiptune music with
/// lobby / race / final-lap states and ducking under jingles, and volume controls.
/// Game code calls the static GameAudio helpers so it works without a manager too.
/// </summary>
public class AudioManager : MonoBehaviour
{
    [Serializable]
    public class SfxEntry
    {
        public Sfx id;
        public AudioClip[] clips;
        [Range(0f, 1f)] public float volume = 0.8f;
        public bool isJingle;
    }

    [SerializeField] SfxEntry[] entries = Array.Empty<SfxEntry>();
    [SerializeField, Range(0f, 1f)] float masterVolume = 0.9f;
    [SerializeField, Range(0f, 1f)] float musicVolume = 0.45f;
    [SerializeField, Range(0f, 1f)] float sfxVolume = 0.9f;
    [SerializeField] int voices = 12;

    readonly Dictionary<Sfx, SfxEntry> map = new Dictionary<Sfx, SfxEntry>();
    readonly Dictionary<Sfx, int> played = new Dictionary<Sfx, int>();
    AudioSource[] pool;
    int nextVoice;
    AudioSource music;
    AudioClip raceClip, lobbyClip;
    float duckUntil;
    bool musicMuted;
    readonly System.Random rng = new System.Random();

    public static AudioManager Instance { get; private set; }
    public MusicState Music { get; private set; }
    public bool MusicPlaying => music != null && music.isPlaying && !musicMuted;
    public float MusicPitch => music != null ? music.pitch : 0f;
    public AudioClip EngineClipFor(int seed) => ProceduralAudio.Engine(seed);
    public AudioClip ScreechClip { get; private set; }
    public float SfxVolume => masterVolume * sfxVolume;
    public int PlayedCount(Sfx id) => played.TryGetValue(id, out int n) ? n : 0;

    public void Configure(SfxEntry[] sfx) => entries = sfx;

    void Awake()
    {
        Instance = this;
        foreach (var e in entries) map[e.id] = e;

        pool = new AudioSource[voices];
        for (int i = 0; i < voices; i++)
        {
            var voice = new GameObject($"SfxVoice_{i}");
            voice.transform.SetParent(transform, false);
            var src = voice.AddComponent<AudioSource>();
            src.playOnAwake = false;
            pool[i] = src;
        }
        music = gameObject.AddComponent<AudioSource>();
        music.loop = true;
        music.playOnAwake = false;
        music.spatialBlend = 0f;

        // Generated audio.
        raceClip = ProceduralAudio.RaceMusic();
        lobbyClip = ProceduralAudio.LobbyMusic();
        ScreechClip = ProceduralAudio.Screech();
        AddGenerated(Sfx.CountBeep, ProceduralAudio.Beep("Beep", 660f, 0.18f), 0.7f);
        AddGenerated(Sfx.CountGo, ProceduralAudio.Beep("Go", 1320f, 0.5f), 0.8f);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void AddGenerated(Sfx id, AudioClip clip, float volume)
    {
        if (!map.ContainsKey(id)) map[id] = new SfxEntry { id = id, clips = new[] { clip }, volume = volume };
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.M))
        {
            musicMuted = !musicMuted;
            Debug.Log("[AudioManager] music " + (musicMuted ? "off" : "on"));
        }
        float target = musicMuted ? 0f : masterVolume * musicVolume * (Time.unscaledTime < duckUntil ? 0.25f : 1f);
        music.volume = Mathf.MoveTowards(music.volume, target, Time.unscaledDeltaTime * 1.5f);
    }

    public void SetMusic(MusicState state)
    {
        if (state == Music) return;
        Music = state;
        switch (state)
        {
            case MusicState.None:
                music.Stop();
                return;
            case MusicState.Lobby:
                Switch(lobbyClip, 1f);
                break;
            case MusicState.Race:
                Switch(raceClip, 1f);
                break;
            case MusicState.FinalLap:
                // Faster and higher, like the classic final-lap switch.
                if (music.clip != raceClip) Switch(raceClip, 1.15f);
                else music.pitch = 1.15f;
                break;
        }
    }

    void Switch(AudioClip clip, float pitch)
    {
        music.clip = clip;
        music.pitch = pitch;
        music.time = 0f;
        music.Play();
    }

    /// <summary>Plays a sound: 2D when position is null, otherwise 3D at the position.</summary>
    public AudioSource Play(Sfx id, Vector3? position = null, float volumeScale = 1f, float pitch = 1f)
    {
        played[id] = PlayedCount(id) + 1;
        if (!map.TryGetValue(id, out var e) || e.clips == null || e.clips.Length == 0) return null;
        AudioClip clip = e.clips[rng.Next(e.clips.Length)];
        if (clip == null) return null;

        AudioSource src = pool[nextVoice];
        nextVoice = (nextVoice + 1) % pool.Length;
        src.clip = clip;
        src.volume = e.volume * volumeScale * SfxVolume;
        src.pitch = pitch;
        if (position.HasValue)
        {
            src.transform.position = position.Value; // each voice is its own child object
            src.spatialBlend = 0.85f;
            src.minDistance = 6f;
            src.maxDistance = 90f;
        }
        else src.spatialBlend = 0f;
        src.Play();
        if (e.isJingle) duckUntil = Time.unscaledTime + clip.length + 0.3f;
        return src;
    }
}

/// <summary>Static helpers so gameplay code can fire sounds without holding references.</summary>
public static class GameAudio
{
    public static void Play(Sfx id, Vector3? position = null, float volume = 1f, float pitch = 1f)
    {
        var m = AudioManager.Instance;
        if (m != null) m.Play(id, position, volume, pitch);
    }

    public static void Music(MusicState state) => AudioManager.Instance?.SetMusic(state);
}
