using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// BattleResultPresentationと同じGameObjectへ1本アタッチして、宝箱の開封と集計を短縮する。
/// 配布版の実時間による待機にも対応するため、時間倍率ではなく待ち時間の設定値を変更する。
/// Inspectorでの参照設定や、ほかのスクリプト・Prefab・シーンの書き換えは不要。
/// ゲーム固有クラスの型は直接参照せず、待ち時間のフィールドを持つコンポーネントを実行時に探す。
/// ゲーム画面用の補助であり、参加者AIの提出フォルダーには入れない。
/// </summary>
[DisallowMultipleComponent]
public sealed class BattleResultFastForwardController : MonoBehaviour
{
    [SerializeField]
    [Min(1.0f)]
    private float m_fastForwardMultiplier = 5.0f;

    // 秒数は倍率を適用する前の上限。既存設定がこれより短ければ、その短さを維持する。
    // 開封前・金額表示・次の開封までの各待ちをまとめて短縮し、途中の長い待ちを残さない。
    private static readonly DurationLimit[] PresentationLimits =
    {
        new DurationLimit("m_resultStartDelay", 0.20f),
        new DurationLimit("m_totalCoinCountUpDuration", 0.08f),
        new DurationLimit("m_treasureRevealInterval", 0.05f),
        new DurationLimit("m_treasureOpenRevealDelay", 0.04f),
        new DurationLimit("m_resultFinishHoldDuration", 0.25f),
        new DurationLimit("m_centralChestPreOpenDelay", 0.35f)
    };

    // 金額ポップアップが終わるまで次の宝箱へ進まないため、生成された各レーンも対象にする。
    private static readonly DurationLimit[] LaneLimits =
    {
        new DurationLimit("m_addedCoinPopupPopDuration", 0.06f),
        new DurationLimit("m_addedCoinPopupHoldDuration", 0.18f),
        new DurationLimit("m_addedCoinPopupFadeOutDuration", 0.08f)
    };

    private readonly List<FieldBackup> m_backups = new List<FieldBackup>();
    // Unity標準の型だけで取得する。ゲーム用asmdefへの参照や、レーンのクラス名に依存しない。
    private readonly List<MonoBehaviour> m_candidates = new List<MonoBehaviour>();
    private readonly HashSet<MonoBehaviour> m_checkedChildren = new HashSet<MonoBehaviour>();
    private MonoBehaviour m_presentation;
    private float m_activeMultiplier;
    private bool m_warnedMissingField;

    private readonly struct DurationLimit
    {
        internal readonly string FieldName;
        internal readonly float MaximumSeconds;
        internal DurationLimit(string fieldName, float maximumSeconds)
        { FieldName = fieldName; MaximumSeconds = maximumSeconds; }
    }

    private sealed class FieldBackup
    {
        internal Component Target;
        internal FieldInfo Field;
        internal float Original, Applied;
    }

    private void OnEnable()
    {
        m_presentation = null;
        m_candidates.Clear();
        GetComponents<MonoBehaviour>(m_candidates);
        foreach (MonoBehaviour candidate in m_candidates)
        {
            if (candidate == null || candidate == this || !HasDurationSettings(candidate, PresentationLimits)) continue;
            m_presentation = candidate;
            break;
        }
        if (m_presentation == null)
        {
            Debug.LogError(
                "BattleResultFastForwardController: 結果表示の待ち時間が見つかりません。BattleResultPresentationと同じGameObjectにアタッチしてください。", this);
            return;
        }

        // 既存シーンの5という保存値をそのまま利用する。倍率変更は次回の有効化時に反映する。
        m_activeMultiplier = float.IsNaN(m_fastForwardMultiplier) || float.IsInfinity(m_fastForwardMultiplier)
            ? 1.0f : Mathf.Max(1.0f, m_fastForwardMultiplier);
        m_warnedMissingField = false;
        if (m_activeMultiplier <= 1.0f) return;
        ApplyLimits(m_presentation, PresentationLimits);
        ConfigureSpawnedPopups();
    }

    private void LateUpdate()
    {
        if (m_presentation == null || m_activeMultiplier <= 1.0f) return;
        // Showで後から生成される4チームのレーンを見つける。各インスタンスは1度だけ設定し、
        // 毎フレームの割り算で待ち時間が縮み続けることや、Prefabアセットの変更を避ける。
        ConfigureSpawnedPopups();
    }

    private void ConfigureSpawnedPopups()
    {
        m_candidates.Clear();
        GetComponentsInChildren<MonoBehaviour>(true, m_candidates);
        foreach (MonoBehaviour candidate in m_candidates)
        {
            // 該当しないUIコンポーネントも確認済みへ入れ、Reflectionを毎フレーム繰り返さない。
            if (candidate == null || !m_checkedChildren.Add(candidate)) continue;
            if (HasDurationSettings(candidate, LaneLimits)) ApplyLimits(candidate, LaneLimits);
        }
    }

    private static bool HasDurationSettings(Component target, DurationLimit[] limits)
    {
        int matches = 0;
        foreach (DurationLimit limit in limits)
            // 無関係なコンポーネントを誤って選びにくくするため、float設定が2項目以上あるものに限定。
            if (FindDurationField(target.GetType(), limit.FieldName) != null && ++matches >= 2) return true;
        return false;
    }

    private static FieldInfo FindDurationField(System.Type type, string fieldName)
    {
        // 型名や名前空間は判定に使わない。基底クラスへ移動したprivate設定も取得できるよう辿る。
        while (type != null && type != typeof(MonoBehaviour))
        {
            FieldInfo field = type.GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null && field.FieldType == typeof(float) && !field.IsInitOnly) return field;
            type = type.BaseType;
        }
        return null;
    }

    private void ApplyLimits(Component target, DurationLimit[] limits)
    {
        foreach (DurationLimit limit in limits)
        {
            // 配布版の待ち時間はprivateで公開の設定APIがないため、ここだけ名前を指定して参照する。
            // 対象は結果表示の秒数だけ。点数、宝箱の結果、AIの観測や対戦状態には触れない。
            FieldInfo field = FindDurationField(target.GetType(), limit.FieldName);
            if (field == null)
            {
                if (!m_warnedMissingField)
                {
                    Debug.LogWarning("BattleResultFastForwardController: 対応する待ち時間が見つかりません。配布版を確認してください: "
                        + target.GetType().Name + "." + limit.FieldName, this);
                    m_warnedMissingField = true;
                }
                continue;
            }

            float original = (float)field.GetValue(target);
            if (float.IsNaN(original) || float.IsInfinity(original)) continue;
            // 実時間待ち・ゲーム内時間待ちのどちらでも、この秒数の短縮が直接効く。
            // Time.timeScaleには触れず、早送りのための二重の時間倍率も作らない。
            float applied = Mathf.Min(Mathf.Max(0.0f, original), limit.MaximumSeconds) / m_activeMultiplier;
            if (original == applied) continue;
            m_backups.Add(new FieldBackup { Target = target, Field = field, Original = original, Applied = applied });
            field.SetValue(target, applied);
        }
    }

    private void OnDisable()
    {
        foreach (FieldBackup backup in m_backups)
        {
            if (backup.Target == null) continue;
            // 自分が設定した値のままなら元へ戻す。ほかの処理やユーザーが途中で変えた値は保護する。
            if ((float)backup.Field.GetValue(backup.Target) == backup.Applied)
                backup.Field.SetValue(backup.Target, backup.Original);
        }
        m_backups.Clear(); m_candidates.Clear(); m_checkedChildren.Clear();
        m_presentation = null;
    }
}
