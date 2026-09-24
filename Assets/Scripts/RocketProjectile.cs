using UnityEngine;

/// <summary>
/// Ground-hugging rocket: follows the track centerline and homes on the
/// nearest kart ahead within a cone. Explodes on karts, walls or timeout.
/// </summary>
public class RocketProjectile : MonoBehaviour
{
    const float Speed = 42f;
    const float Lifetime = 7f;
    const float HomingRange = 70f;
    const float HomingCone = 40f;
    const float TurnRate = 160f;
    const float Radius = 0.45f;
    const float HoverHeight = 0.8f;

    KartItems owner;
    ItemManager manager;
    KartController target;
    float born, nextRetarget;

    public KartController Owner => owner != null ? owner.Kart : null;
    public KartController Target => target;

    public void Init(KartItems ownerItems, ItemManager itemManager)
    {
        owner = ownerItems;
        manager = itemManager;
        born = Time.time;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (Time.time - born > Lifetime)
        {
            Detonate();
            return;
        }

        if (Time.time >= nextRetarget)
        {
            nextRetarget = Time.time + 0.2f;
            target = FindTarget();
        }

        Vector3 pos = transform.position;
        Vector3 aim;
        if (target != null) aim = target.transform.position + Vector3.up * 0.6f;
        else if (manager != null && manager.Track != null)
        {
            RaceTrack track = manager.Track;
            aim = track.PointAt(track.Project(pos) + 15f);
        }
        else aim = pos + transform.forward;

        Vector3 desired = aim - pos;
        desired.y = 0f;
        if (desired.sqrMagnitude > 0.01f)
        {
            Quaternion want = Quaternion.LookRotation(desired);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, TurnRate * dt);
        }

        Vector3 dir = transform.forward;
        float step = Speed * dt;
        RaycastHit[] hits = Physics.SphereCastAll(pos, Radius, dir, step + 0.3f, ~0, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        KartItems hitKart = null;
        bool hitWall = false;
        foreach (var h in hits)
        {
            if (h.distance >= nearest) continue;
            var items = h.collider.GetComponentInParent<KartItems>();
            if (items != null)
            {
                if (items == owner) continue;
                nearest = h.distance;
                hitKart = items;
                hitWall = false;
                continue;
            }
            if (h.collider.GetComponent<TrackSurface>() != null || h.collider.name == "Ground") continue;
            nearest = h.distance;
            hitKart = null;
            hitWall = true;
        }
        if (hitKart != null)
        {
            hitKart.Hit(ItemType.Rocket);
            Detonate();
            return;
        }
        if (hitWall)
        {
            Detonate();
            return;
        }

        pos += dir * step;
        // Hug the ground.
        if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out RaycastHit ground, 8f, ~0, QueryTriggerInteraction.Ignore)
            && ground.collider.GetComponentInParent<KartController>() == null)
            pos.y = Mathf.Lerp(pos.y, ground.point.y + HoverHeight, 0.5f);
        transform.position = pos;
    }

    KartController FindTarget()
    {
        var rm = RaceManager.Instance;
        if (rm == null) return null;
        KartController best = null;
        float bestDist = HomingRange;
        foreach (var k in rm.AllKarts)
        {
            if (k == Owner) continue;
            Vector3 to = k.transform.position - transform.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d > bestDist || d < 0.5f) continue;
            if (Vector3.Angle(transform.forward, to) > HomingCone) continue;
            best = k;
            bestDist = d;
        }
        return best;
    }

    void Detonate()
    {
        GameAudio.Play(Sfx.Explosion, transform.position, 1f);
        manager?.Explode(transform.position);
        manager?.RocketGone(this);
        Destroy(gameObject);
    }
}
