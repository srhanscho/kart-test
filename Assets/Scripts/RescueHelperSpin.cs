using UnityEngine;

/// <summary>Spins the rescue helper's rotor and gives the platform a gentle wobble.</summary>
public class RescueHelperSpin : MonoBehaviour
{
    Transform rotor;

    void Start() => rotor = transform.Find("Rotor");

    void Update()
    {
        if (rotor != null) rotor.Rotate(0f, 900f * Time.deltaTime, 0f, Space.Self);
        transform.rotation = Quaternion.Euler(Mathf.Sin(Time.time * 3f) * 4f, Time.time * 20f, Mathf.Cos(Time.time * 2.5f) * 4f);
    }
}
