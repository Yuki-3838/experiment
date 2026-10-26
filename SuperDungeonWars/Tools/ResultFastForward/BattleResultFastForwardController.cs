using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// 結果画面のルートへ1本アタッチし、そのオブジェクトと子の待ち時間設定を短縮する。
/// UnityとC#標準機能だけを使い、ゲーム固有の型・固定フィールド名・ほかの追加スクリプトに依存しない。
/// 参照設定は不要。ゲーム画面用の補助なので、参加者AIの提出フォルダーには入れない。
/// </summary>
[DisallowMultipleComponent]
public sealed class BattleResultFastForwardController : MonoBehaviour
{
    [SerializeField]
    [Min(1.0f)]
    private float m_fastForwardMultiplier = 5.0f;

    // 単位は倍率適用前の秒。長い溜め演出も待ち続けないよう、共通上限0.35秒を設ける。
    private const float MaximumSecondsBeforeMultiplier = 0.35f;

    // 型ごとに時間設定を一度だけ調べる。キャッシュにはUnityオブジェクト自体を保持しない。
    private static readonly Dictionary<Type, FieldInfo[]> DurationFields = new Dictionary<Type, FieldInfo[]>();
    private readonly List<MonoBehaviour> m_targets = new List<MonoBehaviour>();
    private readonly HashSet<MonoBehaviour> m_checkedTargets = new HashSet<MonoBehaviour>();
    private readonly List<FieldBackup> m_backups = new List<FieldBackup>();
    private float m_activeMultiplier = 1.0f;

    private sealed class FieldBackup
    {
        internal MonoBehaviour Target;
        internal FieldInfo Field;
        internal float Original, Applied;
    }

    private void OnEnable()
    {
        // 既存のInspector保存値も利用する。倍率1は短縮なし、変更は次回有効化時に反映する。
        m_activeMultiplier = float.IsNaN(m_fastForwardMultiplier) || float.IsInfinity(m_fastForwardMultiplier)
            ? 1.0f : Mathf.Max(1.0f, m_fastForwardMultiplier);
        if (m_activeMultiplier > 1.0f) ConfigureTargets();
    }

    private void LateUpdate()
    {
        // 表示後に生成される金額ポップアップなども自動で拾う。
        // 既存の対象は再計算せず、毎フレーム割り算して縮み続けることを防ぐ。
        if (m_activeMultiplier > 1.0f) ConfigureTargets();
    }

    private void ConfigureTargets()
    {
        m_targets.Clear();
        GetComponentsInChildren<MonoBehaviour>(true, m_targets);
        foreach (MonoBehaviour target in m_targets)
        {
            if (target == null || target == this || !m_checkedTargets.Add(target)) continue;
            foreach (FieldInfo field in GetDurationFields(target.GetType()))
            {
                float original = (float)field.GetValue(target);
                // 無効値や負値は「無効」の指定かもしれないため、そのまま維持する。
                if (float.IsNaN(original) || float.IsInfinity(original) || original <= 0.0f) continue;
                float applied = Mathf.Min(original, MaximumSecondsBeforeMultiplier) / m_activeMultiplier;
                if (original == applied) continue;
                m_backups.Add(new FieldBackup { Target = target, Field = field, Original = original, Applied = applied });
                field.SetValue(target, applied);
            }
        }
    }

    private static FieldInfo[] GetDurationFields(Type type)
    {
        if (DurationFields.TryGetValue(type, out FieldInfo[] cached)) return cached;
        List<FieldInfo> fields = new List<FieldInfo>();
        for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                // 公開またはSerializeFieldの付いたfloatだけに限定する。プロパティやメソッドは呼ばない。
                // 位置・速度・得点などを時間と誤認しないよう、時間を表す一般的な接尾辞で絞る。
                if (field.FieldType != typeof(float) || field.IsInitOnly || field.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), false)) continue;
                if (!IsDurationName(field.Name)) continue;
                fields.Add(field);
            }
        }
        FieldInfo[] result = fields.ToArray();
        DurationFields[type] = result;
        return result;
    }

    private static bool IsDurationName(string name)
    {
        // 個別ゲームの変数名は指定しない。Duration=長さ、Delay=待ち、Interval=間隔。
        // これらの接尾辞がない処理や、コードへ直接書かれた待ち時間には作用しない。
        return name.EndsWith("Duration", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Delay", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Interval", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDisable()
    {
        foreach (FieldBackup backup in m_backups)
        {
            if (backup.Target == null) continue;
            // 自分が設定した値のままなら元に戻す。途中で手動変更された値は保護する。
            if ((float)backup.Field.GetValue(backup.Target) == backup.Applied)
                backup.Field.SetValue(backup.Target, backup.Original);
        }
        m_backups.Clear(); m_targets.Clear(); m_checkedTargets.Clear();
        m_activeMultiplier = 1.0f;
    }
}
