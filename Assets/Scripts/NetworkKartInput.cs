using UnityEngine;

/// <summary>
/// Input fed by a phone controller. Values are written on the main thread by
/// RaceManager; if the phone goes silent the kart gets zero input.
/// </summary>
public class NetworkKartInput : IKartInput
{
    public float Timeout = 0.6f;

    float steer, throttle;
    bool drift, item;
    float itemLatchUntil;
    float lastUpdate = float.NegativeInfinity;

    bool Alive => Time.time - lastUpdate <= Timeout;

    public float Steer => Alive ? steer : 0f;
    public float Throttle => Alive ? throttle : 0f;
    public bool Drift => Alive && drift;
    public bool Hop => Alive && drift; // one HOP/DRIFT button: tap hops, hold drifts
    // A quick tap may arrive as press+release in the same frame; latch presses briefly so KartItems sees the edge.
    public bool UseItem => Alive && (item || Time.time < itemLatchUntil);

    public void Apply(float newSteer, float newThrottle, bool newDrift, bool newItem = false)
    {
        if (newItem && !item) itemLatchUntil = Time.time + 0.15f;
        item = newItem;
        steer = Mathf.Clamp(newSteer, -1f, 1f);
        throttle = Mathf.Clamp(newThrottle, -1f, 1f);
        drift = newDrift;
        lastUpdate = Time.time;
    }

    public void Clear()
    {
        steer = throttle = 0f;
        drift = item = false;
        itemLatchUntil = 0f;
        lastUpdate = float.NegativeInfinity;
    }
}
