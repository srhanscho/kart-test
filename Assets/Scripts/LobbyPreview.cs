using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Off-track "showroom": one turntable + camera per player slot rendering to
/// a RenderTexture for the lobby UI, plus PNG thumbnails of every character
/// for the phone picker. Created at runtime by RaceManager.
/// </summary>
public class LobbyPreview : MonoBehaviour
{
    static readonly Vector3 StageOrigin = new Vector3(20000f, 0f, 20000f);
    const float SlotSpacing = 200f;

    class Slot
    {
        public Transform pivot;
        public Camera camera;
        public RenderTexture texture;
        public int character = -1;
        public GameObject model;
    }

    CharacterRoster roster;
    Slot[] slots;
    Slot thumbSlot;
    bool active;

    public static bool CanRender => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

    public void Init(CharacterRoster characterRoster, int slotCount)
    {
        roster = characterRoster;
        slots = new Slot[slotCount];
        for (int i = 0; i < slotCount; i++) slots[i] = CreateSlot(i, 512, 384);
        thumbSlot = CreateSlot(slotCount, 320, 240);
        thumbSlot.camera.enabled = false;
        SetActive(true);
    }

    Slot CreateSlot(int index, int width, int height)
    {
        var s = new Slot();
        s.pivot = new GameObject($"PreviewPivot_{index}").transform;
        s.pivot.SetParent(transform, false);
        s.pivot.position = StageOrigin + Vector3.right * SlotSpacing * index;

        var camGo = new GameObject($"PreviewCamera_{index}");
        camGo.transform.SetParent(transform, false);
        camGo.transform.position = s.pivot.position + new Vector3(0f, 2.5f, 7.4f);
        camGo.transform.LookAt(s.pivot.position + Vector3.up * 1.0f);

        // Key light so the showroom is bright regardless of the sun direction.
        var lightGo = new GameObject($"PreviewLight_{index}");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.position = s.pivot.position + new Vector3(2.5f, 4f, 5f);
        var key = lightGo.AddComponent<Light>();
        key.type = LightType.Point;
        key.range = 14f;
        key.intensity = 2.2f;
        s.camera = camGo.AddComponent<Camera>();
        s.camera.fieldOfView = 30f;
        s.camera.nearClipPlane = 0.3f;
        s.camera.farClipPlane = 40f;
        s.camera.clearFlags = CameraClearFlags.SolidColor;
        s.camera.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
        if (CanRender)
        {
            s.texture = new RenderTexture(width, height, 24) { antiAliasing = 4, name = $"Preview_{index}" };
            s.camera.targetTexture = s.texture;
        }
        else s.camera.enabled = false;
        return s;
    }

    public Texture GetTexture(int slot) => slots != null && slot >= 0 && slot < slots.Length ? slots[slot].texture : null;

    public void SetActive(bool on)
    {
        active = on;
        if (slots == null) return;
        foreach (var s in slots) s.camera.enabled = on && CanRender && s.character >= 0;
    }

    public void SetCharacter(int slot, int character)
    {
        Slot s = slots[slot];
        if (s.character == character) return;
        s.character = character;
        if (s.model != null) Destroy(s.model);
        s.model = character >= 0 ? CharacterRoster.Spawn(roster[character], s.pivot) : null;
        s.camera.enabled = active && CanRender && character >= 0;
    }

    void Update()
    {
        if (!active || slots == null) return;
        foreach (var s in slots) s.pivot.Rotate(0f, 45f * Time.deltaTime, 0f, Space.World);
    }

    /// <summary>Renders every character once and hands back PNG bytes (skipped without a GPU).</summary>
    public IEnumerator RenderThumbnails(Action<int, byte[]> onThumbnail)
    {
        if (!CanRender) yield break;
        yield return null;
        var readback = new Texture2D(thumbSlot.texture.width, thumbSlot.texture.height, TextureFormat.RGB24, false);
        thumbSlot.pivot.rotation = Quaternion.Euler(0f, -35f, 0f);
        for (int i = 0; i < roster.Count; i++)
        {
            GameObject model = CharacterRoster.Spawn(roster[i], thumbSlot.pivot);
            thumbSlot.camera.Render();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = thumbSlot.texture;
            readback.ReadPixels(new Rect(0, 0, readback.width, readback.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            DestroyImmediate(model);
            onThumbnail(i, readback.EncodeToPNG());
            yield return null;
        }
        Destroy(readback);
    }

    void OnDestroy()
    {
        if (slots == null) return;
        foreach (var s in slots) if (s.texture != null) s.texture.Release();
        if (thumbSlot?.texture != null) thumbSlot.texture.Release();
    }
}
