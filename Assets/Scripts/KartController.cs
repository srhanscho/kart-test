using System;
using UnityEngine;

/// <summary>
/// Arcade kart movement (not a vehicle simulation). Physics only handles
/// collisions and gravity; forward speed, lateral grip and yaw are driven
/// directly from IKartInput every FixedUpdate.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class KartController : MonoBehaviour
{
    [Header("Speed (m/s)")]
    [SerializeField] float maxSpeed = 26f;
    [SerializeField] float acceleration = 16f;
    [SerializeField] float reverseMaxSpeed = 9f;
    [SerializeField] float reverseAcceleration = 12f;
    [SerializeField] float brakeDeceleration = 38f;
    [SerializeField] float coastDeceleration = 6f;
    [SerializeField, Range(0.1f, 1f)] float offroadSpeedMultiplier = 0.5f;
    [SerializeField] float offroadDeceleration = 25f;

    [Header("Steering")]
    [Tooltip("Yaw rate in deg/s at full steer.")]
    [SerializeField] float steerRate = 105f;
    [Tooltip("Speed at which steering reaches full authority.")]
    [SerializeField] float fullSteerSpeed = 7f;
    [Tooltip("Steering multiplier at max speed (keeps high speed stable).")]
    [SerializeField, Range(0.2f, 1f)] float highSpeedSteerFactor = 0.7f;

    [Header("Grip / Drift")]
    [Tooltip("How fast sideways velocity is killed (1/s). Higher = more grip.")]
    [SerializeField] float lateralGrip = 14f;
    [SerializeField] float driftLateralGrip = 2.5f;
    [SerializeField] float driftSteerMultiplier = 1.45f;
    [SerializeField] float driftMinSpeed = 8f;
    [SerializeField] float handbrakeDeceleration = 12f;

    [Header("Mini-turbo (hold a drift, release for a boost)")]
    [SerializeField] float miniTurboDriftTime = 1.0f;
    [SerializeField] float miniTurboDuration = 0.9f;
    [SerializeField] float miniTurboSpeedMultiplier = 1.22f;
    [SerializeField] float miniTurboKick = 3f;

    [Header("Hop (tap drift button) and crashes")]
    [SerializeField] float hopSpeed = 8f;
    [SerializeField] float hopCooldown = 0.35f;
    [Tooltip("Impact speed along the contact normal (m/s) that counts as a crash.")]
    [SerializeField] float crashImpactSpeed = 12f;
    [SerializeField] float crashCooldown = 1.5f;

    [Header("Ground")]
    [SerializeField] float groundRayLength = 1.0f;
    [SerializeField] LayerMask groundLayers = ~0;
    [SerializeField] float extraGravity = 20f;

    [Header("Visual")]
    [SerializeField] Transform visual;
    [SerializeField] float maxBodyRoll = 6f;
    [SerializeField] float maxBodyPitch = 2.5f;
    [SerializeField] float driftVisualYaw = 12f;
    [SerializeField] float visualSharpness = 8f;

    const float GroundedTolerance = 0.35f;

    Rigidbody rb;
    IKartInput input;
    bool controlsLocked;
    float steerInput, throttleInput;
    float driftTimer, boostTimer, boostMultiplier = 1f, spinTimer, spinAngle;
    float hopTimer, crashTimer, airTime, landSquash;
    bool lastHop;
    Vector3 lastSetVelocity, wallNormal;
    float wallContactTime = -1f, lossTime = -10f;
    readonly ContactPoint[] contacts = new ContactPoint[16];
    Vector3 groundNormal = Vector3.up, visualNormal = Vector3.up;
    Vector3 baseVisualScale = Vector3.one;

    // Character stats (1 = baseline feel) and runtime speed scaling (rubber-banding).
    float statSpeed = 1f, statAcceleration = 1f, statHandling = 1f;

    public float ForwardSpeed { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool IsDrifting { get; private set; }
    public bool IsOnRoad { get; private set; }
    public bool IsBoosting => boostTimer > 0f;
    public bool IsSpinningOut => spinTimer > 0f;
    public float SteerInput => steerInput;
    public Vector3 GroundNormal => groundNormal;
    public float AirTime => airTime;
    public string LastImpact { get; private set; } = "";
    public string LastContact { get; private set; } = "";
    public int WallCrashes { get; private set; }
    public int KartCrashes { get; private set; }
    public int HazardCrashes { get; private set; }
    public Rigidbody Body => rb;

    /// <summary>Hard wall/kart impact or hazard contact. KartItems turns it into a (shield-able) spin-out.</summary>
    public event Action<KartController, bool> Crashed; // bool: hazard (always spins) vs impact
    public event Action Hopped;
    public bool MiniTurboReady => IsDrifting && driftTimer >= miniTurboDriftTime;
    public float MaxSpeed => maxSpeed * statSpeed * SpeedMultiplier;
    public Transform Visual => visual;
    public IKartInput CurrentInput => input;
    public bool ControlsLocked => controlsLocked;

    /// <summary>Extra top-speed scale applied at runtime (e.g. CPU rubber-banding).</summary>
    public float SpeedMultiplier { get; set; } = 1f;

    /// <summary>Swap the input source (keyboard, phone, AI).</summary>
    public void SetInput(IKartInput newInput) => input = newInput;

    public void SetControlsLocked(bool locked) => controlsLocked = locked;

    public void Configure(Transform visualRoot) => visual = visualRoot;

    /// <summary>Character stat multipliers; keep them close to 1.</summary>
    public void SetStats(float speed, float accel, float handling)
    {
        statSpeed = speed;
        statAcceleration = accel;
        statHandling = handling;
    }

    /// <summary>Effect hook (items later): temporary top-speed boost.</summary>
    public void ApplyBoost(float duration, float speedMultiplier)
    {
        boostTimer = Mathf.Max(boostTimer, duration);
        boostMultiplier = speedMultiplier;
    }

    /// <summary>Effect hook (items later): lose control and spin for a moment.</summary>
    public void SpinOut(float duration)
    {
        spinTimer = Mathf.Max(spinTimer, duration);
        boostTimer = 0f;
        driftTimer = 0f;
    }

    /// <summary>Effect hook: scale the current velocity (e.g. 0.4 when hit).</summary>
    public void Slow(float factor)
    {
        if (rb != null && !rb.isKinematic)
        {
            rb.linearVelocity *= factor;
            lastSetVelocity = rb.linearVelocity;
        }
    }

    /// <summary>Effect hook: small vertical hop.</summary>
    public void Hop(float upSpeed)
    {
        if (rb == null || rb.isKinematic) return;
        Vector3 v = rb.linearVelocity;
        rb.linearVelocity = new Vector3(v.x, Mathf.Max(v.y, upSpeed), v.z);
    }

    /// <summary>Moves the kart and clears all motion state.</summary>
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        transform.SetPositionAndRotation(position, rotation);
        rb.position = position;
        rb.rotation = rotation;
        if (!rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        ForwardSpeed = 0f;
        driftTimer = boostTimer = spinTimer = spinAngle = hopTimer = airTime = landSquash = 0f;
        lastSetVelocity = Vector3.zero;
        groundNormal = visualNormal = Vector3.up;
        IsDrifting = false;
        if (visual != null) visual.localRotation = Quaternion.identity;
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (visual != null) baseVisualScale = visual.localScale;
        if (input == null) input = GetComponent<IKartInput>();

        // Frictionless body: all grip is handled in code, walls just deflect.
        var body = GetComponent<Collider>();
        if (body != null)
        {
            body.sharedMaterial = new PhysicsMaterial("KartBody")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
        }
    }

    void FixedUpdate()
    {
        if (rb.isKinematic) return; // being carried (rescue)
        float dt = Time.fixedDeltaTime;
        float steer = 0f, throttle = 0f;
        bool drift = false;
        bool hop = false;
        if (spinTimer > 0f) spinTimer -= dt;
        if (hopTimer > 0f) hopTimer -= dt;
        if (crashTimer > 0f) crashTimer -= dt;
        if (input != null && !controlsLocked && spinTimer <= 0f)
        {
            steer = Mathf.Clamp(input.Steer, -1f, 1f);
            throttle = Mathf.Clamp(input.Throttle, -1f, 1f);
            drift = input.Drift;
            hop = input.Hop;
        }
        bool hopPressed = hop && !lastHop;
        lastHop = hop;
        steerInput = steer;
        throttleInput = throttle;

        Vector3 up = Vector3.up;
        Vector3 origin = rb.position + up * 0.5f;
        // Long ray keeps contact information over bumps; "grounded" means the wheels are close to it.
        bool hasGround = Physics.Raycast(origin, -up, out RaycastHit hit, 0.5f + groundRayLength,
            groundLayers, QueryTriggerInteraction.Ignore);
        IsGrounded = hasGround && hit.distance < 0.5f + GroundedTolerance;

        // On slopes, drive along the surface: project the kart's axes onto the ground plane.
        // On flat ground the normal is up and this is exactly the original flat behaviour.
        groundNormal = IsGrounded && hit.normal.y > 0.8f ? hit.normal : Vector3.up; // steeper faces are walls, not ramps
        Vector3 v = rb.linearVelocity;
        DetectWallImpact(v);
        Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, groundNormal).normalized;
        Vector3 right = Vector3.ProjectOnPlane(transform.right, groundNormal).normalized;
        up = groundNormal;
        float fs = Vector3.Dot(v, fwd);
        float ls = Vector3.Dot(v, right);
        float vs = Vector3.Dot(v, up);

        if (IsGrounded)
        {
            if (airTime > 0.25f) landSquash = Mathf.Clamp01(airTime * 0.8f); // squash on landing
            airTime = 0f;
        }
        else airTime += dt;

        // Hop: a small jump, also the natural way into a drift (hold the button while steering).
        if (hopPressed && IsGrounded && hopTimer <= 0f)
        {
            vs = Mathf.Max(vs, hopSpeed);
            hopTimer = hopCooldown;
            Hopped?.Invoke();
        }

        if (boostTimer > 0f) boostTimer -= dt;

        if (IsGrounded)
        {
            var surface = hit.collider.GetComponent<TrackSurface>();
            IsOnRoad = surface != null && surface.IsDrivable(hit.triangleIndex);

            // Drift state and mini-turbo charge.
            IsDrifting = drift && Mathf.Abs(steer) > 0.1f && fs > driftMinSpeed;
            if (IsDrifting) driftTimer += dt;
            else
            {
                if (driftTimer >= miniTurboDriftTime && fs > driftMinSpeed)
                {
                    boostTimer = miniTurboDuration;
                    boostMultiplier = miniTurboSpeedMultiplier;
                    fs += miniTurboKick;
                }
                driftTimer = 0f;
            }

            float boost = IsBoosting ? boostMultiplier : 1f;
            float top = maxSpeed * statSpeed * SpeedMultiplier * boost * (IsOnRoad ? 1f : offroadSpeedMultiplier);
            float accel = acceleration * statAcceleration * (IsBoosting ? 2f : 1f);

            if (throttle > 0.01f || IsBoosting)
            {
                float t = IsBoosting ? 1f : throttle;
                if (fs < -0.1f) fs = Mathf.MoveTowards(fs, 0f, brakeDeceleration * dt);
                else
                {
                    float target = top * t;
                    float rate = fs > target ? (IsOnRoad ? coastDeceleration : offroadDeceleration) : accel;
                    fs = Mathf.MoveTowards(fs, target, rate * dt);
                }
            }
            else if (throttle < -0.01f)
            {
                if (fs > 0.5f) fs = Mathf.MoveTowards(fs, 0f, brakeDeceleration * -throttle * dt);
                else fs = Mathf.MoveTowards(fs, reverseMaxSpeed * throttle, reverseAcceleration * dt);
            }
            else
            {
                float rate = fs > top ? offroadDeceleration : coastDeceleration;
                fs = Mathf.MoveTowards(fs, 0f, rate * dt);
            }

            if (drift && !IsDrifting) fs = Mathf.MoveTowards(fs, 0f, handbrakeDeceleration * dt);

            float grip = IsDrifting ? driftLateralGrip : lateralGrip;
            ls *= Mathf.Exp(-grip * dt);

            rb.linearVelocity = fwd * fs + right * ls + up * vs;
            rb.AddForce(-up * extraGravity, ForceMode.Acceleration);

            float speed01 = Mathf.Clamp01(Mathf.Abs(fs) / maxSpeed);
            float authority = Mathf.Clamp01(Mathf.Abs(fs) / fullSteerSpeed)
                              * Mathf.Lerp(1f, highSpeedSteerFactor, speed01)
                              * (IsDrifting ? driftSteerMultiplier : 1f)
                              * statHandling;
            float direction = fs >= 0f ? 1f : -1f;
            float yawRate = steer * steerRate * authority * direction;
            rb.angularVelocity = new Vector3(0f, yawRate * Mathf.Deg2Rad, 0f);
        }
        else
        {
            IsOnRoad = false;
            // Keep a drift alive through a short hop so hop-then-drift works.
            IsDrifting = IsDrifting && drift && airTime < 0.6f;
            if (!IsDrifting) driftTimer = 0f;
            if (vs != Vector3.Dot(v, up)) rb.linearVelocity = v + up * (vs - Vector3.Dot(v, up));
            rb.AddForce(-Vector3.up * extraGravity, ForceMode.Acceleration);
            // Small air steering so a hop can be used to line up a drift.
            float airYaw = steer * steerRate * 0.35f * Mathf.Clamp01(Mathf.Abs(fs) / fullSteerSpeed);
            rb.angularVelocity = new Vector3(0f, airYaw * Mathf.Deg2Rad, 0f);
        }

        ForwardSpeed = fs;
        lastSetVelocity = rb.linearVelocity;
    }

    /// <summary>
    /// Hard wall hit: during the last physics step the kart touched a near-vertical surface and was
    /// driving into it (velocity we set, measured along the wall normal) faster than the crash speed.
    /// Track floors and raised edges are one mesh collider, so this cannot rely on OnCollisionEnter.
    /// </summary>
    void DetectWallImpact(Vector3 current)
    {
        Vector3 before = new Vector3(lastSetVelocity.x, 0f, lastSetVelocity.z);
        Vector3 now = new Vector3(current.x, 0f, current.z);
        lastSetVelocity = current;
        if (crashTimer > 0f || before.magnitude < crashImpactSpeed) return;
        // Speed the physics step took away, and only when touching a near-vertical surface.
        float impact = before.magnitude - Vector3.Dot(now, before.normalized);
        if (impact < crashImpactSpeed * 0.75f) return;
        LastImpact = $"wall stop: lost {impact:F1} m/s";
        lossTime = Time.fixedTime;
        TryWallCrash();
    }

    /// <summary>A big speed loss and a wall contact within 0.1 s of each other (callback order varies).</summary>
    void TryWallCrash()
    {
        if (crashTimer > 0f || Mathf.Abs(lossTime - wallContactTime) > 0.1f) return;
        lossTime = -10f;
        crashTimer = crashCooldown;
        WallCrashes++;
        LastImpact += " -> crash";
        if (Crashed != null) Crashed(this, false);
        else SpinOut(0.8f);
    }

    void OnCollisionStay(Collision collision) => RecordWallContact(collision);

    void RecordWallContact(Collision collision)
    {
        if (collision.collider.GetComponentInParent<KartController>() != null) return;
        int n = collision.GetContacts(contacts);
        for (int i = 0; i < n; i++)
        {
            Vector3 normal = contacts[i].normal;
            LastContact = $"{collision.collider.name} n={normal:F2} t={Time.fixedTime:F2}";
            if (Mathf.Abs(normal.y) > 0.5f) continue; // floors and ramps
            wallNormal = new Vector3(normal.x, 0f, normal.z).normalized;
            wallContactTime = Time.fixedTime;
            TryWallCrash();
            return;
        }
    }

    void OnCollisionEnter(Collision collision)
    {
        RecordWallContact(collision);
        if (crashTimer > 0f || collision.contactCount == 0) return;
        var hazardComp = collision.collider.GetComponentInParent<Hazard>();
        bool hazard = hazardComp != null;
        Vector3 normal = collision.GetContact(0).normal;
        float impact = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, normal));
        LastImpact = $"{collision.collider.name} n={normal:F2} impact={impact:F1}";
        bool otherKart = collision.collider.GetComponentInParent<KartController>() != null;
        if (!hazard && !otherKart) return; // walls are detected from the velocity change (DetectWallImpact)
        if (otherKart && impact < crashImpactSpeed * 2f) return; // bumping in the pack is fine; only big rear-ends spin
        crashTimer = crashCooldown;
        if (hazard)
        {
            HazardCrashes++;
            hazardComp.Hits++;
        }
        else KartCrashes++;
        if (Crashed != null) Crashed(this, hazard);
        else SpinOut(0.9f);
    }

    void Update()
    {
        if (visual == null) return;
        float speed01 = Mathf.Clamp01(Mathf.Abs(ForwardSpeed) / maxSpeed);
        float roll = steerInput * maxBodyRoll * speed01;
        float pitch = -throttleInput * maxBodyPitch * speed01;
        float yaw = IsDrifting ? steerInput * driftVisualYaw : 0f;
        if (spinTimer > 0f) spinAngle = (spinAngle + 720f * Time.deltaTime) % 360f;
        else spinAngle = Mathf.MoveTowardsAngle(spinAngle, 0f, 720f * Time.deltaTime);
        yaw += spinAngle;
        // Align the body with slopes (visual only; the physics body stays upright).
        visualNormal = Vector3.Slerp(visualNormal, groundNormal, 1f - Mathf.Exp(-10f * Time.deltaTime));
        Quaternion slope = Quaternion.FromToRotation(Vector3.up, transform.InverseTransformDirection(visualNormal));
        Quaternion target = slope * Quaternion.Euler(pitch, yaw, roll);
        float t = 1f - Mathf.Exp(-visualSharpness * Time.deltaTime);
        visual.localRotation = Quaternion.Slerp(visual.localRotation, target, t);

        // Squash and stretch after landing.
        landSquash = Mathf.MoveTowards(landSquash, 0f, Time.deltaTime * 3f);
        float squash = Mathf.Sin(landSquash * Mathf.PI) * 0.25f;
        visual.localScale = Vector3.Scale(baseVisualScale, new Vector3(1f + squash * 0.5f, 1f - squash, 1f + squash * 0.5f));
    }
}
