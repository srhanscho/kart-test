using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns item objects (banana peels, rockets, explosions), keeps usage stats,
/// and holds the generated materials/model references. One per scene.
/// </summary>
public class ItemManager : MonoBehaviour
{
    [SerializeField] GameObject bananaModel;
    [SerializeField] float bananaScale = 1f;
    [SerializeField] Vector3 bananaOffset;
    [SerializeField] Material rocketMaterial;
    [SerializeField] Material shieldMaterial;
    [SerializeField] Material explosionMaterial;
    [SerializeField] Material trailMaterial;
    [SerializeField] RaceTrack track;

    readonly List<GameObject> spawned = new List<GameObject>();
    readonly List<RocketProjectile> rockets = new List<RocketProjectile>();
    readonly Dictionary<ItemType, int> uses = new Dictionary<ItemType, int>();

    public static ItemManager Instance { get; private set; }
    public Material ShieldMaterial => shieldMaterial;
    public Material TrailMaterial => trailMaterial;
    public RaceTrack Track => track;
    /// <summary>The active track changed (track selection in the lobby).</summary>
    public void SetTrack(RaceTrack raceTrack) => track = raceTrack;
    public IReadOnlyList<RocketProjectile> Rockets => rockets;
    public int AiUses { get; private set; }
    public int UsesOf(ItemType t) => uses.TryGetValue(t, out int n) ? n : 0;

    public void Configure(GameObject banana, float scale, Vector3 offset, Material rocket, Material shield,
        Material explosion, Material trailMat, RaceTrack raceTrack)
    {
        bananaModel = banana;
        bananaScale = scale;
        bananaOffset = offset;
        rocketMaterial = rocket;
        shieldMaterial = shield;
        explosionMaterial = explosion;
        trailMaterial = trailMat;
        track = raceTrack;
    }

    void Awake()
    {
        Instance = this;
        KartVisualFx.ParticleMaterial = trailMaterial;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void RecordUse(ItemType item, bool byAi)
    {
        uses[item] = UsesOf(item) + 1;
        if (byAi) AiUses++;
    }

    /// <summary>Removes every banana/rocket on the track (race restart).</summary>
    public void ClearAll()
    {
        foreach (var go in spawned) if (go != null) Destroy(go);
        spawned.Clear();
        rockets.Clear();
    }

    public void SpawnBanana(KartItems owner)
    {
        Transform t = owner.transform;
        SpawnBananaAt(t.position - t.forward * 2.8f, t.rotation, owner);
    }

    public BananaPeel SpawnBananaAt(Vector3 position, Quaternion rotation, KartItems owner)
    {
        var root = new GameObject("BananaPeel");
        root.transform.SetPositionAndRotation(GroundPoint(position), rotation);
        if (bananaModel != null)
        {
            GameObject model = Instantiate(bananaModel, root.transform, false);
            model.transform.localPosition = bananaOffset;
            model.transform.localScale = Vector3.one * bananaScale;
            foreach (var c in model.GetComponentsInChildren<Collider>()) Destroy(c);
            ToonStyle.Instance?.Apply(model);
        }
        // Low, flat trigger so a well-timed hop clears the peel.
        var trigger = root.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = new Vector3(1.4f, 0.5f, 1.4f);
        trigger.center = new Vector3(0f, 0.25f, 0f);
        var peel = root.AddComponent<BananaPeel>();
        peel.Init(owner);
        Register(root);
        return peel;
    }

    public RocketProjectile FireRocket(KartItems owner)
    {
        Transform t = owner.transform;
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "Rocket";
        Destroy(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().sharedMaterial = rocketMaterial;
        var body = go.transform;
        body.position = t.position + t.forward * 3f + Vector3.up * 0.8f;
        body.rotation = Quaternion.LookRotation(t.forward);

        // Capsule points along Y; wrap it so the long axis follows the flight direction.
        var root = new GameObject("RocketProjectile").transform;
        root.SetPositionAndRotation(body.position, body.rotation);
        body.SetParent(root, true);
        body.localPosition = Vector3.zero;
        body.localRotation = Quaternion.Euler(90f, 0f, 0f);
        body.localScale = new Vector3(0.45f, 0.6f, 0.45f);

        var trail = root.gameObject.AddComponent<TrailRenderer>();
        trail.time = 0.4f;
        trail.widthMultiplier = 0.45f;
        trail.startColor = new Color(1f, 0.7f, 0.2f, 1f);
        trail.endColor = new Color(0.4f, 0.4f, 0.4f, 0f);
        trail.sharedMaterial = trailMaterial;

        ToonStyle.Instance?.Apply(root.gameObject);
        var rocket = root.gameObject.AddComponent<RocketProjectile>();
        rocket.Init(owner, this);
        rockets.Add(rocket);
        Register(root.gameObject);
        return rocket;
    }

    public void RocketGone(RocketProjectile rocket) => rockets.Remove(rocket);

    public void Explode(Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Explosion";
        Destroy(go.GetComponent<Collider>());
        go.transform.position = position;
        go.GetComponent<Renderer>().material = explosionMaterial; // instance: alpha fades per explosion
        go.AddComponent<ExplosionFlash>();
    }

    /// <summary>True if a rocket not fired by this kart is close and flying toward it.</summary>
    public bool RocketIncoming(KartController kart, float range)
    {
        foreach (var r in rockets)
        {
            if (r == null || r.Owner == kart) continue;
            Vector3 to = kart.transform.position - r.transform.position;
            if (to.magnitude < range && Vector3.Dot(to.normalized, r.transform.forward) > 0.5f) return true;
        }
        return false;
    }

    Vector3 GroundPoint(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 10f, ~0, QueryTriggerInteraction.Ignore)
            && hit.collider.GetComponentInParent<KartController>() == null)
            return hit.point;
        return new Vector3(p.x, 0.1f, p.z);
    }

    void Register(GameObject go)
    {
        spawned.RemoveAll(x => x == null);
        spawned.Add(go);
    }
}
