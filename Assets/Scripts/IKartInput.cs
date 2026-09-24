/// <summary>
/// Abstract driver input. The kart only reads from this, so keyboard, AI or
/// network (phone) controllers can be swapped without touching KartController.
/// </summary>
public interface IKartInput
{
    /// <summary>-1 (full left) .. 1 (full right).</summary>
    float Steer { get; }

    /// <summary>-1 (brake / reverse) .. 1 (full throttle).</summary>
    float Throttle { get; }

    /// <summary>Drift / handbrake button held.</summary>
    bool Drift { get; }

    /// <summary>Item button held. KartItems fires on the press edge (false -> true), once per press.</summary>
    bool UseItem { get; }

    /// <summary>Hop button held (the controller hops on the press edge). Keyboard/phone share it with Drift:
    /// tap = hop, hold while steering = drift.</summary>
    bool Hop { get; }
}
