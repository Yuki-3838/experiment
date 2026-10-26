using System.Collections.Generic;
using UnityEngine;

namespace Party0004838
{
    /// <summary>
    /// 追加強化1（2026-10-26）。反撃と、可視の敵を避ける運搬経路を追加。対戦での効果は未測定。
    /// Ariadneの部隊全体を指揮するAI。4人へ探索・回収・帰還の仕事を割り当てる。
    /// 判断材料は公開SXG APIで取得した観測情報だけで、未観測の迷路は参照しない。
    /// </summary>
    public sealed class Party0004838Party : ComPartyBase
    {
        // 宝箱IDごとの最終観測情報。視界から消えても、一定時間は回収候補として覚えておく。
        private readonly Dictionary<int, TreasureMemory> treasures = new Dictionary<int, TreasureMemory>();

        // expiredは削除予定の宝箱ID、reservedは今回すでに担当を割り当てた目標セル。
        // Dictionaryの列挙中には削除せず、列挙が終わってからまとめて削除する。
        private readonly List<int> expired = new List<int>(), reserved = new List<int>();
        private AriadneMap graph;
        // 自部隊の公開観測を個人AIへ伝える連絡帳。部隊終了時に登録を解除する。
        private readonly AriadneTactics tactics = new AriadneTactics();
        private PartyMemberInfo[] members;

        // [メンバー番号][セル番号]。距離の単位はマス、firstStepsは最初に進む隣接セル。
        // 配列は初回だけ確保し、毎回の経路探索で使い回す。
        private int[][] distances, firstSteps;

        // goalsは前回の担当目標。assignedは今回の思考で仕事が確定済みかを表す。
        private int[] goals;
        private bool[] assigned;
        // 可視の通常敵がいるセル周辺の危険と、各運搬者が前回選んだ搬出口。
        private float[] danger;
        private int[] returnExits;

        // 戦闘支援は同時に1人まで。継続時間と再開待ちの単位はゲーム内秒。
        private int tacticalMember = -1, tacticalEnemy = -1;
        private float tacticalUntil, tacticalRestUntil;

        // Unityのゲーム内時刻（秒）。一時停止やゲーム速度の変更に追従する。
        private float startedAt, nextThink;

        // Valueは実際の獲得点ではなく、宝箱の種類から見積もった相対的な価値。
        private sealed class TreasureMemory
        {
            internal int Cell;
            internal Vector3 Position;
            internal float Value, SeenAt;
        }

        private void OnDestroy() => tactics.Release();

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
                danger = new float[graph.Size]; returnExits = new int[count];
                for (int i = 0; i < count; i++)
                {
                    distances[i] = new int[graph.Size]; firstSteps[i] = new int[graph.Size]; goals[i] = -1; returnExits[i] = -1;
                }
            }
            // 仲間の誰かが新しく観測した通路を取り込み、帰還距離も更新する。
            graph.Refresh(map);
            reserved.Clear();
            for (int i = 0; i < members.Length; i++)
            {
                SXG_TryGetMemberInfo(i, out members[i]);
                // 搬出やKOで手ぶらになったら前回の出口への優遇も終了し、次の運搬へ引き継がない。
                if (members[i] != null && !members[i].HasTreasure) returnExits[i] = -1;
                // 気絶中・行動ロック中の仲間には新しい仕事を割り当てない。
                assigned[i] = members[i] == null || members[i].IsKnockedOut || members[i].IsActionLocked;
                if (assigned[i]) continue;
                graph.Search(graph.Index(members[i].CurrentCell), distances[i], firstSteps[i]);
            }
            IReadOnlyList<VisibleCharacterData> visibleCharacters = SXG_GetVisibleCharacters();
            tactics.Refresh(members, visibleCharacters, map);
            bool hasDanger = RefreshDanger(visibleCharacters);
            for (int i = 0; i < members.Length; i++)
            {
                if (assigned[i] || !members[i].HasTreasure) continue;

                // 運搬中は搬出を最優先にする。帰還担当を探索・回収で上書きしない。
                // 自分の出発口に限らず、既知経路の近さと可視の敵への接近を考慮する。
                assigned[i] = true;
                int exit = graph.ClosestExit(distances[i]);
                // 敵が見えるときは、既知通路の範囲で危険を避ける帰還も比較する。
                // 敵を避けるための際限のない遠回りはMap側の6歩・残り秒数で制限する。
                if (hasDanger && graph.TrySaferReturn(graph.Index(members[i].CurrentCell), distances[i],
                    danger, returnExits[i], 120f - (Time.time - startedAt), out int next, out int saferExit))
                {
                    returnExits[i] = saferExit;
                    SXG_SetMemberMoveTargetCell(i, graph.Cell(next));
                }
                else
                {
                    returnExits[i] = exit;
                    if (exit >= 0) Move(i, exit); else SXG_StopMember(i);
                }
                goals[i] = -1;
            }
            RememberTreasures();
            AssignSupport(visibleCharacters);

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
        /// 手の空いた仲間を最大1人だけ、近場の護衛か敵運搬者の迎撃に回す。
        /// 見失った敵は追わず、既知経路で届く短い仕事だけを引き受ける。
        /// </summary>
        private void AssignSupport(IReadOnlyList<VisibleCharacterData> visible)
        {
            if (tacticalMember >= 0)
            {
                foreach (VisibleCharacterData enemy in visible)
                {
                    if (enemy.CharacterId != tacticalEnemy || Time.time >= tacticalUntil
                        || assigned[tacticalMember] || !CanSupport(tacticalMember, enemy)) continue;
                    IssueSupport(tacticalMember, enemy);
                    return;
                }
                // 接敵して行動ロックに入った時や、敵を見失った時も通常の回収へ戻す。
                // 終了後2.4秒は新しい追跡を始めず、敵について往復する時間を抑える。
                tacticalMember = tacticalEnemy = -1;
                tacticalRestUntil = Time.time + 2.4f;
            }
            if (Time.time < tacticalRestUntil) return;

            int bestMember = -1;
            VisibleCharacterData bestEnemy = null;
            float bestScore = float.NegativeInfinity;
            foreach (VisibleCharacterData enemy in visible)
            {
                for (int i = 0; i < members.Length; i++)
                {
                    if (assigned[i] || !CanSupport(i, enemy)) continue;
                    int d = distances[i][graph.Index(enemy.CellPosition)];
                    bool defense = tactics.ThreatensCarrier(enemy.CharacterId);
                    bool collectingNearby = false;
                    foreach (TreasureMemory treasure in treasures.Values)
                    {
                        int pickup = distances[i][treasure.Cell];
                        int back = graph.ExitDistances[treasure.Cell];
                        // 通常の回収と同じ秒単位の見積もりで、終了までに搬出可能な宝だけを守る。
                        if (pickup >= 0 && pickup <= 4 && back >= 0
                            && pickup * 0.72f + back * 1.05f + 3f <= 120f - (Time.time - startedAt))
                        { collectingNearby = true; break; }
                    }
                    // 4マス以内（通常移動で約2.9秒）に回収できる宝がある仲間は、任意の奪取追跡に使わない。
                    // 追跡の上限2.4秒に近い回収機会を守り、護衛以外の寄り道を抑える。
                    // 味方の護衛なら候補には残すが、距離2マス相当の負担を評価へ加える。
                    if (collectingNearby && !defense) continue;
                    // 護衛20・奪取10は無次元の優先度。候補の最大3歩の距離差より護衛を重くする。
                    float score = (defense ? 20f : 10f) - d - (collectingNearby ? 2f : 0f);
                    if (score > bestScore) { bestScore = score; bestMember = i; bestEnemy = enemy; }
                }
            }
            if (bestMember < 0) return;
            tacticalMember = bestMember; tacticalEnemy = bestEnemy.CharacterId;
            // 通常速度3・セル間隔2なら、2.4秒はおおよそ3マス分の移動時間。
            // この上限で回収役が敵を延々と追い続けるのを防ぐ。
            tacticalUntil = Time.time + 2.4f;
            IssueSupport(bestMember, bestEnemy);
        }

        private bool CanSupport(int member, VisibleCharacterData enemy)
        {
            if (tactics.IsFriend(enemy.CharacterId) || enemy.IsKnockedOut) return false;
            bool defense = tactics.ThreatensCarrier(enemy.CharacterId);
            if (!defense && !enemy.HasTreasure) return false;
            int cell = graph.Index(enemy.CellPosition);
            if (cell < 0 || cell >= graph.Size) return false;
            int distance = distances[member][cell];
            // 護衛は3歩、運搬者への迎撃は2歩以内の既知経路だけ。
            // 運搬速度2に対する通常速度3の差は小さいため、期限2.4秒で届きにくい追跡を減らす。
            return distance >= 0 && distance <= (defense ? 3 : 2);
        }

        /// <summary>今見えている、生存中かつ手ぶらの敵だけを運搬経路の危険として扱う。</summary>
        private bool RefreshDanger(IReadOnlyList<VisibleCharacterData> visible)
        {
            System.Array.Clear(danger, 0, danger.Length);
            bool any = false;
            foreach (VisibleCharacterData enemy in visible)
            {
                // 運搬中の敵自身は攻撃できない。自部隊のIDは公開メンバー一覧から識別する。
                if (enemy.IsKnockedOut || enemy.HasTreasure || tactics.IsFriend(enemy.CharacterId)) continue;
                graph.AddObservedDanger(danger, graph.Index(enemy.CellPosition));
                any = true;
            }
            return any;
        }

        private void IssueSupport(int member, VisibleCharacterData enemy)
        {
            assigned[member] = true; goals[member] = -1;
            int target = graph.Index(enemy.CellPosition);
            // 同じセル内では、観測した敵の座標へ寄って攻撃開始範囲へ入る。
            // 別セルでは通常の経路指示を使い、壁越しの直線追跡をしない。
            if (graph.Index(members[member].CurrentCell) == target)
                SXG_SetMemberMoveTargetWorld(member, enemy.WorldPosition);
            else Move(member, target);
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
