using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Results podium: three coloured steps far off the track with the top-3 characters
/// (in their karts) cheering, filmed by a slowly orbiting camera. Built at runtime.
/// </summary>
public class PodiumStage : MonoBehaviour
{
    static readonly Vector3 Origin = new Vector3(-20000f, 0f, -20000f);

    Camera cam;
    readonly List<GameObject> spawned = new List<GameObject>();
    readonly List<Transform> performers = new List<Transform>();
    float shownAt;

    public Camera Camera => cam;
    public bool Showing => cam != null && cam.enabled;
    public int PerformerCount => performers.Count;

    public void Init()
    {
        var root = new GameObject("PodiumSet").transform;
        root.SetParent(transform, false);
        root.position = Origin;

        var ground = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ground.name = "Stage";
        ground.transform.SetParent(root, false);
        ground.transform.localScale = new Vector3(26f, 0.3f, 26f);
        ground.GetComponent<Renderer>().material.color = new Color(0.22f, 0.25f, 0.33f);
        Destroy(ground.GetComponent<Collider>());

        // Steps: 2nd (left), 1st (middle), 3rd (right).
        Step(root, new Vector3(-4.2f, 0f, 0f), 1.6f, new Color(0.78f, 0.8f, 0.86f));
        Step(root, new Vector3(0f, 0f, 0f), 2.6f, new Color(1f, 0.8f, 0.2f));
        Step(root, new Vector3(4.2f, 0f, 0f), 1.0f, new Color(0.85f, 0.52f, 0.27f));

        var light = new GameObject("PodiumLight").AddComponent<Light>();
        light.transform.SetParent(root, false);
        light.transform.localPosition = new Vector3(0f, 9f, 8f);
        light.type = LightType.Point;
        light.range = 30f;
        light.intensity = 2.5f;

        var spot = new GameObject("PodiumSpot").AddComponent<Light>();
        spot.transform.SetParent(root, false);
        spot.transform.localPosition = new Vector3(0f, 14f, -6f);
        spot.transform.LookAt(root.position + Vector3.up * 2f);
        spot.type = LightType.Spot;
        spot.spotAngle = 55f;
        spot.range = 40f;
        spot.intensity = 1.6f;
        spot.color = new Color(1f, 0.93f, 0.8f);
        ToonStyle.Instance?.Apply(root.gameObject, false);

        cam = new GameObject("PodiumCamera").AddComponent<Camera>();
        cam.transform.SetParent(transform, false);
        cam.depth = 50f;
        cam.fieldOfView = 40f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.09f, 0.1f, 0.16f);
        cam.farClipPlane = 80f;
        cam.enabled = false;
        cam.allowHDR = true;
        ToonStyle.Instance?.EnsureBloom(cam);
    }

    void Step(Transform root, Vector3 pos, float height, Color color)
    {
        var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
        step.name = "Step";
        step.transform.SetParent(root, false);
        step.transform.localPosition = pos + Vector3.up * height * 0.5f;
        step.transform.localScale = new Vector3(4f, height, 3.4f);
        step.GetComponent<Renderer>().material.color = color;
        Destroy(step.GetComponent<Collider>());
    }

    /// <summary>Shows the top three (character indices, first place first).</summary>
    public void Show(CharacterRoster roster, IList<int> topCharacters)
    {
        Clear();
        Vector3[] spots = { new Vector3(0f, 2.6f, 0f), new Vector3(-4.2f, 1.6f, 0f), new Vector3(4.2f, 1.0f, 0f) };
        for (int i = 0; i < topCharacters.Count && i < 3; i++)
        {
            var holder = new GameObject($"Podium_{i + 1}").transform;
            holder.position = Origin + spots[i] + Vector3.up * 0.3f;
            holder.rotation = Quaternion.Euler(0f, 180f, 0f); // face the camera
            GameObject model = CharacterRoster.Spawn(roster[topCharacters[i]], holder);
            foreach (var rig in model.GetComponentsInChildren<DriverRig>()) rig.ForceCheer = true;
            spawned.Add(holder.gameObject);
            performers.Add(holder);
        }
        cam.enabled = true;
        shownAt = Time.time;
    }

    public void Hide()
    {
        Clear();
        if (cam != null) cam.enabled = false;
    }

    void Clear()
    {
        foreach (var go in spawned) if (go != null) Destroy(go);
        spawned.Clear();
        performers.Clear();
    }

    void LateUpdate()
    {
        if (!Showing) return;
        float t = Time.time - shownAt;
        // Slow orbit in front of the podium.
        float angle = Mathf.Sin(t * 0.25f) * 25f;
        // Aim right of centre so the podium sits in the left half (the results table covers the right).
        Vector3 podium = Origin + new Vector3(0f, 2.2f, 0f);
        cam.transform.position = podium + Quaternion.Euler(0f, angle, 0f) * new Vector3(0f, 4f, -21f);
        cam.transform.LookAt(podium + cam.transform.right * 4.5f);
        // Hop and spin a little.
        for (int i = 0; i < performers.Count; i++)
        {
            Transform p = performers[i];
            if (p == null) continue;
            float hop = Mathf.Abs(Mathf.Sin(t * 5f + i)) * (i == 0 ? 0.6f : 0.35f);
            Vector3 basePos = p.position;
            basePos.y = Origin.y + (i == 0 ? 2.6f : i == 1 ? 1.6f : 1.0f) + 0.3f + hop;
            p.position = basePos;
            p.rotation = Quaternion.Euler(0f, 180f + Mathf.Sin(t * 2f + i) * 20f, 0f);
        }
    }
}
