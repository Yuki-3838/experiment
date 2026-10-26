using UnityEngine;

/// <summary>
/// この GameObject が有効な間、結果画面を自動で早送りする。
/// Inspector で他のコンポーネントを割り当てる必要はない。
/// </summary>
public sealed class BattleResultFastForwardController : MonoBehaviour
{
    [SerializeField]
    [Min(1.0f)]
    private float m_fastForwardMultiplier = 5.0f;

    private float m_timeScaleBeforeFastForward = 1.0f;

    private void OnEnable()
    {
        m_timeScaleBeforeFastForward = Time.timeScale;
        Time.timeScale = Mathf.Max(
            1.0f,
            m_fastForwardMultiplier);
    }

    private void OnDisable()
    {
        Time.timeScale = m_timeScaleBeforeFastForward;
    }
}