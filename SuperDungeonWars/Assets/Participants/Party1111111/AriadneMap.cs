using System.Collections.Generic;
using UnityEngine;

namespace Party1111111
{
    /// <summary>
    /// 公開された既知マップを、最短経路を調べやすい配列へ写したもの。
    /// 未観測セルへは「入口の分かっている次の1マス」としてだけ進める。
    /// 未観測セルを通過して別の場所へ行く経路は作らない。
    /// </summary>
    internal sealed class AriadneMap
    {
        // この順番（北・東・南・西）を、開口ビットと隣接セルの計算で共通に使う。
        private static readonly Vector2Int[] Directions =
            { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
        private static readonly MazeDirection[] Flags =
            { MazeDirection.North, MazeDirection.East, MazeDirection.South, MazeDirection.West };
        private readonly int width, height;
        private readonly bool[] observed;

        // openingsの下位4ビットが各方向の開口。queueはBFSで使い回す探索待ち配列。
        private readonly int[] openings, queue, exitFirst;

        // 最寄りの既知搬出口までの距離（マス）。-1は既知経路なし、0は搬出口そのもの。
        internal readonly int[] ExitDistances;

        // 観測済みの通路から1歩で入れる未観測セル。探索担当の目標候補になる。
        internal readonly List<int> Frontiers = new List<int>();
        internal int Size => width * height;

        internal AriadneMap(int width, int height)
        {
            this.width = width; this.height = height;
            observed = new bool[Size]; openings = new int[Size]; queue = new int[Size];
            ExitDistances = new int[Size]; exitFirst = new int[Size];
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
        internal int DistanceToCenter(int a) => Manhattan(a, (height / 2) * width + width / 2);
        /// <summary>新しく見えたセルを取り込み、探索候補と帰還距離を更新する。</summary>
        internal void Refresh(KnownMapView map)
        {
            // このゲームでは一度分かった通路は変化しないため、観測済みセルは再取得しない。
            for (int i = 0; i < Size; i++)
            {
                if (observed[i]) continue;
                if (!map.TryGetCell(Cell(i), out KnownCellInfo info) || !info.IsObserved) continue;
                observed[i] = true;
                for (int d = 0; d < 4; d++) if (info.IsOpen(Flags[d])) openings[i] |= 1 << d;
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
        // 未観測・通行不能の入口は帰還先として仮定しない。
        private void Seed(int cell, ref int tail)
        {
            if (!observed[cell] || openings[cell] == 0 || ExitDistances[cell] >= 0) return;
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
        /// allowFrontierがtrueの探索では未観測の終点も記録し、帰還では既知セルだけを通す。
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
                    if (observed[next])
                    {
                        // 通路の両側が開いていることを確認する。逆方向は4方向の2つ先。
                        if ((openings[next] & (1 << ((d + 2) % 4))) == 0) continue;
                    }
                    else if (!allowFrontier) continue;
                    distances[next] = distances[current] + 1;
                    // 始点の隣ならそのセル、それ以降は手前のセルが覚えた最初の一歩を引き継ぐ。
                    first[next] = distances[current] == 0 ? next : first[current];

                    // 未観測セルは経路の終点にだけ使い、その先を推測して探索を広げない。
                    if (observed[next]) queue[tail++] = next;
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
