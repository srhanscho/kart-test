using UnityEngine;

/// <summary>
/// Per-kart sound: procedural engine loop (pitch/volume follow speed and throttle, small
/// per-kart variance), tyre screech while drifting, and hop/land/mini-turbo/spin cues.
/// Human karts play 2D and a bit quieter (split screen shares one listener); CPU karts are
/// 3D and fall silent when far from the listener, which keeps the mix readable.
/// </summary>
[RequireComponent(typeof(KartController))]
public class KartAudio : MonoBehaviour
{
    const float AudibleRange = 80f;

    KartController kart;
    KartItems items;
    AudioSource engine, screech;
    float variance;
    bool wasGrounded = true, wasTurboReady, wasBoosting, wasSpinning;
    float airAccum;

    /// <summary>Set by RaceManager for karts driven by people.</summary>
    public bool Human { get; set; }
    public bool EnginePlaying => engine != null && engine.isPlaying;
    public float EnginePitch => engine != null ? engine.pitch : 0f;
    public AudioSource Engine => engine;

    void Start()
    {
        kart = GetComponent<KartController>();
        items = GetComponent<KartItems>();
        int seed = gameObject.name.GetHashCode();
        variance = new System.Random(seed).Next(-60, 60) / 1000f;
        engine = MakeSource("Engine", ProceduralAudio.Engine(seed));
        var manager = AudioManager.Instance;
        screech = MakeSource("Screech", manager != null ? manager.ScreechClip : ProceduralAudio.Screech());
        engine.Play();
        screech.volume = 0f;
        screech.Play();
        kart.Hopped += () => Cue(Sfx.Hop, 0.7f);
    }

    AudioSource MakeSource(string name, AudioClip clip)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.loop = true;
        src.playOnAwake = false;
        src.minDistance = 5f;
        src.maxDistance = AudibleRange;
        src.rolloffMode = AudioRolloffMode.Linear;
        src.dopplerLevel = 0f;
        return src;
    }

    void Update()
    {
        var manager = AudioManager.Instance;
        float sfx = manager != null ? manager.SfxVolume : 0.8f;
        float speed01 = Mathf.Clamp01(Mathf.Abs(kart.ForwardSpeed) / 26f);
        float throttle = kart.ControlsLocked || kart.CurrentInput == null ? 0f : Mathf.Max(0f, kart.CurrentInput.Throttle);

        engine.spatialBlend = screech.spatialBlend = Human ? 0f : 1f;
        float pitch = (0.55f + speed01 * 1.25f + throttle * 0.12f + (kart.IsBoosting ? 0.15f : 0f)) * (1f + variance);
        engine.pitch = Mathf.Lerp(engine.pitch, pitch, 1f - Mathf.Exp(-10f * Time.deltaTime));
        float volume = sfx * (Human ? 0.22f : 0.5f) * (0.45f + 0.45f * speed01 + 0.15f * throttle);

        // CPU karts far from the listener stay silent (voice budget).
        if (!Human)
        {
            var listener = FindListener();
            if (listener != null && Vector3.Distance(listener.position, transform.position) > AudibleRange) volume = 0f;
        }
        engine.volume = volume;

        bool drifting = kart.IsDrifting && kart.IsGrounded;
        screech.volume = Mathf.MoveTowards(screech.volume, drifting ? volume * 0.9f : 0f, Time.deltaTime * 3f);
        screech.pitch = 0.9f + speed01 * 0.3f;

        // One-shot cues from state changes.
        if (!kart.IsGrounded) airAccum += Time.deltaTime;
        else
        {
            if (!wasGrounded && airAccum > 0.35f) Cue(Sfx.Land, 0.6f);
            airAccum = 0f;
        }
        wasGrounded = kart.IsGrounded;

        bool ready = kart.MiniTurboReady;
        if (ready && !wasTurboReady) Cue(Sfx.MiniTurboReady, 0.6f);
        wasTurboReady = ready;

        bool boosting = kart.IsBoosting;
        bool itemTurbo = items != null && Time.time - items.LastTurboTime < 0.2f;
        if (boosting && !wasBoosting && !itemTurbo) Cue(Sfx.MiniTurboRelease, 0.8f);
        wasBoosting = boosting;

        bool spinning = kart.IsSpinningOut;
        if (spinning && !wasSpinning) Cue(Sfx.SpinOut, 0.8f);
        wasSpinning = spinning;
    }

    void Cue(Sfx id, float volume)
    {
        if (Human) GameAudio.Play(id, null, volume);
        else GameAudio.Play(id, transform.position, volume);
    }

    static Transform listenerCache;
    static Transform FindListener()
    {
        if (listenerCache == null)
        {
            var l = FindAnyObjectByType<AudioListener>();
            listenerCache = l != null ? l.transform : null;
        }
        return listenerCache;
    }
}
