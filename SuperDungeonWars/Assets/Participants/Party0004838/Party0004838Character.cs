using System.Collections.Generic;
using UnityEngine;

namespace Party0004838
{
    /// <summary>
    /// 追加強化3（2026-10-26）の個人判断。反撃と、中央担当だけの宝箱取得の選別を行う。
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

        // 中央担当1人は、途中の通常宝箱で運搬を始めて目的を失わないよう選別する。
        // ほかの仲間と、中央担当を解除された仲間は、従来どおり通常宝箱も拾える。
        protected override bool SXG_ShouldPickUpTreasure(TreasureType type)
            => !SXG_HasTreasure && (type == TreasureType.CentralChest || !AriadneTactics.IsCentralRunner(SXG_CharacterId));

        // 自分の搬出という公開イベントを連絡帳へ返し、空になった中央の探索を再開させない。
        protected override void SXG_OnTreasureExported(TreasureType type)
        {
            if (type == TreasureType.CentralChest) AriadneTactics.NotifyCentralExported(SXG_CharacterId);
        }
    }
}
