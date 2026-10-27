using UnityEngine;

namespace AriadneEntranceAmbush
{
    /// <summary>
    /// 独立待ち伏せ版（2026-10-27）。同じ敵入口へ3人を配置し、待機と一時回収を管理する。
    /// 公開マップの寸法と自分の出発口だけを使う補助クラス。アタッチは不要。
    /// </summary>
    internal sealed class AriadneAmbush
    {
        private readonly Vector2Int[] entrances;
        private readonly int[] entranceByMember;
        private readonly float[] quietSince, forageUntil;
        private int home = -1;
        private int targetEntrance = -1;
        private bool fallbackEnabled;

        internal AriadneAmbush(int count, int width, int height)
        {
            // 4辺中央という公開ルール上の位置。敵の非公開の現在位置や経路は参照しない。
            entrances = new[] { new Vector2Int(width / 2, height - 1), new Vector2Int(width - 1, height / 2),
                new Vector2Int(width / 2, 0), new Vector2Int(0, height / 2) };
            entranceByMember = new int[count]; quietSince = new float[count]; forageUntil = new float[count];
            for (int i = 0; i < count; i++) { entranceByMember[i] = -1; quietSince[i] = -1f; }
        }

        internal bool ConfigureHome(Vector2Int ownEntrance, int[] knownDistances, int width)
        {
            int own = -1;
            for (int i = 0; i < entrances.Length; i++) if (entrances[i] == ownEntrance) own = i;
            if (own < 0) return false;
            if (home == own) return true;
            home = own;
            for (int i = 0; i < entranceByMember.Length; i++) entranceByMember[i] = -1;
            // 経路が既知の敵入口があれば最短を選ぶ。未知なら座標上で近い入口を目標にする。
            // 座標差は誘導用であり、壁の向こうへ直進できるという意味ではない。
            int bestKnown = int.MaxValue, bestEstimate = int.MaxValue;
            targetEntrance = -1;
            for (int i = 0; i < entrances.Length; i++)
            {
                if (i == own) continue;
                Vector2Int exit = entrances[i];
                int d = knownDistances[exit.y * width + exit.x];
                int estimate = Mathf.Abs(exit.x - ownEntrance.x) + Mathf.Abs(exit.y - ownEntrance.y);
                if (d >= 0 && d < bestKnown)
                { bestKnown = d; targetEntrance = i; }
                else if (bestKnown == int.MaxValue && estimate < bestEstimate)
                { bestEstimate = estimate; targetEntrance = i; }
            }
            // メンバー0は回収専任。1〜3は同じ敵入口を守り、別の入口へ分散しない。
            for (int member = 1; member < entranceByMember.Length; member++)
                entranceByMember[member] = targetEntrance;
            ResetTimers();
            return true;
        }

        internal bool IsAmbusher(int member) => entranceByMember[member] >= 0;
        internal Vector2Int TargetEntrance => entrances[targetEntrance];

        internal Vector2Int Post(int member, KnownMapView map)
        {
            Vector2Int exit = entrances[entranceByMember[member]];
            Vector2Int inward = exit.y == map.Height - 1 ? Vector2Int.down : exit.y == 0 ? Vector2Int.up
                : exit.x == map.Width - 1 ? Vector2Int.left : Vector2Int.right;
            // 入口から1・2・3マス内側へずらして構え、全員が同一点へ重なるのを抑える。
            // 開口を観測した範囲だけ進め、曲がり角や未知の通路へは配置を延ばさない。
            Vector2Int post = exit;
            for (int step = 0; step < member && map.CanMove(post, post + inward); step++) post += inward;
            return post;
        }

        internal void SetFallbackEnabled(bool enabled)
        {
            if (fallbackEnabled == enabled) return;
            fallbackEnabled = enabled;
            // 実行中のON/OFFでも、以前の待機時間を持ち越して即座に切り替わらないようにする。
            ResetTimers();
        }

        internal void ResetMember(int member)
        { quietSince[member] = -1f; forageUntil[member] = 0f; }

        internal void ResetTimers()
        { for (int i = 0; i < entranceByMember.Length; i++) ResetMember(i); }

        /// <summary>単位はゲーム内秒。移動中やKO中を「入口で空振りした時間」へ数えない。</summary>
        internal bool Update(int member, bool nearPost, bool carrierNearPost,
            float now, float quietSeconds, float forageSeconds)
        {
            if (!fallbackEnabled)
            { ResetMember(member); return false; }
            if (carrierNearPost)
            {
                // 近場回収中でも運搬者が見えたら迎撃へ戻す。通常敵だけでは待機時計をリセットしない。
                forageUntil[member] = 0f;
                quietSince[member] = nearPost ? now : -1f;
                return false;
            }
            if (forageUntil[member] > now) return true;
            if (forageUntil[member] > 0f) ResetMember(member);
            if (!nearPost) { quietSince[member] = -1f; return false; }
            if (quietSince[member] < 0f) quietSince[member] = now;
            if (now - quietSince[member] < Mathf.Max(1f, quietSeconds)) return false;
            forageUntil[member] = now + Mathf.Max(1f, forageSeconds);
            quietSince[member] = -1f;
            return true;
        }

        internal float ForageSecondsLeft(int member, float now) => Mathf.Max(0f, forageUntil[member] - now);
    }
}
