using System.Collections.Generic;
using UnityEngine;

namespace Party0004838
{
    /// <summary>
    /// 追加強化2（2026-10-26）の個人判断。護衛と奪取を優先し、攻撃範囲内の通常敵にも反撃する。
    /// 移動先の決定はParty側に集約し、戦うためだけの追跡はここでは行わない。
    /// </summary>
    public sealed class Party0004838Character : ComCharacterBase
    {
        /// <summary>
        /// ゲーム側が渡す攻撃可能な候補から、護衛対象への脅威、敵運搬者、通常敵の順に選ぶ。
        /// 返す値はキャラクターIDではなく、candidates内の添字。-1なら攻撃しない。
        /// </summary>
        protected override int SXG_ChooseAttackTargetIndex(IReadOnlyList<VisibleCharacterData> candidates)
        {
            // 自分の搬出を優先し、運搬中・気絶中・行動ロック中には攻撃を要求しない。
            if (SXG_HasTreasure || SXG_IsKnockedOut || SXG_IsActionLocked) return -1;
            int best = -1, bestPriority = -1;
            float distance = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                VisibleCharacterData enemy = candidates[i];
                // Partyが公開観測から通知した護衛対象かどうかを確認し、攻撃の優先順位を付ける。
                // 運搬前に通常敵へ接近した場合も、反撃を放棄して一方的に倒されるのを避けたい。
                // 攻撃候補はゲーム側で範囲判定済みなので、このために移動先を変更する必要はない。
                // 気絶した敵への追撃は起き上がり待ちに時間を使うため行わない。
                bool defense = AriadneTactics.IsDefenseTarget(SXG_CharacterId, enemy.CharacterId);
                if (enemy.IsKnockedOut) continue;
                // 護衛を2、宝の奪取を1、通常敵を0として比較する。距離の単位とは混ぜない。
                int priority = (defense ? 2 : 0) + (enemy.HasTreasure ? 1 : 0);

                // 比較だけなので平方根は不要。二乗距離のまま最も近い候補を選ぶ。
                Vector3 delta = enemy.WorldPosition - SXG_WorldPosition; delta.y = 0;
                float d = delta.sqrMagnitude;
                if (priority > bestPriority || (priority == bestPriority && d < distance))
                { bestPriority = priority; distance = d; best = i; }
            }
            return best;
        }

        // 宝の種類は問わず、手が空いているときだけ接触した宝を拾う。
        // この段階では中央宝箱を探すために通常宝箱を拒否するような選別はしない。
        protected override bool SXG_ShouldPickUpTreasure(TreasureType type) => !SXG_HasTreasure;
    }
}
