using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Diagnostics only (added by the stall test at runtime): remembers the contacts of the last
/// few physics steps - collider, normal, impulse - so sudden stops can be explained.
/// </summary>
public class ContactProbe : MonoBehaviour
{
    public struct Hit
    {
        public float time;
        public string collider;
        public Vector3 normal;
        public float impulse;
        public bool enter;
    }

    readonly Queue<Hit> recent = new Queue<Hit>();
    readonly ContactPoint[] buffer = new ContactPoint[16];

    public IEnumerable<Hit> Recent(float window)
    {
        foreach (var h in recent) if (Time.fixedTime - h.time <= window) yield return h;
    }

    void OnCollisionEnter(Collision c) => Record(c, true);
    void OnCollisionStay(Collision c) => Record(c, false);

    void Record(Collision c, bool enter)
    {
        int n = c.GetContacts(buffer);
        for (int i = 0; i < n; i++)
            recent.Enqueue(new Hit { time = Time.fixedTime, collider = c.collider.name, normal = buffer[i].normal, impulse = c.impulse.magnitude, enter = enter });
        while (recent.Count > 200 || (recent.Count > 0 && Time.fixedTime - recent.Peek().time > 1f)) recent.Dequeue();
    }
}
