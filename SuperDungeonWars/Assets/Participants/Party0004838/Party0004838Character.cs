using System.Collections.Generic;
using UnityEngine;

namespace Party0004838
{
    /// <summary>
    /// 2026-10-29。護衛対象と奪取対象を優先して反撃し、担当に応じて宝箱の取得を選ぶ。
    /// 移動先の決定はParty側に集約し、戦うためだけの追跡はここでは行わない。
    /// </summary>
    public sealed class Party0004838Character : ComCharacterBase
    {
        /// <summary>
        /// ゲーム側が渡す候補から、金宝箱の運搬者、担当中の奪取対象、護衛への脅威を優先する。
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
                // 金宝箱は4、担当中の奪取は3、護衛は2に、運搬者への1を加える。距離とは混ぜない。
                int priority = (AriadneTactics.IsGoldTarget(SXG_CharacterId, enemy.CharacterId) ? 4 : 0)
                    + (AriadneTactics.IsHunting(SXG_CharacterId, enemy.CharacterId) && enemy.HasTreasure ? 3 : defense ? 2 : 0)
                    + (enemy.HasTreasure ? 1 : 0);

                // 比較だけなので平方根は不要。二乗距離のまま最も近い候補を選ぶ。
                Vector3 delta = enemy.WorldPosition - SXG_WorldPosition; delta.y = 0;
                float d = delta.sqrMagnitude;
                if (priority > bestPriority || (priority == bestPriority && d < distance))
                { bestPriority = priority; distance = d; best = i; }
            }
            return best;
        }

        // 発見済みの金宝箱を回収する担当は、道中の通常宝箱を見送る。
        // 金宝箱が未発見の探索では通常宝も取得する。護衛役は通常宝を見送る。
        protected override bool SXG_ShouldPickUpTreasure(TreasureType type)
            => !SXG_HasTreasure && (type == TreasureType.CentralChest || !AriadneTactics.IsCentralRunner(SXG_CharacterId))
                && AriadneTactics.CanPickUp(SXG_CharacterId, type);

        // 自分の搬出という公開イベントを連絡帳へ返し、空になった中央の探索を再開させない。
        protected override void SXG_OnTreasureExported(TreasureType type)
        {
            if (type == TreasureType.CentralChest) AriadneTactics.NotifyCentralExported(SXG_CharacterId);
        }
    }
}
