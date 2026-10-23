using System.Collections.Generic;
using UnityEngine;

namespace Party1111111
{
    /// <summary>
    /// 個人の攻撃と宝箱取得の判断。移動先の決定はParty側に集約し、指示の競合を防ぐ。
    /// </summary>
    public sealed class Party1111111Character : ComCharacterBase
    {
        /// <summary>
        /// ゲーム側が渡す攻撃可能な候補から、近くの宝箱運搬者を選ぶ。
        /// 返す値はキャラクターIDではなく、candidates内の添字。-1なら攻撃しない。
        /// </summary>
        protected override int SXG_ChooseAttackTargetIndex(IReadOnlyList<VisibleCharacterData> candidates)
        {
            // 自分の搬出を優先し、運搬中・気絶中・行動ロック中には攻撃を要求しない。
            if (SXG_HasTreasure || SXG_IsKnockedOut || SXG_IsActionLocked) return -1;
            int best = -1;
            float distance = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                VisibleCharacterData enemy = candidates[i];
                // 得点に直結しない戦闘を減らすため、宝を持たない敵や気絶中の敵は狙わない。
                if (enemy.IsKnockedOut || !enemy.HasTreasure) continue;

                // 比較だけなので平方根は不要。二乗距離のまま最も近い候補を選ぶ。
                float d = (enemy.WorldPosition - SXG_WorldPosition).sqrMagnitude;
                if (d < distance) { distance = d; best = i; }
            }
            return best;
        }

        // 宝の種類は問わず、手が空いているときだけ接触した宝を拾う。
        // この段階では中央宝箱を探すために通常宝箱を拒否するような選別はしない。
        protected override bool SXG_ShouldPickUpTreasure(TreasureType type) => !SXG_HasTreasure;
    }
}
