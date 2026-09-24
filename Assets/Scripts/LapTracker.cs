using System;
using UnityEngine;

/// <summary>
/// Per-kart race progress. Karts start behind the finish line: the first
/// crossing starts lap 1, then every checkpoint must be passed in order
/// before the finish line counts the next lap.
/// </summary>
public class LapTracker : MonoBehaviour
{
    [SerializeField] int totalLaps = 3;
    [SerializeField] int checkpointCount = 4;

    int currentLap;
    int nextCheckpoint;
    bool racing;
    float raceStartTime, lapStartTime;

    public int TotalLaps => totalLaps;
    public int CheckpointCount => checkpointCount;
    /// <summary>0 before the first line crossing, then 1..TotalLaps.</summary>
    public int CurrentLap => currentLap;
    public int DisplayLap => Mathf.Clamp(currentLap, 1, totalLaps);
    public int NextCheckpoint => nextCheckpoint;
    public bool Finished => FinishTime >= 0f;
    public float FinishTime { get; private set; } = -1f;
    public float BestLap { get; private set; } = -1f;
    public float RaceTime => racing ? Time.time - raceStartTime : 0f;

    public event Action<LapTracker> RaceFinished;
    /// <summary>Raised when the finish line starts a new lap (argument: the new lap number).</summary>
    public event Action<LapTracker, int> LapStarted;

    public void Configure(int checkpoints, int laps)
    {
        checkpointCount = checkpoints;
        totalLaps = laps;
    }

    /// <summary>Back to the grid state (behind the line, not racing).</summary>
    public void ResetForRace()
    {
        currentLap = 0;
        nextCheckpoint = checkpointCount; // armed: next finish crossing starts lap 1
        racing = false;
        FinishTime = -1f;
        BestLap = -1f;
    }

    public void BeginRace(float startTime)
    {
        raceStartTime = lapStartTime = startTime;
        racing = true;
    }

    void Awake() => ResetForRace();

    public void OnCheckpointPassed(Checkpoint checkpoint)
    {
        if (!racing || Finished) return;

        if (checkpoint.IsFinishLine)
        {
            if (nextCheckpoint < checkpointCount) return; // skipped part of the track
            if (currentLap > 0)
            {
                float lapTime = Time.time - lapStartTime;
                if (BestLap < 0f || lapTime < BestLap) BestLap = lapTime;
            }
            lapStartTime = Time.time;
            nextCheckpoint = 0;

            if (currentLap >= totalLaps)
            {
                FinishTime = Time.time - raceStartTime;
                RaceFinished?.Invoke(this);
            }
            else
            {
                currentLap++;
                LapStarted?.Invoke(this, currentLap);
            }
        }
        else if (checkpoint.Index == nextCheckpoint)
        {
            nextCheckpoint++;
        }
    }
}
