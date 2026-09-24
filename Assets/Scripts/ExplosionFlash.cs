using UnityEngine;

/// <summary>Expanding, fading sphere used as a cheap explosion.</summary>
public class ExplosionFlash : MonoBehaviour
{
    const float Duration = 0.4f;
    const float MaxSize = 5f;
    float t;
    Material mat;
    Color baseColor;

    void Start()
    {
        mat = GetComponent<Renderer>().material;
        baseColor = mat.color;
        transform.localScale = Vector3.one * 0.5f;
    }

    void Update()
    {
        t += Time.deltaTime;
        float k = t / Duration;
        transform.localScale = Vector3.one * Mathf.Lerp(0.5f, MaxSize, Mathf.Sqrt(k));
        mat.color = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * (1f - k));
        if (k >= 1f)
        {
            Destroy(mat);
            Destroy(gameObject);
        }
    }
}
