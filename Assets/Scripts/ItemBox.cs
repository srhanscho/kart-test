using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Spinning, bobbing item box: starts the kart's roulette, respawns after a few seconds.</summary>
[RequireComponent(typeof(Collider))]
public class ItemBox : MonoBehaviour
{
    [SerializeField] Transform visual;
    [SerializeField] float respawnTime = 3f;

    static readonly List<ItemBox> All = new List<ItemBox>();
    Collider trigger;
    float hiddenUntil = -1f;
    float phase;
    bool popping;

    public static int Pickups { get; private set; }
    public bool Available => hiddenUntil < 0f;

    public void Configure(Transform visualRoot) => visual = visualRoot;

    void Awake()
    {
        trigger = GetComponent<Collider>();
        phase = Random.value * 10f;
    }

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    public static void ResetAll()
    {
        foreach (var box in All) box.Show();
    }

    void Update()
    {
        if (hiddenUntil > 0f && Time.time >= hiddenUntil && !popping) Show();
        if (visual != null)
        {
            visual.localRotation = Quaternion.Euler(20f, (Time.time * 90f + phase * 36f) % 360f, 10f);
            visual.localPosition = new Vector3(0f, Mathf.Sin(Time.time * 2.5f + phase) * 0.2f, 0f);
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (!Available) return;
        var items = other.GetComponentInParent<KartItems>();
        if (items == null) return;
        items.TryStartRoulette(); // the box breaks even if the slot is full
        Pickups++;
        if (items.IsAi) GameAudio.Play(Sfx.ItemBox, transform.position, 0.6f);
        else GameAudio.Play(Sfx.ItemBox, null, 0.8f);
        trigger.enabled = false;
        hiddenUntil = Time.time + respawnTime;
        StartCoroutine(Pop());
    }

    /// <summary>Quick "pop": the box swells and vanishes, then stays hidden until respawn.</summary>
    IEnumerator Pop()
    {
        popping = true;
        for (float t = 0f; t < 0.15f && visual != null; t += Time.deltaTime)
        {
            visual.localScale = Vector3.one * (1f + t / 0.15f * 0.6f);
            yield return null;
        }
        popping = false;
        if (visual != null) visual.localScale = Vector3.one;
        Hide();
    }

    void Hide()
    {
        hiddenUntil = Time.time + respawnTime;
        trigger.enabled = false;
        if (visual != null) visual.gameObject.SetActive(false);
    }

    void Show()
    {
        StopAllCoroutines();
        popping = false;
        if (visual != null) visual.localScale = Vector3.one;
        hiddenUntil = -1f;
        trigger.enabled = true;
        if (visual != null) visual.gameObject.SetActive(true);
    }
}
