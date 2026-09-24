using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cosmetic kart effects: wheels spin with speed and front wheels steer (when the model has
/// separate wheel meshes), drift sparks under the rear (blue, then orange once a mini-turbo is
/// charged) and a burst of dust on landing. Added to each character model at spawn.
/// </summary>
public class KartVisualFx : MonoBehaviour
{
    class Wheel
    {
        public Transform t;
        public Quaternion baseRot;
        public bool front;
        public float radius;
    }

    readonly List<Wheel> wheels = new List<Wheel>();
    KartController kart;
    ParticleSystem sparks;
    float spin;

    public static Material ParticleMaterial { get; set; }

    void Start()
    {
        kart = GetComponentInParent<KartController>();
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            string n = r.name.ToLowerInvariant();
            if (!n.StartsWith("wheel")) continue;
            wheels.Add(new Wheel
            {
                t = r.transform,
                baseRot = r.transform.localRotation,
                front = n.StartsWith("wheel-f"),
                radius = Mathf.Max(0.1f, r.bounds.extents.y)
            });
        }
        if (kart != null) sparks = CreateSparks();
    }

    ParticleSystem CreateSparks()
    {
        var go = new GameObject("DriftSparks");
        go.transform.SetParent(kart.transform, false);
        go.transform.localPosition = new Vector3(0f, 0.15f, -1.4f);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.duration = 1f;
        main.loop = true;
        main.startLifetime = 0.35f;
        main.startSpeed = 4f;
        main.startSize = 0.18f;
        main.gravityModifier = 1.5f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 200;
        var emission = ps.emission;
        emission.rateOverTime = 0f;
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 35f;
        shape.radius = 0.6f;
        shape.rotation = new Vector3(-150f, 0f, 0f);
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        if (ParticleMaterial != null) renderer.sharedMaterial = ParticleMaterial;
        ps.Play();
        return ps;
    }

    void Update()
    {
        if (kart == null) return;
        float dt = Time.deltaTime;
        float speed = kart.ForwardSpeed;

        if (wheels.Count > 0) spin = (spin + speed / (2f * Mathf.PI * wheels[0].radius) * 360f * dt) % 360f;
        foreach (var w in wheels)
        {
            float steerYaw = w.front ? kart.SteerInput * 25f : 0f;
            w.t.localRotation = Quaternion.Euler(0f, steerYaw, 0f) * w.baseRot * Quaternion.Euler(spin, 0f, 0f);
        }

        if (sparks != null)
        {
            var emission = sparks.emission;
            emission.rateOverTime = kart.IsDrifting && kart.IsGrounded ? 70f : 0f;
            var main = sparks.main;
            main.startColor = kart.MiniTurboReady ? new Color(1f, 0.55f, 0.1f) : new Color(0.4f, 0.75f, 1f);
        }
    }
}
