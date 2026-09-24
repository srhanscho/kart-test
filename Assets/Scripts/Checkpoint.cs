using UnityEngine;

/// <summary>Trigger volume on the track. Reports to the LapTracker of the kart passing through.</summary>
[RequireComponent(typeof(Collider))]
public class Checkpoint : MonoBehaviour
{
    [SerializeField] int index;
    [SerializeField] bool isFinishLine;

    public int Index => index;
    public bool IsFinishLine => isFinishLine;

    public void Configure(int checkpointIndex, bool finishLine)
    {
        index = checkpointIndex;
        isFinishLine = finishLine;
    }

    void OnTriggerEnter(Collider other)
    {
        var tracker = other.GetComponentInParent<LapTracker>();
        if (tracker != null) tracker.OnCheckpointPassed(this);
    }
}
