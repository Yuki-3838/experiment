using UnityEngine;

/// <summary>
/// 試合終了画面とリザルト演出を自動で早送りする。
/// BattleResultPresentation と同じ GameObject に取り付ける。
/// </summary>
[RequireComponent(typeof(BattleResultPresentation))]
public sealed class BattleResultFastForwardController : MonoBehaviour
{
    [SerializeField]
    [Min(1.0f)]
    private float m_fastForwardMultiplier = 5.0f;

    private BattleResultPresentation m_resultPresentation;

    private void Awake()
    {
        m_resultPresentation =
            GetComponent<BattleResultPresentation>();
    }

    private void Update()
    {
        if (m_resultPresentation == null
            || m_resultPresentation.IsResultSequenceCompleted)
        {
            BattlePresentationTime.ResetMultiplier();
            return;
        }

        BattlePresentationTime.SetMultiplier(
            m_fastForwardMultiplier);
    }

    private void OnDisable()
    {
        BattlePresentationTime.ResetMultiplier();
    }
}
