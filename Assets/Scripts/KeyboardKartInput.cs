using UnityEngine;

/// <summary>
/// Keyboard driver: WASD / arrows to drive, Space: tap = hop, hold while steering = drift; E / Left Shift for items.
/// The project uses the legacy Input Manager (activeInputHandler = 0, no
/// com.unity.inputsystem package), so this reads UnityEngine.Input.
/// </summary>
public class KeyboardKartInput : MonoBehaviour, IKartInput
{
    public float Steer => Axis(KeyCode.A, KeyCode.LeftArrow, KeyCode.D, KeyCode.RightArrow);
    public float Throttle => Axis(KeyCode.S, KeyCode.DownArrow, KeyCode.W, KeyCode.UpArrow);
    public bool Drift => Input.GetKey(KeyCode.Space);
    public bool Hop => Input.GetKey(KeyCode.Space);
    public bool UseItem => Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.LeftShift);

    static float Axis(KeyCode negA, KeyCode negB, KeyCode posA, KeyCode posB)
    {
        float value = 0f;
        if (Input.GetKey(negA) || Input.GetKey(negB)) value -= 1f;
        if (Input.GetKey(posA) || Input.GetKey(posB)) value += 1f;
        return value;
    }
}
