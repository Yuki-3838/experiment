using System.Collections.Generic;
using UnityEngine;
using SuperDungeonWars.AI;

namespace Party0004838
{
    /// <summary>
    /// 2026-10-29。観測地形による経路と、選択済みの搬出口を保つ帰還経路を求める。
    /// 観測地形と汎用C4ビューの回転推定で経路を作る。推定と実測は分けて保持する。
    /// 完全未知のセルは探索の終点にだけ使う。宝や敵の位置を回転コピーしない。
    /// </summary>
    internal sealed class AriadneMap
    {
        // この順番（北・東・南・西）を、開口ビットと隣接セルの計算で共通に使う。
        private static readonly Vector2Int[] Directions =
            { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
        private static readonly MazeDirection[] Flags =
            { MazeDirection.North, MazeDirection.East, MazeDirection.South, MazeDirection.West };
        private readonly int width, height;
        // observedは実測、knownは実測＋回転推定。宝の探索と経路の計画を混同しない。
        private readonly bool[] observed, known;
        internal C4SymmetricMap Terrain { get; }
        // 中央ホールの範囲は公開IsCentralHallから取得する。訪問記録は自部隊の現在セルだけから作る。
        private readonly bool[] centralHall, centralVisited;

        // openingsの下位4ビットが各方向の開口。queueはBFSで使い回す探索待ち配列。
        private readonly int[] openings, queue, exitFirst;

        // 最寄りの既知搬出口までの距離（マス）。-1は既知経路なし、0は搬出口そのもの。
        internal readonly int[] ExitDistances;

        // 観測済みの通路から1歩で入れる未観測セル。探索担当の目標候補になる。
        internal readonly List<int> Frontiers = new List<int>();
        internal int Width => width;
        internal int Height => height;
        internal int Size => width * height;
        internal int CenterCell => (height / 2) * width + width / 2;

        // 危険を含む経路の作業領域。重みは「通常の1歩」と比較する相対コスト。
        // 二分ヒープを使い、全セル同士の比較や毎思考の配列生成を避ける。
        private readonly float[] routeCost, heapCost;
        private readonly int[] routeFirst, routeSteps, heapCell, dangerMark, dangerDepth;
        private int heapCount, dangerGeneration;


        internal AriadneMap(int width, int height)
        {
            this.width = width; this.height = height;
            observed = new bool[Size]; known = new bool[Size];
            Terrain = new C4SymmetricMap(width, height);
            openings = new int[Size]; queue = new int[Size];
            centralHall = new bool[Size]; centralVisited = new bool[Size];
            ExitDistances = new int[Size]; exitFirst = new int[Size];
            routeCost = new float[Size]; routeFirst = new int[Size]; routeSteps = new int[Size];
            heapCell = new int[Size * 4 + 4]; heapCost = new float[heapCell.Length];
            dangerMark = new int[Size]; dangerDepth = new int[Size];

        }
        // 2次元のセル座標と1次元の配列添字を相互に変換する。
        internal int Index(Vector2Int cell) => cell.y * width + cell.x;
        internal Vector2Int Cell(int index) => new Vector2Int(index % width, index / width);

        // マンハッタン距離は直交方向での座標差。壁を考慮した経路長ではない。
        // 担当の分散と中央への誘導に使い、移動の可否は別のBFSで判定する。
        internal int Manhattan(int a, int b)
        {
            Vector2Int ca = Cell(a), cb = Cell(b);
            return Mathf.Abs(ca.x - cb.x) + Mathf.Abs(ca.y - cb.y);
        }
        internal int DistanceToCenter(int a) => Manhattan(a, CenterCell);

        internal void RecordCentralVisit(int cell)
        {
            if (cell >= 0 && cell < Size && centralHall[cell]) centralVisited[cell] = true;
        }

        // 宝が未発見のとき、確認済みの空のホールへ何度も人を戻さないための終了条件。
        // 未観測セルの中身や敵の搬出を推測した判定ではない。
        internal bool CentralSearchComplete
        {
            get
            {
                for (int i = 0; i < Size; i++) if (centralVisited[i]) return true;
                return false;
            }
        }

        /// <summary>既知経路で届く、まだ自部隊が訪れていないホール内のセルを選ぶ。</summary>
        internal int FindCentralSearchTarget(int[] distances)
        {
            int target = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < Size; i++)
            {
                if (!centralHall[i] || centralVisited[i] || distances[i] < 1) continue;
                // 移動1歩を基本に、中心からの距離1マスへ0.25を加える誘導用の相対コスト。
                float score = distances[i] + DistanceToCenter(i) * 0.25f;
                if (score < best) { best = score; target = i; }
            }
            return target;
        }
        /// <summary>新しく見えたセルを取り込み、探索候補と帰還距離を更新する。</summary>
        internal void Refresh(KnownMapView map)
        {
            // 汎用ビューへ自部隊の観測だけを渡す。矛盾したら推定が消えるため毎回上書きする。
            Terrain.Refresh(map);
            for (int i = 0; i < Size; i++)
            {
                Terrain.TryGetCell(Cell(i), out SymmetricCell info);
                observed[i] = info.IsObserved; known[i] = info.IsKnown;
                centralHall[i] = info.IsCentralHall && info.IsWalkable;
                openings[i] = (int)info.OpenDirections;
            }
            // 観測済みセルの開口だけを根拠に、隣の未観測セルを列挙する。
            Frontiers.Clear();
            for (int i = 0; i < Size; i++)
            {
                if (!observed[i]) continue;
                for (int d = 0; d < 4; d++)
                {
                    int next = Neighbor(i, d);
                    if (next >= 0 && !observed[next] && !Frontiers.Contains(next)) Frontiers.Add(next);
                }
            }
            Clear(ExitDistances, exitFirst);
            int tail = 0;
            // ゲーム規則の搬出口は四隅ではなく4辺の中央。
            // 観測できている搬出口を同時に始点にして、最寄りの出口までの距離を求める。
            Seed(width / 2, ref tail); Seed((height - 1) * width + width / 2, ref tail);
            Seed((height / 2) * width, ref tail); Seed((height / 2) * width + width - 1, ref tail);
            Flood(ExitDistances, exitFirst, tail, false);
        }
        // 実測・回転推定のどちらでも接続のない入口は帰還先にしない。
        private void Seed(int cell, ref int tail)
        {
            if (!known[cell] || openings[cell] == 0 || ExitDistances[cell] >= 0) return;
            ExitDistances[cell] = 0; exitFirst[cell] = cell; queue[tail++] = cell;
        }
        /// <summary>
        /// 指定した現在セルから幅優先探索（BFS）を行う。
        /// distancesに最短のマス数、firstにその経路の最初の隣接セルを返す。
        /// </summary>
        internal void Search(int start, int[] distances, int[] first)
        {
            Clear(distances, first);
            if (start < 0 || start >= Size) return;
            distances[start] = 0; first[start] = start; queue[0] = start;
            Flood(distances, first, 1, true);
        }
        // -1を未到達の印にする。距離0の始点と区別するため、0では初期化しない。
        private static void Clear(int[] distances, int[] first)
        {
            for (int i = 0; i < distances.Length; i++) { distances[i] = -1; first[i] = -1; }
        }
        /// <summary>
        /// 各移動のコストを1として近いセルから順に調べる共通処理。
        /// allowFrontierがtrueなら完全未知の終点も記録する。帰還は実測・回転推定の接続だけを通す。
        /// </summary>
        private void Flood(int[] distances, int[] first, int tail, bool allowFrontier)
        {
            int head = 0;
            while (head < tail)
            {
                int current = queue[head++];
                for (int d = 0; d < 4; d++)
                {
                    int next = Neighbor(current, d);
                    if (next < 0 || distances[next] >= 0) continue;
                    if (known[next])
                    {
                        // 通路の両側が開いていることを確認する。逆方向は4方向の2つ先。
                        if ((openings[next] & (1 << ((d + 2) % 4))) == 0) continue;
                    }
                    else if (!allowFrontier) continue;
                    distances[next] = distances[current] + 1;
                    // 始点の隣ならそのセル、それ以降は手前のセルが覚えた最初の一歩を引き継ぐ。
                    first[next] = distances[current] == 0 ? next : first[current];

                    // 実測・回転推定の接続は展開する。完全未知のセルは終点にだけ使う。
                    if (known[next]) queue[tail++] = next;
                }
            }
        }
        // 現在セルの既知の開口と、マップ内に収まる座標だけを使って隣接セルを求める。
        private int Neighbor(int cell, int direction)
        {
            if ((openings[cell] & (1 << direction)) == 0) return -1;
            Vector2Int p = Cell(cell) + Directions[direction];
            return p.x >= 0 && p.x < width && p.y >= 0 && p.y < height ? Index(p) : -1;
        }

        /// <summary>
        /// 見えている攻撃可能な敵から、既知通路で2歩以内へ危険を加える。
        /// 同じ敵から同じセルへは一度だけ加算し、壁を越えて危険を広げない。
        /// </summary>
        internal void AddObservedDanger(float[] danger, int enemyCell)
        {
            if (enemyCell < 0 || enemyCell >= Size || !observed[enemyCell]) return;
            if (++dangerGeneration == int.MaxValue)
            {
                System.Array.Clear(dangerMark, 0, dangerMark.Length);
                dangerGeneration = 1;
            }
            int head = 0, tail = 1;
            queue[0] = enemyCell; dangerMark[enemyCell] = dangerGeneration; dangerDepth[enemyCell] = 0;
            while (head < tail)
            {
                int current = queue[head++], depth = dangerDepth[current];
                // 同じセル6・隣接3・2歩先1は、接敵を避けるための追加マス相当コスト。
                // 隠れた敵や攻撃のクールダウンは推測せず、観測済みの近傍だけを使う。
                danger[current] += depth == 0 ? 6f : depth == 1 ? 3f : 1f;
                if (depth == 2) continue;
                for (int d = 0; d < 4; d++)
                {
                    int next = KnownNeighbor(current, d);
                    if (next < 0 || dangerMark[next] == dangerGeneration) continue;
                    dangerMark[next] = dangerGeneration; dangerDepth[next] = depth + 1; queue[tail++] = next;
                }
            }
        }

        /// <summary>
        /// 運搬者だけに、距離と可視の敵への接近コストを合計した帰還経路を求める。
        /// 再計画時点の最短帰還より6歩以内で、残り時間内に搬出できる見積もりの経路だけを許す。
        /// 制約内で候補を得られなければ、呼び出し元が従来の最短帰還へ戻す。
        /// </summary>
        internal bool TrySaferReturn(int start, int[] shortest, float[] danger, int previousExit,
            float secondsLeft, out int nextStep, out int chosenExit)
        {
            nextStep = chosenExit = -1;
            int nearest = ClosestExit(shortest);
            if (nearest < 0 || start < 0 || start >= Size || !known[start] || shortest[nearest] == 0) return false;
            int stepLimit = shortest[nearest] + 6;
            // 各セルに最小コストの候補を1つ保持する近似探索。
            // 歩数制約下のあらゆる経路を保持する方式ではなく、候補がなければ最短帰還に戻す。
            for (int i = 0; i < Size; i++)
            { routeCost[i] = float.PositiveInfinity; routeFirst[i] = -1; routeSteps[i] = int.MaxValue; }
            heapCount = 0; routeCost[start] = 0; routeFirst[start] = start; routeSteps[start] = 0;
            PushRoute(start, 0);
            while (heapCount > 0)
            {
                PopRoute(out int current, out float cost);
                if (cost != routeCost[current] || routeSteps[current] >= stepLimit) continue;
                for (int d = 0; d < 4; d++)
                {
                    int next = KnownNeighbor(current, d);
                    if (next < 0) continue;
                    float candidate = cost + 1f + danger[next];
                    if (candidate >= routeCost[next]) continue;
                    routeCost[next] = candidate; routeSteps[next] = routeSteps[current] + 1;
                    routeFirst[next] = current == start ? next : routeFirst[current];
                    PushRoute(next, candidate);
                }
            }
            float best = float.PositiveInfinity;
            for (int i = 0; i < Size; i++)
            {
                if (ExitDistances[i] != 0 || routeFirst[i] < 0 || routeSteps[i] > stepLimit) continue;
                if (previousExit >= 0 && i != previousExit) continue;
                // 運搬1.05秒/歩と投げ動作等の1.5秒。間に合わない遠回りは採用しない。
                if (routeSteps[i] * 1.05f + 1.5f > secondsLeft) continue;
                // 選択済みの搬出口を維持し、軽微な危険変化で別の入口へ折り返さない。
                float score = routeCost[i] * (i == previousExit ? 0.9f : 1f);
                if (score >= best) continue;
                best = score; chosenExit = i; nextStep = routeFirst[i];
            }
            return nextStep >= 0;
        }

        // 帰還では両端が既知で、両方の開口が一致した隣接セルだけを通す。
        private int KnownNeighbor(int current, int direction)
        {
            int next = Neighbor(current, direction);
            return next >= 0 && known[next]
                && (openings[next] & (1 << ((direction + 2) % 4))) != 0 ? next : -1;
        }

        // 小さいコストを先に取り出す二分ヒープ。古い値は取り出し時に捨てる。
        private void PushRoute(int cell, float cost)
        {
            int index = heapCount++;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (heapCost[parent] <= cost) break;
                heapCell[index] = heapCell[parent]; heapCost[index] = heapCost[parent]; index = parent;
            }
            heapCell[index] = cell; heapCost[index] = cost;
        }

        private void PopRoute(out int cell, out float cost)
        {
            cell = heapCell[0]; cost = heapCost[0];
            int lastCell = heapCell[--heapCount]; float lastCost = heapCost[heapCount];
            if (heapCount == 0) return;
            int index = 0;
            while (index * 2 + 1 < heapCount)
            {
                int child = index * 2 + 1;
                if (child + 1 < heapCount && heapCost[child + 1] < heapCost[child]) child++;
                if (heapCost[child] >= lastCost) break;
                heapCell[index] = heapCell[child]; heapCost[index] = heapCost[child]; index = child;
            }
            heapCell[index] = lastCell; heapCost[index] = lastCost;
        }

        /// <summary>このメンバーが到達できる既知搬出口のうち、最短のものを返す。</summary>
        internal int ClosestExit(int[] distances)
        {
            int result = -1, best = int.MaxValue;
            for (int i = 0; i < Size; i++)
                if (ExitDistances[i] == 0 && distances[i] >= 0 && distances[i] < best)
                { best = distances[i]; result = i; }
            return result;
        }
        /// <summary>
        /// 目標セル周囲3×3の未観測セル数を数え、探索の収穫の目安にする。
        /// 壁越しに見えると保証する値ではなく、移動経路の判定にも使わない。
        /// </summary>
        internal int InformationGain(int cell)
        {
            int count = 0;
            Vector2Int p = Cell(cell);
            for (int y = Mathf.Max(0, p.y - 1); y <= Mathf.Min(height - 1, p.y + 1); y++)
                for (int x = Mathf.Max(0, p.x - 1); x <= Mathf.Min(width - 1, p.x + 1); x++)
                    if (!observed[y * width + x]) count++;
            return count;
        }
    }
}
