using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// UI Toolkit minimap: the centerline drawn as a thick outlined path with a dot per kart
/// (player colours, grey CPUs). Call SetKarts each frame; drawing uses Painter2D.
/// </summary>
public class MinimapElement : VisualElement
{
    Vector3[] track = new Vector3[0];
    Rect bounds;
    readonly List<(Vector3 pos, Color color, bool human)> karts = new List<(Vector3, Color, bool)>();

    public int DotCount => karts.Count;

    public MinimapElement()
    {
        generateVisualContent += Draw;
        pickingMode = PickingMode.Ignore;
    }

    public void SetTrack(Vector3[] waypoints)
    {
        track = waypoints ?? new Vector3[0];
        if (track.Length == 0) return;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in track)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
        }
        bounds = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        MarkDirtyRepaint();
    }

    public void SetKarts(IEnumerable<(Vector3 pos, Color color, bool human)> list)
    {
        karts.Clear();
        karts.AddRange(list);
        MarkDirtyRepaint();
    }

    Vector2 Map(Vector3 p, Rect area)
    {
        float scale = Mathf.Min(area.width / Mathf.Max(1f, bounds.width), area.height / Mathf.Max(1f, bounds.height));
        Vector2 size = new Vector2(bounds.width, bounds.height) * scale;
        Vector2 offset = area.position + (area.size - size) * 0.5f;
        // World +Z is up on the map.
        return offset + new Vector2((p.x - bounds.xMin) * scale, (bounds.yMax - p.z) * scale);
    }

    void Draw(MeshGenerationContext mgc)
    {
        if (track.Length < 2) return;
        Rect area = contentRect;
        area = new Rect(area.x + 12f, area.y + 12f, area.width - 24f, area.height - 24f);
        var painter = mgc.painter2D;

        painter.lineJoin = LineJoin.Round;
        painter.lineCap = LineCap.Round;
        // Soft dark outline under a white track line; no background panel.
        foreach (var (width, color) in new[] { (11f, new Color(0f, 0f, 0f, 0.45f)), (5f, new Color(1f, 1f, 1f, 0.9f)) })
        {
            painter.strokeColor = color;
            painter.lineWidth = width;
            painter.BeginPath();
            painter.MoveTo(Map(track[0], area));
            for (int i = 1; i < track.Length; i++) painter.LineTo(Map(track[i], area));
            painter.ClosePath();
            painter.Stroke();
        }

        // Finish line tick.
        Vector2 f0 = Map(track[0], area);
        painter.fillColor = new Color(1f, 0.85f, 0.2f);
        painter.BeginPath();
        painter.Arc(f0, 5f, 0f, 360f);
        painter.Fill();

        // CPUs first, humans on top.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var k in karts)
            {
                if (k.human != (pass == 1)) continue;
                Vector2 c = Map(k.pos, area);
                float r = k.human ? 8f : 4.5f;
                // Humans: player colour with a white ring; CPUs: small grey dots.
                painter.fillColor = new Color(0f, 0f, 0f, 0.6f);
                painter.BeginPath();
                painter.Arc(c, r + (k.human ? 5f : 1.5f), 0f, 360f);
                painter.Fill();
                if (k.human)
                {
                    painter.fillColor = Color.white;
                    painter.BeginPath();
                    painter.Arc(c, r + 3f, 0f, 360f);
                    painter.Fill();
                }
                painter.fillColor = k.color;
                painter.BeginPath();
                painter.Arc(c, r, 0f, 360f);
                painter.Fill();
            }
        }
    }
}
