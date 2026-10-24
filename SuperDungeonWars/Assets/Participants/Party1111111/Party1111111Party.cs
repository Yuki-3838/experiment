using System.Collections.Generic;
using UnityEngine;

namespace Party1111111
{
    /// <summary>
    /// Ariadneの部隊全体を指揮するAI。4人へ探索・回収・帰還の仕事を割り当てる。
    /// 判断材料は公開SXG APIで取得した観測情報だけで、未観測の迷路は参照しない。
    /// </summary>
    public sealed class Party1111111Party : ComPartyBase
    {
        // 宝箱IDごとの最終観測情報。視界から消えても、一定時間は回収候補として覚えておく。
        private readonly Dictionary<int, TreasureMemory> treasures = new Dictionary<int, TreasureMemory>();

        // expiredは削除予定の宝箱ID、reservedは今回すでに担当を割り当てた目標セル。
        // Dictionaryの列挙中には削除せず、列挙が終わってからまとめて削除する。
        private readonly List<int> expired = new List<int>(), reserved = new List<int>();
        private AriadneMap graph;
        private PartyMemberInfo[] members;

        // [メンバー番号][セル番号]。距離の単位はマス、firstStepsは最初に進む隣接セル。
        // 配列は初回だけ確保し、毎回の経路探索で使い回す。
        private int[][] distances, firstSteps;

        // goalsは前回の担当目標。assignedは今回の思考で仕事が確定済みかを表す。
        private int[] goals;
        private bool[] assigned;

        // Unityのゲーム内時刻（秒）。一時停止やゲーム速度の変更に追従する。
        private float startedAt, nextThink;

        // Valueは実際の獲得点ではなく、宝箱の種類から見積もった相対的な価値。
        private sealed class TreasureMemory
        {
            internal int Cell;
            internal Vector3 Position;
            internal float Value, SeenAt;
        }

        protected override void SXG_OnPartyThink()
        {
            // 全員分の探索を毎フレームやり直す負荷を抑える。0.12秒ごとに再計画する。
            if (Time.time < nextThink) return;
            nextThink = Time.time + 0.12f;
            KnownMapView map = SXG_GetKnownMap();
            if (map == null) return;
            if (graph == null)
            {
                // 初回の思考時に、実際のマップ寸法と部隊人数に合わせて作業領域を用意する。
                startedAt = Time.time;
                graph = new AriadneMap(map.Width, map.Height);
                int count = SXG_GetMemberCount();
                members = new PartyMemberInfo[count];
                distances = new int[count][]; firstSteps = new int[count][];
                goals = new int[count]; assigned = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    distances[i] = new int[graph.Size]; firstSteps[i] = new int[graph.Size]; goals[i] = -1;
                }
            }
            // 仲間の誰かが新しく観測した通路を取り込み、帰還距離も更新する。
            graph.Refresh(map);
            reserved.Clear();
            for (int i = 0; i < members.Length; i++)
            {
                SXG_TryGetMemberInfo(i, out members[i]);
                // 気絶中・行動ロック中の仲間には新しい仕事を割り当てない。
                assigned[i] = members[i] == null || members[i].IsKnockedOut || members[i].IsActionLocked;
                if (assigned[i]) continue;
                graph.Search(graph.Index(members[i].CurrentCell), distances[i], firstSteps[i]);
                if (!members[i].HasTreasure) continue;

                // 運搬中は搬出を最優先にする。帰還担当を探索・回収で上書きしない。
                // 自分の出発口に限らず、既知の経路で最も近い搬出口を選ぶ。
                assigned[i] = true;
                int exit = graph.ClosestExit(distances[i]);
                if (exit >= 0) Move(i, exit); else SXG_StopMember(i);
                goals[i] = -1;
            }
            RememberTreasures();

            // 「誰が・何をするか」の全組合せから、評価の高い組を1件ずつ確定する。
            // 先頭の仲間が遠くの宝を先取りするのを防ぎ、同じセルの担当も重複させない。
            for (int round = 0; round < members.Length; round++)
            {
                float best = float.NegativeInfinity;
                int member = -1, target = -1;
                foreach (TreasureMemory t in treasures.Values)
                {
                    if (reserved.Contains(t.Cell)) continue;
                    for (int i = 0; i < members.Length; i++)
                    {
                        if (assigned[i]) continue;
                        int d = distances[i][t.Cell], back = graph.ExitDistances[t.Cell];
                        // 回収と搬出の両方について、既知の経路がある宝だけを候補にする。
                        if (d < 0 || back < 0) continue;

                        // 120秒制の時間見積もり。1マスの間隔2、通常速度3、運搬速度2を前提とする。
                        // 往路0.72秒/マス・復路1.05秒/マスには小さな余裕を持たせ、
                        // 取得・搬出の行動ロックなどに3秒を確保する。衝突や戦闘までは予測しない。
                        if (d * 0.72f + back * 1.05f + 3f > 120f - (Time.time - startedAt)) continue;

                        // 価値が高く、回収から搬出まで短い宝を優先するための経験的な評価式。
                        // 2日目は探索との優先度倍率を12から60へ上げる。
                        // 見つけた宝の回収・搬出を近場の追加探索より優先し、探索だけで時間を使い切るのを抑える。
                        // 4は近距離で評価が極端になるのを抑えるマス相当の定数。
                        // 復路は運搬による拘束を考慮して距離（マス）を1.4倍にする。
                        float score = 60f * t.Value / (4f + d + back * 1.4f);

                        // 前回の担当を22%優遇し、僅かな評価差で目標を取り替え続けるのを抑える。
                        if (goals[i] == t.Cell) score *= 1.22f;
                        if (score > best) { best = score; member = i; target = t.Cell; }
                    }
                }
                foreach (int frontier in graph.Frontiers)
                {
                    if (reserved.Contains(frontier)) continue;
                    for (int i = 0; i < members.Length; i++)
                    {
                        if (assigned[i]) continue;
                        int d = distances[i][frontier];
                        if (d < 1) continue;

                        // 未観測セルの多い近場の探索先を優先する。情報量は周囲3×3の概算であり、
                        // 実際にその場所から見通せる範囲を保証するものではない。
                        float score = (2.5f + graph.InformationGain(frontier) * 0.25f) / (d + 2f);

                        // 中央ホールへの接続を探しやすくする弱い誘導。未知の通路は仮定しない。
                        score *= 1f + 0.25f / (1f + graph.DistanceToCenter(frontier) * 0.1f);

                        // すでに別の担当が向かうセルから3マス以内なら評価を1/4に下げて分散させる。
                        foreach (int other in reserved)
                            if (graph.Manhattan(frontier, other) <= 3) score *= 0.25f;

                        // 探索でも前回の目標を18%優遇する。各係数は今後の対戦で調整する候補。
                        if (goals[i] == frontier) score *= 1.18f;
                        if (score > best) { best = score; member = i; target = frontier; }
                    }
                }
                if (member < 0) break;
                assigned[member] = true; reserved.Add(target); goals[member] = target;
                Move(member, target);
            }
            for (int i = 0; i < members.Length; i++)
            {
                if (assigned[i]) continue;
                // 一本道では探索先が人数より少ないことがある。
                // その場合だけ担当の重複を許し、近い探索先へ進んで次の分岐を待つ。
                int target = -1, length = int.MaxValue;
                foreach (int frontier in graph.Frontiers)
                    if (distances[i][frontier] > 0 && distances[i][frontier] < length)
                    { length = distances[i][frontier]; target = frontier; }
                if (target >= 0) Move(i, target); else SXG_StopMember(i);
            }
        }

        /// <summary>
        /// 見えている宝を記憶へ反映し、古い情報を破棄する。
        /// 可視一覧から消えただけでは、他チームに回収されたとは断定しない。
        /// </summary>
        private void RememberTreasures()
        {
            IReadOnlyList<VisibleTreasureData> visible = SXG_GetVisibleTreasures();
            expired.Clear();
            foreach (KeyValuePair<int, TreasureMemory> pair in treasures)
            {
                bool seen = false;
                for (int j = 0; j < visible.Count; j++)
                    if (visible[j].TreasureId == pair.Key) { seen = true; break; }
                if (seen) continue;
                bool checkedCell = false;

                // 仲間が元の場所の近くまで来ても見えない宝は回収候補から外す。
                // 近くへ行けなかった情報も45秒で捨て、古い位置へ向かい続けないようにする。
                foreach (PartyMemberInfo m in members)
                    if (m != null && graph.Manhattan(graph.Index(m.CurrentCell), pair.Value.Cell) <= 1)
                        checkedCell = true;
                if (checkedCell || Time.time - pair.Value.SeenAt > 45f) expired.Add(pair.Key);
            }
            foreach (int id in expired) treasures.Remove(id);
            for (int i = 0; i < visible.Count; i++)
            {
                VisibleTreasureData t = visible[i];
                // 所有者にかかわらず、運ばれている宝は落ちている宝の回収候補に含めない。
                if (t.IsCarried) { treasures.Remove(t.TreasureId); continue; }

                // 通常宝箱の平均100点を1とし、中央宝箱の平均270点を2.7として扱う。
                // 実際の点数を非公開のゲーム状態から取得することはしない。
                treasures[t.TreasureId] = new TreasureMemory
                {
                    Cell = graph.Index(t.CellPosition), Position = t.WorldPosition,
                    Value = t.TreasureType == TreasureType.CentralChest ? 2.7f : 1f, SeenAt = Time.time
                };
            }
        }

        /// <summary>
        /// 選んだ目標までの最初の1マスだけを指示する。
        /// 次の思考で観測情報と現在位置を見直し、経路を更新する。
        /// </summary>
        private void Move(int member, int target)
        {
            if (graph.Index(members[member].CurrentCell) != target)
            {
                int next = firstSteps[member][target];
                if (next >= 0) SXG_SetMemberMoveTargetCell(member, graph.Cell(next));
                return;
            }
            // 戦闘で散った宝などはセル中央にあるとは限らない。
            // 同じセルまで来たら観測したワールド座標へ近づき、接触による取得を狙う。
            foreach (TreasureMemory t in treasures.Values)
                if (t.Cell == target) { SXG_SetMemberMoveTargetWorld(member, t.Position); return; }
            SXG_StopMember(member);
        }
    }
}
