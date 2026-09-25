using UnityEngine;

/// <summary>
/// One selectable circuit (all tracks live in the race scene; only the active one is enabled).
/// Holds the gameplay references the RaceManager needs and the environment (sky, sun, ambient,
/// fog) that is applied to the global RenderSettings when the track becomes active.
/// </summary>
public class TrackDefinition : MonoBehaviour
{
    [Header("Info")]
    public string displayName = "Track";
    public string twist = "";
    [Range(1, 3)] public int difficulty = 1;
    public int laps = 3;
    public Color accent = Color.white;

    [Header("Gameplay")]
    public RaceTrack track;
    public Transform[] gridSlots;
    public int checkpointCount;
    public Vector3 overviewPosition;
    public Quaternion overviewRotation = Quaternion.identity;
    public Bounds bounds;

    [Header("Environment")]
    public Material skybox;
    public Vector3 sunEuler = new Vector3(50f, -35f, 0f);
    public Color sunColor = Color.white;
    public float sunIntensity = 1f;
    [Range(0f, 1f)] public float sunShadowStrength = 0.8f;
    public Color ambientSky = Color.gray, ambientEquator = Color.gray, ambientGround = Color.gray;
    public bool fog;
    public Color fogColor = Color.gray;
    public float fogDensity = 0.003f;
    [Tooltip("Human karts switch their headlight spot on (night tracks).")]
    public bool headlights;

    /// <summary>Rendered at lobby start (RaceManager.RenderTrackThumbnails).</summary>
    public Texture2D Thumbnail { get; set; }
    public float Length => track != null ? track.Length : 0f;

    /// <summary>Applies sky, sun and ambient/fog of this track to the global render settings.</summary>
    public void ApplyEnvironment(Light sun)
    {
        if (skybox != null) RenderSettings.skybox = skybox;
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(sunEuler);
            sun.color = sunColor;
            sun.intensity = sunIntensity;
            sun.shadowStrength = sunShadowStrength;
            RenderSettings.sun = sun;
        }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = ambientSky;
        RenderSettings.ambientEquatorColor = ambientEquator;
        RenderSettings.ambientGroundColor = ambientGround;
        RenderSettings.fog = fog;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogDensity = fogDensity;
    }
}
