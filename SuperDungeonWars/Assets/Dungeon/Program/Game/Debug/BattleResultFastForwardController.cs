using UnityEngine;

/// <summary>
/// 試合終了画面とリザルト演出を自動で早送りする。
/// BattlePrototypeGameController と同じ GameObject に取り付ける。
/// </summary>
public sealed class BattleResultFastForwardController : MonoBehaviour
{
    [SerializeField]
    private BattlePrototypeSceneFlowController m_sceneFlowController;

    [SerializeField]
    [Min(1.0f)]
    private float m_fastForwardMultiplier = 5.0f;

    private void Awake()
    {
        if (m_sceneFlowController == null)
        {
            m_sceneFlowController =
                GetComponent<BattlePrototypeSceneFlowController>();
        }
    }

    private void Update()
    {
        if (!IsBattleEndOrResultFlow())
        {
            BattlePresentationTime.ResetMultiplier();
            return;
        }

        BattlePresentationTime.SetMultiplier(
            m_fastForwardMultiplier);
    }

    private bool IsBattleEndOrResultFlow()
    {
        if (m_sceneFlowController == null)
        {
            return false;
        }

        BattlePrototypeSceneFlowController.SceneFlow sceneFlow =
            m_sceneFlowController.CurrentSceneFlow;

        return sceneFlow
                   == BattlePrototypeSceneFlowController.SceneFlow.BattleEndWait
            || sceneFlow
                   == BattlePrototypeSceneFlowController.SceneFlow.Result;
    }

    private void OnDisable()
    {
        BattlePresentationTime.ResetMultiplier();
    }
}
