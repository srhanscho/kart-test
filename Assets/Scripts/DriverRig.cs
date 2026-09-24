using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Plays the character's "drive" clip through a tiny PlayableGraph (no
/// AnimatorController asset needed) and layers procedural motion on top:
/// lean into turns, speed bob, head turn while drifting, dizzy wobble when
/// spinning out and arms-up celebration after finishing.
/// </summary>
public class DriverRig : MonoBehaviour
{
    [SerializeField] AnimationClip clip;
    [SerializeField] Avatar avatar;
    [SerializeField] Quaternion baseRotation = Quaternion.identity;
    [SerializeField] Vector3 basePosition;
    [SerializeField] float maxLean = 12f;
    [SerializeField] float leanSharpness = 6f;

    /// <summary>Celebrate regardless of race state (podium).</summary>
    public bool ForceCheer { get; set; }

    PlayableGraph graph;
    KartController kart;
    LapTracker lap;
    Transform head, armLeft, armRight;
    float lean, headYaw, cheer;
    // Rest pose per bone (refreshed whenever the animation writes the bone) and our last write.
    Quaternion headRest, headLast, leftRest, leftLast, rightRest, rightLast;

    public void Init(AnimationClip driveClip, Avatar rigAvatar)
    {
        clip = driveClip;
        avatar = rigAvatar;
        baseRotation = transform.localRotation;
        basePosition = transform.localPosition;
        Play();
    }

    void OnEnable() => Play();

    void OnDisable()
    {
        if (graph.IsValid()) graph.Destroy();
    }

    void Play()
    {
        if (clip == null || graph.IsValid()) return;
        if (!Application.isPlaying)
        {
            clip.SampleAnimation(gameObject, 0f); // edit mode (scene builder): just pose it
            return;
        }
        var animator = GetComponent<Animator>();
        if (animator == null) animator = gameObject.AddComponent<Animator>();
        if (animator.avatar == null && avatar != null) animator.avatar = avatar;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        AnimationPlayableUtilities.PlayClip(animator, clip, out graph);
        graph.Evaluate(0f); // pose immediately (e.g. for thumbnail renders)
    }

    void LateUpdate()
    {
        if (kart == null)
        {
            kart = GetComponentInParent<KartController>();
            if (kart != null) lap = kart.GetComponent<LapTracker>();
            head = FindBone("head");
            armLeft = FindBone("arm-left");
            armRight = FindBone("arm-right");
        }
        float dt = Time.deltaTime;
        float steer = kart != null ? kart.SteerInput : 0f;
        float speed = kart != null ? Mathf.Abs(kart.ForwardSpeed) : 0f;
        bool spinning = kart != null && kart.IsSpinningOut;
        bool finished = ForceCheer || (lap != null && lap.Finished);

        // Lean into turns and bob with speed (applied in the kart's visual space, +Z = forward).
        lean = Mathf.Lerp(lean, -steer * maxLean, 1f - Mathf.Exp(-leanSharpness * dt));
        float bob = Mathf.Sin(Time.time * (6f + speed * 0.4f)) * 0.03f * Mathf.Clamp01(speed / 10f);
        transform.localRotation = Quaternion.Euler(0f, steer * 6f, lean) * baseRotation;
        transform.localPosition = basePosition + Vector3.up * bob;

        // Head: look into the drift, wobble when dizzy.
        if (head != null)
        {
            float wantYaw = kart != null && kart.IsDrifting ? steer * 30f : steer * 10f;
            headYaw = Mathf.Lerp(headYaw, wantYaw, 1f - Mathf.Exp(-8f * dt));
            Quaternion extra = Quaternion.Euler(0f, headYaw, 0f);
            if (spinning) extra *= Quaternion.Euler(Mathf.Sin(Time.time * 18f) * 20f, Mathf.Sin(Time.time * 11f) * 35f, Mathf.Cos(Time.time * 14f) * 20f);
            Offset(head, ref headRest, ref headLast, extra);
        }

        // Arms up after the finish line (also flail while spinning out).
        cheer = Mathf.MoveTowards(cheer, finished || spinning ? 1f : 0f, dt * 3f);
        if (cheer > 0f)
        {
            float wave = spinning ? Mathf.Sin(Time.time * 20f) * 40f : Mathf.Sin(Time.time * 8f) * 15f;
            if (armLeft != null) Offset(armLeft, ref leftRest, ref leftLast, Quaternion.Euler(0f, 0f, (150f + wave) * cheer));
            if (armRight != null) Offset(armRight, ref rightRest, ref rightLast, Quaternion.Euler(0f, 0f, (-150f - wave) * cheer));
            if (finished && !spinning) transform.localPosition += Vector3.up * Mathf.Abs(Mathf.Sin(Time.time * 6f)) * 0.15f * cheer;
        }
    }

    /// <summary>Applies an offset on top of the bone's rest pose without accumulating across frames,
    /// whether or not the playing clip animates that bone.</summary>
    static void Offset(Transform bone, ref Quaternion rest, ref Quaternion last, Quaternion extra)
    {
        if (bone.localRotation != last) rest = bone.localRotation; // animation (or first frame) wrote it
        last = rest * extra;
        bone.localRotation = last;
    }

    Transform FindBone(string boneName)
    {
        foreach (var t in GetComponentsInChildren<Transform>())
            if (t.name == boneName) return t;
        return null;
    }
}
