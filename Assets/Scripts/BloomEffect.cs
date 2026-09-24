using UnityEngine;

/// <summary>
/// Camera bloom (built-in pipeline image effect): bright-pass with soft knee, dual-filter
/// downsample/upsample chain, then combine with a light vignette and saturation/contrast
/// grading. Works per camera, so split-screen viewports each get their own pass; with 3-4
/// viewports it uses fewer iterations.
/// </summary>
[RequireComponent(typeof(Camera))]
public class BloomEffect : MonoBehaviour
{
    [SerializeField] Shader shader;
    [Range(0f, 4f)] public float threshold = 1.35f;  // lit surfaces stay below; emissive lamps, items, sparks go above
    [Range(0f, 1f)] public float softKnee = 0.5f;
    [Range(0f, 3f)] public float intensity = 0.6f;
    [Range(0f, 1f)] public float vignette = 0.28f;
    [Range(0f, 2f)] public float saturation = 1.12f;
    [Range(0f, 2f)] public float contrast = 1.06f;

    Material material;
    Camera cam;
    readonly RenderTexture[] chain = new RenderTexture[8];

    public void Init(Shader bloomShader) => shader = bloomShader;

    void OnEnable() => cam = GetComponent<Camera>();

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }

    /// <summary>
    /// Final pass. Graphics.Blit always covers the whole target, which would paint this camera
    /// over the other split-screen viewports, so for partial-rect cameras the quad is drawn
    /// with the viewport restricted to camera.pixelRect.
    /// </summary>
    void Combine(RenderTexture source, RenderTexture destination)
    {
        Rect r = cam != null ? cam.rect : new Rect(0f, 0f, 1f, 1f);
        bool full = r.x <= 0.001f && r.y <= 0.001f && r.width >= 0.999f && r.height >= 0.999f;
        if (full)
        {
            material.SetVector("_ViewRect", new Vector4(0f, 0f, 1f, 1f));
            Graphics.Blit(source, destination, material, 3);
            return;
        }
        Graphics.SetRenderTarget(destination);
        GL.PushMatrix();
        GL.LoadOrtho();
        GL.Viewport(cam.pixelRect);
        material.SetTexture("_MainTex", source);
        material.SetPass(3);
        // The built-in pipeline hands us a full-size source with this camera's image inside its
        // viewport; sample only that region (or all of it if the source is viewport-sized).
        Rect px = cam.pixelRect;
        bool viewportSized = Mathf.Abs(source.width - px.width) < 1.5f && Mathf.Abs(source.height - px.height) < 1.5f;
        Vector2 uv0 = viewportSized ? Vector2.zero : new Vector2(px.x / source.width, px.y / source.height);
        Vector2 uv1 = viewportSized ? Vector2.one : new Vector2(px.xMax / source.width, px.yMax / source.height);
        material.SetVector("_ViewRect", new Vector4(uv0.x, uv0.y, uv1.x - uv0.x, uv1.y - uv0.y)); // vignette per viewport
        GL.Begin(GL.QUADS);
        GL.TexCoord2(uv0.x, uv0.y); GL.Vertex3(0f, 0f, 0f);
        GL.TexCoord2(uv1.x, uv0.y); GL.Vertex3(1f, 0f, 0f);
        GL.TexCoord2(uv1.x, uv1.y); GL.Vertex3(1f, 1f, 0f);
        GL.TexCoord2(uv0.x, uv1.y); GL.Vertex3(0f, 1f, 0f);
        GL.End();
        GL.PopMatrix();
    }

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (cam == null) cam = GetComponent<Camera>();
        if (shader == null || !shader.isSupported)
        {
            enabled = false; // unsupported GPU: turn the effect off rather than blit over other viewports
            Graphics.Blit(source, destination);
            return;
        }
        if (material == null) material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

        // Smaller viewports (3-4 player split) -> fewer, cheaper iterations.
        bool small = cam != null && cam.rect.width < 0.75f && cam.rect.height < 0.75f;
        int iterations = small ? 3 : 5;
        float knee = threshold * softKnee + 1e-5f;
        material.SetVector("_Filter", new Vector4(threshold, threshold - knee, 2f * knee, 0.25f / knee));
        material.SetFloat("_Intensity", intensity);
        material.SetVector("_Grade", new Vector4(vignette, saturation, contrast, 0f));

        int width = source.width / 2, height = source.height / 2;
        var format = source.format;
        RenderTexture current = chain[0] = RenderTexture.GetTemporary(width, height, 0, format);
        Graphics.Blit(source, current, material, 0); // prefilter
        int levels = 1;
        for (; levels < iterations; levels++)
        {
            width /= 2;
            height /= 2;
            if (width < 4 || height < 4) break;
            chain[levels] = RenderTexture.GetTemporary(width, height, 0, format);
            Graphics.Blit(current, chain[levels], material, 1); // downsample
            current = chain[levels];
        }
        for (int i = levels - 2; i >= 0; i--)
        {
            Graphics.Blit(current, chain[i], material, 2); // upsample (additive)
            current = chain[i];
        }
        material.SetTexture("_BloomTex", current);
        Combine(source, destination);
        for (int i = 0; i < levels; i++)
        {
            RenderTexture.ReleaseTemporary(chain[i]);
            chain[i] = null;
        }
    }
}
