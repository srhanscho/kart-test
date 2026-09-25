using UnityEngine;

/// <summary>
/// One item slot per kart: roulette after an item box, use on the item button's
/// press edge, shield bubble, boost trail, and taking hits.
/// </summary>
[RequireComponent(typeof(KartController))]
public class KartItems : MonoBehaviour
{
    public const float RouletteTime = 1f;
    const float ShieldTime = 6f;

    KartController kart;
    GameObject bubble;
    TrailRenderer trail;
    bool lastButton;
    float rollEnd, shieldTimer;
    static readonly System.Random Rng = new System.Random();

    public ItemType Held { get; private set; }
    /// <summary>Counts item-box pickups that started a roulette (for UI pop / tests).</summary>
    public int Pops { get; private set; }
    public bool IsRolling { get; private set; }
    public bool ShieldActive => shieldTimer > 0f;
    public bool IsAi { get; set; }
    public KartController Kart => kart;
    public int HitsTaken { get; private set; }
    public int HitsBlocked { get; private set; }
    public int Crashes { get; private set; }
    public float LastTurboTime { get; private set; } = -10f;
    float nextTick;
    bool Local => !IsAi; // human karts: 2D sounds and roulette ticks
    public float InvulnerableUntil { get; set; }
    public bool Invulnerable => Time.time < InvulnerableUntil;

    /// <summary>Item shown in the HUD while the roulette spins.</summary>
    public ItemType RouletteDisplay => ItemTable.Items[(int)(Time.time * 12f) % ItemTable.Items.Length];

    void Awake()
    {
        kart = GetComponent<KartController>();
        kart.Crashed += OnCrashed;
        var manager = ItemManager.Instance != null ? ItemManager.Instance : FindAnyObjectByType<ItemManager>();

        bubble = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        bubble.name = "ShieldBubble";
        Destroy(bubble.GetComponent<Collider>());
        bubble.transform.SetParent(transform, false);
        bubble.transform.localPosition = new Vector3(0f, 0.9f, 0f);
        bubble.transform.localScale = Vector3.one * 4.2f;
        if (manager != null) bubble.GetComponent<Renderer>().sharedMaterial = manager.ShieldMaterial;
        bubble.SetActive(false);

        var trailGo = new GameObject("BoostTrail");
        trailGo.transform.SetParent(transform, false);
        trailGo.transform.localPosition = new Vector3(0f, 0.5f, -1.6f);
        trail = trailGo.AddComponent<TrailRenderer>();
        trail.time = 0.35f;
        trail.widthMultiplier = 0.9f;
        trail.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        trail.startColor = new Color(1f, 0.8f, 0.2f, 0.9f);
        trail.endColor = new Color(1f, 0.2f, 0f, 0f);
        trail.emitting = false;
        if (manager != null) trail.sharedMaterial = manager.TrailMaterial;
    }

    public void ResetItems()
    {
        Held = ItemType.None;
        IsRolling = false;
        shieldTimer = 0f;
        lastButton = false;
        if (bubble != null) bubble.SetActive(false);
    }

    /// <summary>Item box hit: start the roulette if the slot is free.</summary>
    public bool TryStartRoulette()
    {
        if (Held != ItemType.None || IsRolling) return false;
        IsRolling = true;
        rollEnd = Time.time + RouletteTime;
        Pops++;
        return true;
    }

    /// <summary>Test/debug hook: fill the slot directly.</summary>
    public void GiveItem(ItemType item)
    {
        IsRolling = false;
        Held = item;
    }

    void Update()
    {
        if (IsRolling && Local && Time.time >= nextTick)
        {
            nextTick = Time.time + 0.09f;
            GameAudio.Play(Sfx.RouletteTick, null, 0.35f, 1f + 0.1f * Mathf.Sin(Time.time * 9f));
        }
        if (IsRolling && Time.time >= rollEnd)
        {
            IsRolling = false;
            var rm = RaceManager.Instance;
            int position = rm != null ? rm.PositionOf(kart) : 1;
            int count = rm != null ? rm.RacerCount : 1;
            Held = ItemTable.Roll(position, count, Rng);
            if (Local) GameAudio.Play(Sfx.RouletteDing, null, 0.8f);
        }

        bool button = kart.CurrentInput != null && kart.CurrentInput.UseItem;
        if (button && !lastButton && !kart.ControlsLocked) UseHeldItem();
        lastButton = button;

        if (shieldTimer > 0f)
        {
            shieldTimer -= Time.deltaTime;
            float pulse = 1f + Mathf.Sin(Time.time * 8f) * 0.03f;
            bubble.transform.localScale = Vector3.one * 4.2f * pulse;
        }
        bubble.SetActive(shieldTimer > 0f);
        trail.emitting = kart.IsBoosting;
    }

    /// <summary>Uses whatever is in the slot (no-op when empty or rolling).</summary>
    public void UseHeldItem()
    {
        if (IsRolling || Held == ItemType.None) return;
        ItemType item = Held;
        Held = ItemType.None;
        var manager = ItemManager.Instance;
        switch (item)
        {
            case ItemType.Banana:
                manager?.SpawnBanana(this);
                Sound(Sfx.BananaDrop);
                break;
            case ItemType.Turbo:
                kart.ApplyBoost(1.3f, 1.35f);
                LastTurboTime = Time.time;
                Sound(Sfx.Turbo);
                break;
            case ItemType.Rocket:
                manager?.FireRocket(this);
                Sound(Sfx.RocketLaunch);
                break;
            case ItemType.Shield:
                shieldTimer = ShieldTime;
                Sound(Sfx.ShieldUp);
                break;
        }
        manager?.RecordUse(item, IsAi);
    }

    void Sound(Sfx id, float volume = 1f)
    {
        if (Local) GameAudio.Play(id, null, volume);
        else GameAudio.Play(id, transform.position, volume);
    }

    /// <summary>Wall / kart impact or hazard contact: shield blocks it, otherwise a short spin-out.</summary>
    void OnCrashed(KartController k, bool hazard)
    {
        if (Invulnerable) return;
        Sound(hazard ? Sfx.ObstacleHit : Sfx.WallCrash);
        if (ShieldActive)
        {
            shieldTimer = 0f;
            HitsBlocked++;
            Sound(Sfx.ShieldBreak);
            return;
        }
        Crashes++;
        kart.SpinOut(hazard ? 1.1f : 0.8f);
        kart.Slow(0.5f);
    }

    /// <summary>Returns true if the hit landed (false when the shield absorbed it).</summary>
    public bool Hit(ItemType source)
    {
        if (Invulnerable) return false;
        if (ShieldActive)
        {
            shieldTimer = 0f;
            HitsBlocked++;
            Sound(Sfx.ShieldBreak);
            return false;
        }
        HitsTaken++;
        if (source == ItemType.Banana) Sound(Sfx.BananaSlip);
        kart.SpinOut(source == ItemType.Rocket ? 1.4f : 1.1f);
        kart.Slow(source == ItemType.Rocket ? 0.25f : 0.45f);
        if (source == ItemType.Rocket) kart.Hop(5f);
        return true;
    }
}
