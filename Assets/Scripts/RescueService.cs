using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Pit Pal" rescue: when a kart leaves the track (falls, stays off the track surface,
/// or gets stuck) a little helper on a hovering propeller platform flies in, lifts the
/// kart and puts it back on the centerline at its last safe spot, then the kart blinks
/// (invulnerable) for a moment. Works for humans and CPUs.
/// </summary>
public class RescueService : MonoBehaviour
{
    [SerializeField] GameObject helperModel;
    [SerializeField] float helperScale = 1f;
    [SerializeField] Material platformMaterial;
    [SerializeField] Material rotorMaterial;
    [SerializeField] AnimationClip helperClip;
    [SerializeField] Avatar helperAvatar;
    [SerializeField] float killHeight = -4f;
    [SerializeField] float offTrackTime = 2.5f;

    class WatchState
    {
        public float offTrack;
        public float lastSafeS = -1f;
        public bool rescuing;
    }

    readonly Dictionary<KartController, WatchState> watches = new Dictionary<KartController, WatchState>();
    RaceTrack track;

    public static RescueService Instance { get; private set; }
    public int RescueCount { get; private set; }
    public bool IsRescuing(KartController k) => watches.TryGetValue(k, out var w) && w.rescuing;
    public GameObject LastHelper { get; private set; }

    public void Configure(GameObject helper, float scale, Material platform, Material rotor, AnimationClip clip, Avatar avatar)
    {
        helperClip = clip;
        helperAvatar = avatar;
        helperModel = helper;
        helperScale = scale;
        platformMaterial = platform;
        rotorMaterial = rotor;
    }

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void ResetAll()
    {
        StopAllCoroutines();
        foreach (var kv in watches)
        {
            kv.Value.rescuing = false;
            kv.Value.offTrack = 0f;
            kv.Value.lastSafeS = -1f;
            if (kv.Key.Body != null) kv.Key.Body.isKinematic = false;
            if (kv.Key.Visual != null) kv.Key.Visual.gameObject.SetActive(true);
        }
        if (LastHelper != null) Destroy(LastHelper);
    }

    /// <summary>Called every frame by RaceManager for each racing kart.</summary>
    public void Watch(KartController kart, RaceTrack raceTrack, bool forceRescue)
    {
        track = raceTrack;
        if (!watches.TryGetValue(kart, out var w)) watches[kart] = w = new WatchState();
        if (w.rescuing) return;

        Vector3 p = kart.transform.position;
        if (kart.IsGrounded && kart.IsOnRoad)
        {
            w.offTrack = 0f;
            w.lastSafeS = track.Project(p);
        }
        else w.offTrack += Time.deltaTime;

        if (forceRescue || p.y < killHeight || w.offTrack > offTrackTime)
            StartCoroutine(Rescue(kart, w));
    }

    /// <summary>Starts a rescue right away (tests / stuck CPUs).</summary>
    public void RescueNow(KartController kart, RaceTrack raceTrack)
    {
        track = raceTrack;
        if (!watches.TryGetValue(kart, out var w)) watches[kart] = w = new WatchState();
        if (!w.rescuing) StartCoroutine(Rescue(kart, w));
    }

    IEnumerator Rescue(KartController kart, WatchState w)
    {
        w.rescuing = true;
        RescueCount++;
        Rigidbody body = kart.Body;
        body.isKinematic = true;
        kart.SetControlsLocked(true);

        float s = w.lastSafeS >= 0f ? w.lastSafeS : track.Project(kart.transform.position);
        s -= 4f; // a little behind where it left, so it does not land in the same trap
        Vector3 dropPoint = track.PointAt(s);
        if (Physics.Raycast(dropPoint + Vector3.up * 8f, Vector3.down, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore)
            && hit.collider.GetComponentInParent<KartController>() == null)
            dropPoint = hit.point;
        dropPoint += Vector3.up * 0.4f;
        Quaternion dropRot = Quaternion.LookRotation(track.TangentAt(s));

        GameObject helper = BuildHelper();
        GameAudio.Play(Sfx.Rescue, kart.transform.position, 1f);
        LastHelper = helper;
        Transform h = helper.transform;
        Vector3 kartPos = kart.transform.position;
        Vector3 hoverOffset = Vector3.up * 2.6f;

        // 1) fly down to the kart
        yield return Move(h, kartPos + Vector3.up * 14f, kartPos + hoverOffset, 0.6f);
        // 2) lift
        Vector3 lifted = kartPos + Vector3.up * 5f;
        yield return Carry(h, kart.transform, kartPos, lifted, kart.transform.rotation, kart.transform.rotation, hoverOffset, 0.6f);
        // 3) carry over the drop point
        Vector3 above = dropPoint + Vector3.up * 4f;
        yield return Carry(h, kart.transform, lifted, above, kart.transform.rotation, dropRot, hoverOffset, 0.9f);
        // 4) lower and release
        yield return Carry(h, kart.transform, above, dropPoint, dropRot, dropRot, hoverOffset, 0.4f);

        body.isKinematic = false;
        kart.Teleport(dropPoint, dropRot);
        bool racing = RaceManager.Instance == null || RaceManager.Instance.Phase == RacePhase.Racing
                      || RaceManager.Instance.Phase == RacePhase.Results;
        kart.SetControlsLocked(!racing);
        var items = kart.GetComponent<KartItems>();
        if (items != null) items.InvulnerableUntil = Time.time + 1.5f;
        StartCoroutine(Blink(kart, 1.5f));
        w.offTrack = 0f;
        w.rescuing = false;

        // 5) fly away
        yield return Move(h, h.position, h.position + Vector3.up * 16f, 0.6f);
        Destroy(helper);
    }

    IEnumerator Move(Transform t, Vector3 from, Vector3 to, float duration)
    {
        for (float e = 0f; e < duration; e += Time.deltaTime)
        {
            t.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, e / duration));
            yield return null;
        }
        t.position = to;
    }

    IEnumerator Carry(Transform helper, Transform kart, Vector3 from, Vector3 to, Quaternion fromRot, Quaternion toRot, Vector3 offset, float duration)
    {
        for (float e = 0f; e < duration; e += Time.deltaTime)
        {
            float k = Mathf.SmoothStep(0f, 1f, e / duration);
            Vector3 p = Vector3.Lerp(from, to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 1.5f;
            kart.SetPositionAndRotation(p, Quaternion.Slerp(fromRot, toRot, k));
            helper.position = p + offset;
            yield return null;
        }
        kart.SetPositionAndRotation(to, toRot);
        helper.position = to + offset;
    }

    IEnumerator Blink(KartController kart, float duration)
    {
        Transform visual = kart.Visual;
        for (float e = 0f; e < duration; e += 0.1f)
        {
            if (visual != null) visual.gameObject.SetActive(((int)(e * 10f)) % 2 == 0);
            yield return new WaitForSeconds(0.1f);
        }
        if (visual != null) visual.gameObject.SetActive(true);
    }

    /// <summary>Original helper: a mini character on a round hover platform with a spinning rotor and a tow hook.</summary>
    GameObject BuildHelper()
    {
        var root = new GameObject("PitPal");
        root.AddComponent<RescueHelperSpin>();

        GameObject platform = Primitive(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 0f, 0f), new Vector3(2.2f, 0.18f, 2.2f), platformMaterial);
        platform.name = "Platform";
        Primitive(PrimitiveType.Sphere, root.transform, new Vector3(0f, -0.25f, 0f), new Vector3(1.2f, 0.5f, 1.2f), platformMaterial);
        GameObject mast = Primitive(PrimitiveType.Cylinder, root.transform, new Vector3(-0.8f, 0.9f, 0f), new Vector3(0.12f, 0.9f, 0.12f), rotorMaterial);
        mast.name = "Mast";
        GameObject rotor = Primitive(PrimitiveType.Cube, root.transform, new Vector3(-0.8f, 1.85f, 0f), new Vector3(2.6f, 0.06f, 0.3f), rotorMaterial);
        rotor.name = "Rotor";
        Primitive(PrimitiveType.Cylinder, root.transform, new Vector3(0f, -1.3f, 0f), new Vector3(0.06f, 1.1f, 0.06f), rotorMaterial); // tow cable

        if (helperModel != null)
        {
            GameObject pilot = Instantiate(helperModel, root.transform, false);
            pilot.name = "Pilot";
            pilot.transform.localPosition = new Vector3(0.3f, 0.1f, 0f);
            pilot.transform.localScale = Vector3.one * helperScale;
            foreach (var c in pilot.GetComponentsInChildren<Collider>()) Destroy(c);
            if (helperClip != null) pilot.AddComponent<DriverRig>().Init(helperClip, helperAvatar);
        }
        ToonStyle.Instance?.Apply(root);
        return root;
    }

    static GameObject Primitive(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
        return go;
    }
}
