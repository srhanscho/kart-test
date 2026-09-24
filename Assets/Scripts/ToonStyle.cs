using System.Collections.Generic;
using UnityEngine;

public enum VisualMode { Plain, Toon, ToonBloom }

/// <summary>
/// Toon look: converts materials to Kart/Toon (surfaces) or Kart/ToonOutline (objects),
/// keeping their colour, texture and emission; manages bloom on cameras and the F2 toggle
/// (plain / toon / toon + bloom). Static scene objects are converted by the scene builder;
/// anything spawned at runtime goes through Apply().
/// </summary>
public class ToonStyle : MonoBehaviour
{
    public const string ToonShaderName = "Kart/Toon";
    public const string OutlineShaderName = "Kart/ToonOutline";
    const string OffKeyword = "KART_TOON_OFF";

    [SerializeField] Shader toonShader;
    [SerializeField] Shader outlineShader;
    [SerializeField] Shader bloomShader;
    [SerializeField] VisualMode mode = VisualMode.ToonBloom;

    readonly Dictionary<(Material, bool), Material> cache = new Dictionary<(Material, bool), Material>();

    public static ToonStyle Instance { get; private set; }
    public VisualMode Mode => mode;
    public Shader BloomShader => bloomShader;

    public void Configure(Shader toon, Shader outline, Shader bloom)
    {
        toonShader = toon;
        outlineShader = outline;
        bloomShader = bloom;
    }

    void Awake()
    {
        Instance = this;
        SetMode(mode);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Shader.DisableKeyword(OffKeyword);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F2)) SetMode((VisualMode)(((int)mode + 1) % 3));
    }

    public void SetMode(VisualMode newMode)
    {
        mode = newMode;
        if (mode == VisualMode.Plain) Shader.EnableKeyword(OffKeyword);
        else Shader.DisableKeyword(OffKeyword);
        foreach (var cam in FindObjectsByType<Camera>(FindObjectsInactive.Include)) EnsureBloom(cam);
    }

    /// <summary>Adds/updates the bloom effect on a camera according to the current mode.</summary>
    public void EnsureBloom(Camera cam)
    {
        var bloom = cam.GetComponent<BloomEffect>();
        if (bloom == null)
        {
            if (bloomShader == null) return;
            bloom = cam.gameObject.AddComponent<BloomEffect>();
            bloom.Init(bloomShader);
        }
        cam.allowHDR = true;
        bloom.enabled = mode == VisualMode.ToonBloom;
    }

    /// <summary>Converts every renderer under root (outlined unless it is a big surface).</summary>
    public void Apply(GameObject root, bool outline = true)
    {
        if (root == null || toonShader == null) return;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                var converted = Convert(mats[i], outline, toonShader, outlineShader, cache);
                if (converted != mats[i]) { mats[i] = converted; changed = true; }
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    /// <summary>Opaque Standard/legacy-diffuse materials become toon; anything else is left alone.</summary>
    public static Material Convert(Material src, bool outline, Shader toon, Shader outlined, Dictionary<(Material, bool), Material> cache)
    {
        if (src == null || src.shader == null) return src;
        string name = src.shader.name;
        if (name == ToonShaderName || name == OutlineShaderName) return src;
        bool opaqueLit = name == "Standard" || name == "Legacy Shaders/Diffuse" || name == "Standard (Specular setup)";
        if (!opaqueLit || src.renderQueue > 2500) return src;
        if (cache != null && cache.TryGetValue((src, outline), out var hit)) return hit;

        var m = new Material(outline ? outlined : toon) { name = src.name + (outline ? " (Toon+Outline)" : " (Toon)") };
        m.SetColor("_Color", src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white);
        if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null)
        {
            m.SetTexture("_MainTex", src.GetTexture("_MainTex"));
            m.SetTextureScale("_MainTex", src.GetTextureScale("_MainTex"));
            m.SetTextureOffset("_MainTex", src.GetTextureOffset("_MainTex"));
        }
        if (src.IsKeywordEnabled("_EMISSION") && src.HasProperty("_EmissionColor"))
        {
            m.SetColor("_EmissionColor", src.GetColor("_EmissionColor"));
            if (src.GetTexture("_EmissionMap") != null) m.SetTexture("_EmissionMap", src.GetTexture("_EmissionMap"));
        }
        else m.SetColor("_EmissionColor", Color.black);
        if (cache != null) cache[(src, outline)] = m;
        return m;
    }
}
