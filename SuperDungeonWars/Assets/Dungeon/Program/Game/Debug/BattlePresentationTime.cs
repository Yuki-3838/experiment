using System.Collections;
using UnityEngine;

/// <summary>
/// リザルト演出で使用する、早送り可能な実時間を提供する。
/// </summary>
public static class BattlePresentationTime
{
    private static float s_multiplier = 1.0f;

    public static float DeltaTime
    {
        get { return Time.unscaledDeltaTime * s_multiplier; }
    }

    public static void SetMultiplier(float multiplier)
    {
        s_multiplier = Mathf.Max(1.0f, multiplier);
    }

    public static void ResetMultiplier()
    {
        s_multiplier = 1.0f;
    }

    public static IEnumerator WaitForSeconds(float seconds)
    {
        if (seconds <= 0.0f)
        {
            yield break;
        }

        float elapsedTime = 0.0f;

        while (elapsedTime < seconds)
        {
            elapsedTime += DeltaTime;
            yield return null;
        }
    }

    [RuntimeInitializeOnLoadMethod(
        RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnSubsystemRegistration()
    {
        ResetMultiplier();
    }
}
