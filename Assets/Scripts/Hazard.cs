using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Track obstacle: touching it spins the kart out. Static (cones) or sliding back and
/// forth across the road. CPU drivers read All to steer around hazards.
/// </summary>
public class Hazard : MonoBehaviour
{
    [SerializeField] Vector3 slideAxis = Vector3.right;
    [SerializeField] float slideDistance;   // 0 = static
    [SerializeField] float slidePeriod = 3f;
    [SerializeField] float radius = 1.5f;

    static readonly List<Hazard> all = new List<Hazard>();
    Vector3 origin;
    Rigidbody body;

    public static IReadOnlyList<Hazard> All => all;
    public float Radius => radius;
    public int Hits { get; set; }
    public bool Moving => slideDistance > 0f;
    /// <summary>Centre of the area the hazard can occupy (its rest position).</summary>
    public Vector3 SweepCentre => Application.isPlaying ? origin : transform.position;
    /// <summary>Radius of the whole area it can occupy (includes the slide).</summary>
    public float SweepRadius => radius + slideDistance;

    public void Configure(float hazardRadius, Vector3 axis, float distance, float period)
    {
        radius = hazardRadius;
        slideAxis = axis;
        slideDistance = distance;
        slidePeriod = period;
    }

    void Awake()
    {
        origin = transform.position;
        body = GetComponent<Rigidbody>();
    }

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    void FixedUpdate()
    {
        if (!Moving) return;
        float offset = Mathf.Sin(Time.time * Mathf.PI * 2f / slidePeriod) * slideDistance;
        Vector3 target = origin + slideAxis.normalized * offset;
        if (body != null) body.MovePosition(target);
        else transform.position = target;
    }
}
