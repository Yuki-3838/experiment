using UnityEngine;

namespace Party0004838
{
    /// <summary>
    /// 追加強化4（2026-10-27）。入口担当と、運搬者が来ないときの切り替え時間を管理する。
    /// 公開マップの寸法と自分の出発口だけを使う補助クラス。アタッチは不要。
    /// </summary>
    internal sealed class AriadneAmbush
    {
        private readonly Vector2Int[] entrances;
        private readonly int[] entranceByMember;
        private readonly float[] quietSince, forageUntil;
        private int home = -1;
        private bool fallbackEnabled;

        internal AriadneAmbush(int count, int width, int height)
        {
            // 4辺中央という公開ルール上の位置。敵の非公開の現在位置や経路は参照しない。
            entrances = new[] { new Vector2Int(width / 2, height - 1), new Vector2Int(width - 1, height / 2),
                new Vector2Int(width / 2, 0), new Vector2Int(0, height / 2) };
            entranceByMember = new int[count]; quietSince = new float[count]; forageUntil = new float[count];
            for (int i = 0; i < count; i++) { entranceByMember[i] = -1; quietSince[i] = -1f; }
        }

        internal bool ConfigureHome(Vector2Int ownEntrance)
        {
            int own = -1;
            for (int i = 0; i < entrances.Length; i++) if (entrances[i] == ownEntrance) own = i;
            if (own < 0) return false;
            if (home == own) return true;
            home = own;
            for (int i = 0; i < entranceByMember.Length; i++) entranceByMember[i] = -1;
            // メンバー0を回収担当として残し、残りを別々の3入口へ固定して担当の入れ替わりを抑える。
            int member = 1;
            for (int i = 0; i < entrances.Length && member < entranceByMember.Length; i++)
                if (i != own) entranceByMember[member++] = i;
            ResetTimers();
            return true;
        }

        internal bool IsAmbusher(int member) => entranceByMember[member] >= 0;

        internal Vector2Int Post(int member, KnownMapView map)
        {
            Vector2Int exit = entrances[entranceByMember[member]];
            Vector2Int inward = exit.y == map.Height - 1 ? Vector2Int.down : exit.y == 0 ? Vector2Int.up
                : exit.x == map.Width - 1 ? Vector2Int.left : Vector2Int.right;
            // 接続を確認できたら入口1マス手前で構える。未知の接続は仮定しない。
            return map.CanMove(exit, exit + inward) ? exit + inward : exit;
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
