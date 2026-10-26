using UnityEngine;

/// <summary>
/// Inspector-driven controls for changing the game's global time scale.
/// </summary>
[DisallowMultipleComponent]
public sealed class GameSpeedDebugController : MonoBehaviour
{
    private float m_initialTimeScale = 1.0f;

    private void Awake()
    {
        m_initialTimeScale = Time.timeScale;
    }

    /// <summary>
    /// Sets the global game speed. A value of 0 pauses scaled-time systems.
    /// </summary>
    public void SetGameSpeed(float speedMultiplier)
    {
        if (!Application.isPlaying)
        {
            return;
        }

        Time.timeScale = Mathf.Max(0.0f, speedMultiplier);
    }

    /// <summary>
    /// Restores the time scale captured when this component became active.
    /// </summary>
    public void ResetGameSpeed()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        Time.timeScale = m_initialTimeScale;
    }

    private void OnDisable()
    {
        Time.timeScale = m_initialTimeScale;
    }
}
