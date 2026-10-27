using System.Collections.Generic;
using UnityEngine;

namespace Party0004838
{
    /// <summary>
    /// 追加強化3（2026-10-26）。中央の金宝箱へ1人を優先し、残りで近場を回収する。効果は未測定。
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

        // pickupIdsは回収する宝そのもののID。セルだけでなくIDも保持し、同じセルの別の宝へ向かわない。
        // longJobsは長い回収を始めた担当。運搬中も遠征枠を保持し、同時に遠征へ出す人数を制限する。
        private int[] pickupIds;
        private bool[] longJobs;
        private enum CollectionKind { Nearby, ShortTrip, LongerTrip }
        // 中央担当のメンバー番号と、金宝箱が最後に見えたときの運搬状態。
        // 運搬中と観測した後は、落ちた金宝箱を再観測するまで空の中央へ向かわない。
        private int centralRunner = -1;
        private bool centralCarried;

        // 戦闘支援は同時に1人まで。継続時間と再開待ちの単位はゲーム内秒。
        private int tacticalMember = -1, tacticalEnemy = -1;
        private float tacticalUntil, tacticalRestUntil;

        // Unityのゲーム内時刻（秒）。一時停止やゲーム速度の変更に追従する。
        private float startedAt, nextThink;

        // Valueは実際の獲得点ではなく、宝箱の種類から見積もった相対的な価値。
        private sealed class TreasureMemory
        {
            internal int Id, Cell;
            internal Vector3 Position;
            internal float Value, SeenAt;
            internal bool Central;
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
                pickupIds = new int[count]; longJobs = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    distances[i] = new int[graph.Size]; firstSteps[i] = new int[graph.Size]; goals[i] = -1; returnExits[i] = -1;
                    pickupIds[i] = -1;
                }
            }
            // 仲間の誰かが新しく観測した通路を取り込み、帰還距離も更新する。
            graph.Refresh(map);
            reserved.Clear();
            for (int i = 0; i < members.Length; i++)
            {
                SXG_TryGetMemberInfo(i, out members[i]);
                if (members[i] != null && !members[i].IsKnockedOut)
                    graph.RecordCentralVisit(graph.Index(members[i].CurrentCell));
                // 搬出やKOで手ぶらになったら前回の出口への優遇も終了し、次の運搬へ引き継がない。
                if (members[i] != null && !members[i].HasTreasure) returnExits[i] = -1;
                if (members[i] != null && members[i].IsKnockedOut)
                { pickupIds[i] = -1; longJobs[i] = false; }
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
                pickupIds[i] = -1;
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

            // 探索と宝の回収を同じ点数で競わせず、中央担当と近場回収を探索より先に確保する。
            // 投げ動作などでロック中の運搬者・回収担当も、すでに働いている人数として数える。
            int collectors = 0;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] == null) continue;
                if (!members[i].HasTreasure && pickupIds[i] >= 0 && !treasures.ContainsKey(pickupIds[i]))
                { pickupIds[i] = -1; longJobs[i] = false; }
                if (members[i].HasTreasure || (assigned[i] && pickupIds[i] >= 0)) collectors++;
                if (!members[i].HasTreasure && assigned[i]
                    && treasures.TryGetValue(pickupIds[i], out TreasureMemory pendingPickup))
                    reserved.Add(pendingPickup.Cell);
            }
            // 金宝箱は通常の距離・効率比較へ埋もれさせず、手ぶらの中央担当を先に1人確保する。
            AssignCentralObjective(ref collectors);
            // 中央担当以外は、4歩以内かつ回収・搬出24秒以内の宝を探索より先に確保する。
            AssignCollections(CollectionKind.Nearby, members.Length, ref collectors);
            // それ以外の短い回収は、通常1人を探索等へ残す。近場回収で全員埋まった場合は上書きしない。
            int collectorLimit = Mathf.Max(1, members.Length - 1);
            AssignCollections(CollectionKind.ShortTrip, collectorLimit, ref collectors);
            // 中央担当がいる間は通常宝への新しい遠征を出さない。運搬中の仕事は先に確保済み。
            if (centralRunner < 0 && !HasLongCollection())
                AssignCollections(CollectionKind.LongerTrip, collectorLimit, ref collectors);
            AssignSupport(visibleCharacters);

            // 回収・搬出・支援の担当を確保した後、残った仲間だけへ探索を割り当てる。
            for (int round = 0; round < members.Length; round++)
            {
                float best = float.NegativeInfinity;
                int member = -1, target = -1;
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
                pickupIds[member] = -1; longJobs[member] = false;
                Move(member, target);
            }
            for (int i = 0; i < members.Length; i++)
            {
                if (assigned[i]) continue;
                pickupIds[i] = -1; longJobs[i] = false;
                // 一本道では探索先が人数より少ないことがある。
                // その場合だけ担当の重複を許し、近い探索先へ進んで次の分岐を待つ。
                int target = -1, length = int.MaxValue;
                foreach (int frontier in graph.Frontiers)
                    if (distances[i][frontier] > 0 && distances[i][frontier] < length)
                    { length = distances[i][frontier]; target = frontier; }
                goals[i] = target; assigned[i] = true;
                if (target >= 0) Move(i, target); else SXG_StopMember(i);
            }
        }

        /// <summary>
        /// 金宝箱が取れる既知経路を優先し、未接続なら1人だけ中央へ向けて開拓する。
        /// 運搬者の仕事を奪わず、ほかの仲間による近場回収を継続する。
        /// </summary>
        private void AssignCentralObjective(ref int collectors)
        {
            int previous = centralRunner;
            centralRunner = -1;
            if (tactics.CentralExported || centralCarried) return;
            TreasureMemory gold = null;
            foreach (TreasureMemory treasure in treasures.Values)
                if (treasure.Central) { gold = treasure; break; }

            // ロック中でも、すでに金宝箱へ向かっていた担当を重ねて派遣しない。
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i] == null || members[i].IsKnockedOut || members[i].HasTreasure || !assigned[i]) continue;
                if (gold != null && pickupIds[i] == gold.Id)
                { SetCentralRunner(i); return; }
            }

            int runner = -1;
            float best = float.PositiveInfinity;
            bool knownTrip = false;
            if (gold != null)
            {
                if (gold.Cell < 0 || gold.Cell >= graph.Size || reserved.Contains(gold.Cell)) return;
                for (int i = 0; i < members.Length; i++)
                {
                    if (assigned[i]) continue;
                    if (distances[i][gold.Cell] >= 0 && graph.ExitDistances[gold.Cell] >= 0) knownTrip = true;
                    if (!TryCollectionTime(i, gold, out _, out float seconds)) continue;
                    // 前回の担当を15%優遇。比較用の秒数だけを割り引き、残り時間の判定は実見積もりで行う。
                    float score = seconds * (i == previous ? 0.85f : 1f);
                    if (score < best) { best = score; runner = i; }
                }
                if (runner >= 0)
                {
                    // 金宝箱は通常宝の24秒・45秒制限の例外。ただし制限時間内の搬出見積もりは必須。
                    pickupIds[runner] = gold.Id; longJobs[runner] = true;
                    goals[runner] = gold.Cell; assigned[runner] = true;
                    reserved.Add(gold.Cell); collectors++;
                    SetCentralRunner(runner); Move(runner, gold.Cell);
                    return;
                }
                // 経路が分かっていて搬出に間に合わない場合、探索扱いで無理に出発させない。
                if (knownTrip) return;
            }

            // 未知の経路を探す段階は残り30秒まで。帰還距離が不明な終盤の遠征を抑える。
            // ホールを全て訪問済みでも、別の場所に落ちた金宝箱を観測した場合はその位置を目指せる。
            if (120f - (Time.time - startedAt) <= 30f || (gold == null && graph.CentralSearchComplete)) return;
            int focus = gold != null ? gold.Cell : graph.CenterCell;
            int target = -1;
            best = float.PositiveInfinity;
            for (int i = 0; i < members.Length; i++)
            {
                if (assigned[i]) continue;
                int hallTarget = gold == null ? graph.FindCentralSearchTarget(distances[i]) : -1;
                // ホールへ接続済みなら内部の未訪問セルを確認し、金宝箱の観測を狙う。
                if (hallTarget >= 0 && !reserved.Contains(hallTarget))
                {
                    float score = distances[i][hallTarget] * (i == previous ? 0.85f : 1f);
                    if (score < best) { best = score; runner = i; target = hallTarget; }
                    continue;
                }
                foreach (int frontier in graph.Frontiers)
                {
                    int steps = distances[i][frontier];
                    if (steps < 1 || reserved.Contains(frontier)) continue;
                    // 既知経路1歩に0.65、中心または観測した宝への座標差1マスに1の相対コスト。
                    // 座標差は方向付けだけに使い、壁や未知通路を通れるとは仮定しない。
                    float score = (steps * 0.65f + graph.Manhattan(frontier, focus)) * (i == previous ? 0.85f : 1f);
                    if (score < best) { best = score; runner = i; target = frontier; }
                }
            }
            if (target < 0) return;
            pickupIds[runner] = -1; longJobs[runner] = false;
            goals[runner] = target; assigned[runner] = true; reserved.Add(target);
            SetCentralRunner(runner); Move(runner, target);
        }

        private void SetCentralRunner(int member)
        {
            centralRunner = member;
            tactics.SetCentralRunner(members[member].CharacterId);
        }

        /// <summary>
        /// 近場、短い一往復、長い一往復を別々に割り当てる。
        /// 近場は到着の早さを優先し、それ以外は相対価値/回収から搬出までの秒数で比較する。
        /// </summary>
        private void AssignCollections(CollectionKind kind, int limit, ref int collectors)
        {
            for (int round = 0; round < members.Length && collectors < limit; round++)
            {
                int bestMember = -1, bestDistance = int.MaxValue;
                float bestSeconds = float.PositiveInfinity, bestScore = float.NegativeInfinity;
                TreasureMemory bestTreasure = null;
                foreach (TreasureMemory treasure in treasures.Values)
                {
                    if (reserved.Contains(treasure.Cell)) continue;
                    for (int i = 0; i < members.Length; i++)
                    {
                        if (assigned[i] || !TryCollectionTime(i, treasure, out int pickupDistance, out float seconds)) continue;
                        // 通常移動12歩（概算8.64秒）・全工程24秒以内を短い仕事とする。
                        // 金宝箱の専任担当を先に確保し、ここでは残った回収候補を扱う。
                        bool shortTrip = pickupDistance <= 12 && seconds <= 24f;
                        if (kind == CollectionKind.Nearby && (pickupDistance > 4 || !shortTrip)) continue;
                        if (kind == CollectionKind.ShortTrip && !shortTrip) continue;
                        // 45秒を超える一往復へ新規に出さず、他の仲間の観測で短い経路が判明するのを待つ。
                        if (kind == CollectionKind.LongerTrip && (shortTrip || seconds > 45f)) continue;
                        float score = treasure.Value / seconds;
                        // 近場以外では同じ宝の担当を15%優遇し、僅かな効率差で引き返すのを抑える。
                        if (pickupIds[i] == treasure.Id) score *= 1.15f;
                        bool better = kind == CollectionKind.Nearby
                            ? pickupDistance < bestDistance || (pickupDistance == bestDistance && seconds < bestSeconds)
                            : score > bestScore;
                        if (!better) continue;
                        bestMember = i; bestTreasure = treasure; bestDistance = pickupDistance;
                        bestSeconds = seconds; bestScore = score;
                    }
                }
                if (bestMember < 0) return;
                // 遠征が途中で「短い仕事」の距離に入っても、同じ宝の運搬完了まで遠征枠を保持する。
                bool continuingLong = longJobs[bestMember] && pickupIds[bestMember] == bestTreasure.Id;
                longJobs[bestMember] = kind == CollectionKind.LongerTrip || continuingLong;
                pickupIds[bestMember] = bestTreasure.Id;
                goals[bestMember] = bestTreasure.Cell; assigned[bestMember] = true;
                reserved.Add(bestTreasure.Cell); collectors++;
                Move(bestMember, bestTreasure.Cell);
                if (kind == CollectionKind.LongerTrip) return;
            }
        }

        /// <summary>公開マップの実経路で、回収と搬出を終えるまでの時間を秒で概算する。</summary>
        private bool TryCollectionTime(int member, TreasureMemory treasure, out int pickupDistance, out float seconds)
        {
            pickupDistance = -1; seconds = float.PositiveInfinity;
            if (treasure.Cell < 0 || treasure.Cell >= graph.Size) return false;
            pickupDistance = distances[member][treasure.Cell];
            int back = graph.ExitDistances[treasure.Cell];
            if (pickupDistance < 0 || back < 0) return false;
            // セル間隔2・通常速度3・運搬速度2。小さな移動余裕と動作ロック等の3秒を含む。
            // 衝突、戦闘、危険回避の寄り道まで予測するものではない。
            seconds = pickupDistance * 0.72f + back * 1.05f + 3f;
            return seconds <= 120f - (Time.time - startedAt);
        }

        private bool HasLongCollection()
        {
            for (int i = 0; i < members.Length; i++)
                if (members[i] != null && longJobs[i]
                    && (members[i].HasTreasure || (assigned[i] && pickupIds[i] >= 0))) return true;
            return false;
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
            pickupIds[member] = -1; longJobs[member] = false;
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
                if (t.TreasureType == TreasureType.CentralChest) centralCarried = t.IsCarried;
                // 所有者にかかわらず、運ばれている宝は落ちている宝の回収候補に含めない。
                if (t.IsCarried) { treasures.Remove(t.TreasureId); continue; }

                // 通常宝箱の平均100点を1とし、中央宝箱の平均270点を2.7として扱う。
                // 実際の点数を非公開のゲーム状態から取得することはしない。
                treasures[t.TreasureId] = new TreasureMemory
                {
                    Id = t.TreasureId, Cell = graph.Index(t.CellPosition), Position = t.WorldPosition,
                    Value = t.TreasureType == TreasureType.CentralChest ? 2.7f : 1f, SeenAt = Time.time,
                    Central = t.TreasureType == TreasureType.CentralChest
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
            if (!members[member].HasTreasure && treasures.TryGetValue(pickupIds[member], out TreasureMemory treasure)
                && treasure.Cell == target)
            { SXG_SetMemberMoveTargetWorld(member, treasure.Position); return; }
            SXG_StopMember(member);
        }
    }
}
