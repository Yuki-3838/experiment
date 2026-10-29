using System.Collections.Generic;
using UnityEngine;

namespace AriadneEntranceAmbush
{
    /// <summary>
    /// 独立待ち伏せ版（2026-10-27）の個人判断。通常の回収AIと連絡帳を共有しない。
    /// 移動先の決定はParty側に集約し、戦うためだけの追跡はここでは行わない。
    /// </summary>
    public sealed class AriadneEntranceAmbushCharacter : ComCharacterBase
    {
        /// <summary>
        /// ゲーム側が渡す攻撃可能な候補から、敵運搬者、護衛対象への脅威、密着した通常敵の順に選ぶ。
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
                // 指定した相手が手ぶらの短い間だけ殴らず待つ。取得後は即座に攻撃候補へ戻す。
                if (!enemy.HasTreasure && !defense && AriadneTactics.IsPickupBait(SXG_CharacterId, enemy.CharacterId)) continue;
                // 運搬者3、護衛2、通常敵0の優先度。距離の単位とは混ぜない。
                int priority = enemy.HasTreasure ? 3 : defense ? 2 : 0;

                // 比較だけなので平方根は不要。二乗距離のまま最も近い候補を選ぶ。
                Vector3 delta = enemy.WorldPosition - SXG_WorldPosition; delta.y = 0;
                float d = delta.sqrMagnitude;
                // 入口役は通常敵の攻撃で0.7秒ずつ足を止め続けない。密着0.65単位以内は反撃する。
                // 回収/奪取中と護衛対象への脅威は従来通り。運搬者は常に最優先。
                if (SXG_MemberIndex > 0 && !enemy.HasTreasure && !defense && d > 0.4225f
                    && AriadneTactics.TryGetAmbushPickup(SXG_CharacterId, out bool allowed) && !allowed) continue;
                if (priority > bestPriority || (priority == bestPriority && d < distance))
                { bestPriority = priority; distance = d; best = i; }
            }
            return best;
        }

        // 入口役は奪取または切り替え回収の間だけ取得する。待機・配置への移動中は寄り道を抑える。
        // 回収担当が中央へ向かう間は、途中の通常宝箱を拾って金宝箱への到達が遅れないようにする。
        protected override bool SXG_ShouldPickUpTreasure(TreasureType type)
        {
            if (SXG_HasTreasure) return false;
            if (AriadneTactics.TryGetAmbushPickup(SXG_CharacterId, out bool allowed)) return allowed;
            return type == TreasureType.CentralChest || !AriadneTactics.IsCentralRunner(SXG_CharacterId);
        }

        // 自分の搬出という公開イベントを連絡帳へ返し、空になった中央の探索を再開させない。
        protected override void SXG_OnTreasureExported(TreasureType type)
        {
            if (type == TreasureType.CentralChest) AriadneTactics.NotifyCentralExported(SXG_CharacterId);
        }
    }
}
