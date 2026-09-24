using UnityEngine;

/// <summary>
/// CPU driver: follows the track centerline with look-ahead steering, slows
/// for corners, keeps a personal lane offset and backs out when stuck.
/// Call Tick() once per frame from the main thread.
/// </summary>
public class AiKartInput : IKartInput
{
    readonly KartController kart;
    readonly RaceTrack track;
    readonly float laneOffset;
    readonly float wobblePhase;
    readonly float cornerCaution; // 0 = brave, 1 = careful
    readonly KartItems items;
    readonly float turboDelay;
    readonly System.Random rng;

    float stuckTimer, reverseTimer;
    float itemHeldTime, pressTimer, lastTurn, hopTimer;

    public float Steer { get; private set; }
    public float Throttle { get; private set; }
    public bool Drift => false;
    public bool Hop => hopTimer > 0f;
    public bool UseItem => pressTimer > 0f;
    public float Progress { get; private set; }
    public bool NeedsRespawn { get; private set; }

    public AiKartInput(KartController kart, RaceTrack track, System.Random rng, KartItems items = null)
    {
        this.kart = kart;
        this.track = track;
        this.items = items;
        this.rng = rng;
        turboDelay = Mathf.Lerp(0.5f, 3f, (float)rng.NextDouble());
        float maxLane = track.RoadWidth * 0.5f - 2.2f;
        laneOffset = Mathf.Lerp(-maxLane, maxLane, (float)rng.NextDouble());
        wobblePhase = (float)rng.NextDouble() * 10f;
        cornerCaution = (float)rng.NextDouble();
    }

    public void Tick(float time, float dt, bool active)
    {
        Transform t = kart.transform;
        float speed = kart.ForwardSpeed;
        float s = track.Project(t.position);
        Progress = s;

        // Steering toward a look-ahead point with a slowly wobbling lane, tighter in hairpins.
        float maxLane = track.RoadWidth * 0.5f - 2.2f;
        float look = 7f + Mathf.Abs(speed) * 0.45f;
        float laneScale = Mathf.Clamp01((track.RadiusAt(s + look) - 8f) / 20f);
        float lane = Mathf.Clamp(laneOffset + Mathf.Sin(time * 0.35f + wobblePhase) * 1.2f, -maxLane, maxLane) * laneScale;
        lane = AvoidHazards(s, lane, maxLane);
        Vector3 target = track.PointAt(s + look) + track.RightAt(s + look) * lane;
        Vector3 local = t.InverseTransformPoint(target);
        float angle = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.5f)) * Mathf.Rad2Deg;
        float steer = Mathf.Clamp(angle / 18f, -1f, 1f);

        // Corner speed from centerline curvature: the fastest speed from which the kart can still
        // brake down to each upcoming corner's limit (limit ~ radius * grip factor).
        float ahead = 12f + Mathf.Abs(speed) * 0.8f;
        lastTurn = Vector3.Angle(track.TangentAt(s + 4f), track.TangentAt(s + ahead));
        float grip = Mathf.Lerp(1.3f, 1.1f, cornerCaution);
        float desired = kart.MaxSpeed;
        for (float d = 2f; d <= 14f + Mathf.Abs(speed) * 1.3f; d += 3f)
        {
            float cornerSpeed = Mathf.Max(7f, track.RadiusAt(s + d) * grip);
            desired = Mathf.Min(desired, Mathf.Sqrt(cornerSpeed * cornerSpeed + 2f * 14f * Mathf.Max(0f, d - 3f)));
        }
        float error = desired - speed;
        float throttle = error > 1f ? 1f : error > -1.5f ? 0.4f : -0.5f;

        // Stuck: back out with opposite steering, respawn if it lasts.
        if (active)
        {
            if (reverseTimer > 0f)
            {
                reverseTimer -= dt;
                throttle = -1f;
                steer = -steer;
            }
            else if (Mathf.Abs(speed) < 1.5f)
            {
                stuckTimer += dt;
                if (stuckTimer > 1.5f) reverseTimer = 1.2f;
            }
            else stuckTimer = Mathf.Max(0f, stuckTimer - dt);
            NeedsRespawn = stuckTimer > 6f;
        }
        else
        {
            stuckTimer = reverseTimer = 0f;
            NeedsRespawn = false;
        }

        Steer = steer;
        Throttle = throttle;
        TickItems(dt, active);
    }

    /// <summary>Item tactics: rockets at karts ahead, bananas for karts behind, turbo on straights,
    /// shield when a rocket is coming. Presses are held briefly so KartItems sees the edge.</summary>
    void TickItems(float dt, bool active)
    {
        if (pressTimer > 0f) pressTimer -= dt;
        if (items == null || !active || items.IsRolling || items.Held == ItemType.None)
        {
            itemHeldTime = 0f;
            return;
        }
        itemHeldTime += dt;
        if (itemHeldTime < 0.4f || pressTimer > 0f) return;

        bool press = false;
        Transform t = kart.transform;
        switch (items.Held)
        {
            case ItemType.Rocket:
                press = OtherKart((to, d) => d > 4f && d < 55f && Vector3.Angle(t.forward, to) < 20f) || itemHeldTime > 12f;
                break;
            case ItemType.Banana:
                press = OtherKart((to, d) => d < 14f && Vector3.Dot(t.forward, to) < 0f) || itemHeldTime > 10f;
                break;
            case ItemType.Turbo:
                press = itemHeldTime > turboDelay && lastTurn < 8f && kart.ForwardSpeed > 12f;
                break;
            case ItemType.Shield:
                var manager = ItemManager.Instance;
                press = (manager != null && manager.RocketIncoming(kart, 30f)) || itemHeldTime > 8f + (float)rng.NextDouble() * 4f;
                break;
        }
        if (press) pressTimer = 0.25f;
    }

    bool OtherKart(System.Func<Vector3, float, bool> predicate)
    {
        var rm = RaceManager.Instance;
        if (rm == null) return false;
        foreach (var other in rm.AllKarts)
        {
            if (other == kart) continue;
            Vector3 to = other.transform.position - kart.transform.position;
            to.y = 0f;
            if (predicate(to, to.magnitude)) return true;
        }
        return false;
    }

    /// <summary>
    /// Pick a lane that clears the nearest group of obstacles ahead (hazards within 8 m of each
    /// other along the track count as one group); moving hazards block their whole sweep.
    /// </summary>
    float AvoidHazards(float s, float lane, float maxLane)
    {
        float nearest = float.MaxValue;
        var group = new System.Collections.Generic.List<(float d, float lat, float clear)>();
        foreach (var h in Hazard.All)
        {
            Vector3 hp = h.SweepCentre;
            float hs = track.Project(hp);
            float d = Mathf.Repeat(hs - s + 6f, track.Length) - 6f; // slightly behind counts until passed
            if (d > 45f || d < -4f) continue;
            group.Add((d, Vector3.Dot(hp - track.PointAt(hs), track.RightAt(hs)), h.SweepRadius + 1.9f));
            nearest = Mathf.Min(nearest, d);
        }
        if (group.Count == 0) return lane;

        float limit = maxLane + 1f;
        float best = lane, bestCost = float.MaxValue;
        for (float candidate = -limit; candidate <= limit + 0.01f; candidate += 0.4f)
        {
            float cost = Mathf.Abs(candidate - lane);
            foreach (var g in group)
            {
                if (g.d > nearest + 8f) continue; // later groups are handled once these are passed
                float gap = Mathf.Abs(candidate - g.lat);
                if (gap < g.clear) cost += 100f + (g.clear - gap) * 10f;
            }
            if (cost < bestCost)
            {
                bestCost = cost;
                best = candidate;
            }
        }
        return best;
    }

    public void ClearStuck()
    {
        stuckTimer = reverseTimer = 0f;
        NeedsRespawn = false;
    }
}
