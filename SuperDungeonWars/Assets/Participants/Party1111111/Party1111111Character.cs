using System.Collections.Generic;
using UnityEngine;

namespace Party1111111
{
    /// <summary>
    /// 3日目（2026-10-25）の個人判断。味方運搬者への脅威を優先して迎撃する。移動先の決定はParty側に集約し、指示の競合を防ぐ。
    /// </summary>
    public sealed class Party1111111Character : ComCharacterBase
    {
        /// <summary>
        /// ゲーム側が渡す攻撃可能な候補から、護衛対象への脅威、次に敵運搬者を選ぶ。
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
                // Partyが公開観測から通知した、味方運搬者へ接近中の敵だけを追加で迎撃する。
                // 気絶した敵への追撃や、護衛・宝の奪取に関係しない戦闘には時間を使わない。
                bool defense = AriadneTactics.IsDefenseTarget(SXG_CharacterId, enemy.CharacterId);
                if (enemy.IsKnockedOut || (!enemy.HasTreasure && !defense)) continue;
                // 護衛を2、宝の奪取を1として優先度を比較する。距離との加算による単位混在を避ける。
                int priority = (defense ? 2 : 0) + (enemy.HasTreasure ? 1 : 0);

                // 比較だけなので平方根は不要。二乗距離のまま最も近い候補を選ぶ。
                float d = (enemy.WorldPosition - SXG_WorldPosition).sqrMagnitude;
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
