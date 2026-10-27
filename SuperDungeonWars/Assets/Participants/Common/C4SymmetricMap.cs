using System;
using UnityEngine;

namespace SuperDungeonWars.AI
{
    /// <summary>地形の取得元。推定を実際の観測と区別し、宝や敵の発見には使わない。</summary>
    public enum TerrainKnowledge { Unknown, Observed, Inferred }

    /// <summary>公開観測、またはその90度回転から作った地形。部屋IDは観測した場所だけで有効。</summary>
    public readonly struct SymmetricCell
    {
        public Vector2Int CellPosition { get; }
        public TerrainKnowledge Knowledge { get; }
        public MazeDirection OpenDirections { get; }
        public bool IsRoom { get; }
        public bool IsCentralHall { get; }
        public int RoomId { get; }
        public Vector2Int SourceCell { get; }
        public bool IsObserved => Knowledge == TerrainKnowledge.Observed;
        public bool IsKnown => Knowledge != TerrainKnowledge.Unknown;
        public bool IsWalkable => IsKnown && OpenDirections != MazeDirection.None;
        public bool IsOpen(MazeDirection direction) => (OpenDirections & direction) != 0;

        internal SymmetricCell(Vector2Int cell, TerrainKnowledge knowledge, MazeDirection openings,
            bool room, bool centralHall, int roomId, Vector2Int source)
        {
            CellPosition = cell; Knowledge = knowledge; OpenDirections = openings;
            IsRoom = room; IsCentralHall = centralHall; RoomId = roomId; SourceCell = source;
        }
    }

    /// <summary>
    /// 任意の参加者AIから使える回転対称地形ビュー。GameObjectへのアタッチは不要。
    /// 各Partyが1つ所有し、Refreshへ自部隊の公開KnownMapViewだけを渡す。
    /// 非公開マップ・シード・敵・宝箱を取得せず、元の観測マップも書き換えない。
    /// </summary>
    public sealed class C4SymmetricMap
    {
        private static readonly Vector2Int[] Directions =
            { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
        private static readonly MazeDirection[] Flags =
            { MazeDirection.North, MazeDirection.East, MazeDirection.South, MazeDirection.West };
        private readonly bool[] observed, actualRoom, actualHall, room, hall;
        private readonly MazeDirection[] actualOpen, openings;
        private readonly TerrainKnowledge[] knowledge;
        private readonly int[] roomIds, sources, queue, parents;
        private bool dirty = true, inferenceRequested;

        public int Width { get; }
        public int Height { get; }
        public int Size => knowledge.Length;
        public int ObservedCellCount { get; private set; }
        public int InferredCellCount { get; private set; }
        public int Revision { get; private set; }
        // 1つでも実測と矛盾したら、そのインスタンスでは以後すべての推定を無効にする。
        // 対称でない設定を相手に、誤った経路へ繰り返し進むことを避けるため。
        public bool HasSymmetryConflict { get; private set; }
        public bool InferenceActive => inferenceRequested && Width == Height && (Width & 1) == 1 && !HasSymmetryConflict;

        public C4SymmetricMap(int width, int height, bool inferQuarterTurns = true)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width; Height = height; inferenceRequested = inferQuarterTurns;
            int size = checked(width * height);
            observed = new bool[size]; actualRoom = new bool[size]; actualHall = new bool[size];
            room = new bool[size]; hall = new bool[size];
            actualOpen = new MazeDirection[size]; openings = new MazeDirection[size];
            knowledge = new TerrainKnowledge[size]; roomIds = new int[size]; sources = new int[size];
            queue = new int[size]; parents = new int[size];
        }

        /// <summary>非対称ステージ等ではOFFにできる。変更は次のRefreshで反映する。</summary>
        public void SetInferenceEnabled(bool enabled)
        { if (inferenceRequested != enabled) { inferenceRequested = enabled; dirty = true; } }

        /// <summary>
        /// 新しい観測だけを取り込む。配布版の地形は試合中に不変であることが前提。
        /// 新しい試合・変更される地形ではインスタンスを作り直す。
        /// </summary>
        public bool Refresh(KnownMapView map)
        {
            if (map == null) return false;
            if (map.Width != Width || map.Height != Height) throw new ArgumentException("Map dimensions changed.", nameof(map));
            for (int i = 0; i < Size; i++)
            {
                if (observed[i] || !map.TryGetCell(Cell(i), out KnownCellInfo info) || !info.IsObserved) continue;
                observed[i] = true; actualOpen[i] = info.KnownOpenDirections;
                actualRoom[i] = info.IsRoom; actualHall[i] = info.IsCentralHall; roomIds[i] = info.RoomId;
                ObservedCellCount++; dirty = true;
            }
            if (!dirty) return false;
            dirty = false; Revision++;
            RestoreObserved();
            if (!InferenceActive) return true;
            for (int source = 0; source < Size; source++)
            {
                if (!observed[source]) continue;
                Vector2Int cell = Cell(source);
                MazeDirection rotated = actualOpen[source];
                for (int turn = 1; turn <= 3; turn++)
                {
                    cell = new Vector2Int(cell.y, Width - 1 - cell.x);
                    rotated = RotateDirectionsClockwise(rotated);
                    int target = Index(cell);
                    if (observed[target])
                    {
                        if (actualOpen[target] == rotated && actualRoom[target] == actualRoom[source]
                            && actualHall[target] == actualHall[source]) continue;
                        HasSymmetryConflict = true;
                        RestoreObserved();
                        return true;
                    }
                    knowledge[target] = TerrainKnowledge.Inferred; openings[target] = rotated;
                    room[target] = actualRoom[source]; hall[target] = actualHall[source]; sources[target] = source;
                }
            }
            for (int i = 0; i < Size; i++) if (knowledge[i] == TerrainKnowledge.Inferred) InferredCellCount++;
            return true;
        }

        private void RestoreObserved()
        {
            InferredCellCount = 0;
            for (int i = 0; i < Size; i++)
            {
                knowledge[i] = observed[i] ? TerrainKnowledge.Observed : TerrainKnowledge.Unknown;
                openings[i] = actualOpen[i]; room[i] = actualRoom[i]; hall[i] = actualHall[i]; sources[i] = i;
            }
        }

        public bool IsInside(Vector2Int cell) => cell.x >= 0 && cell.x < Width && cell.y >= 0 && cell.y < Height;
        public bool IsObserved(Vector2Int cell) => IsInside(cell) && observed[Index(cell)];
        public bool IsKnown(Vector2Int cell) => IsInside(cell) && knowledge[Index(cell)] != TerrainKnowledge.Unknown;
        public bool IsWalkable(Vector2Int cell) => IsKnown(cell) && openings[Index(cell)] != MazeDirection.None;

        public bool TryGetCell(Vector2Int cell, out SymmetricCell info)
        {
            info = default;
            if (!IsInside(cell)) return false;
            int i = Index(cell);
            info = new SymmetricCell(cell, knowledge[i], openings[i], room[i], hall[i],
                observed[i] ? roomIds[i] : -1, Cell(sources[i]));
            return true;
        }

        /// <summary>上下左右の隣接セル間で、実測または回転推定の両側の開口を確認する。</summary>
        public bool CanMove(Vector2Int from, Vector2Int to)
        {
            int direction = DirectionIndex(to - from);
            return direction >= 0 && IsWalkable(from) && IsWalkable(to)
                && (openings[Index(from)] & Flags[direction]) != 0
                && (openings[Index(to)] & Flags[(direction + 2) % 4]) != 0;
        }

        /// <summary>移動元の開口を使い、完全に未知の隣接セルも探索の終点として返せる。</summary>
        public bool TryGetOpenNeighbor(Vector2Int from, Vector2Int direction, out Vector2Int neighbor)
        {
            neighbor = from + direction;
            int d = DirectionIndex(direction);
            return d >= 0 && IsKnown(from) && IsInside(neighbor) && (openings[Index(from)] & Flags[d]) != 0;
        }

        /// <summary>
        /// 最短経路の最初の1マスを返す汎用BFS。推定セルも通れるが、敵や宝の存在は仮定しない。
        /// allowUnknownGoalがtrueの時だけ、完全未知の目標を最後の1セルとして許可する。
        /// 配列は使い回す。同じインスタンスへの並列呼び出しは行わない。
        /// </summary>
        public bool TryGetNextStep(Vector2Int start, Vector2Int goal, out Vector2Int next, bool allowUnknownGoal = false)
        {
            next = start;
            if (!IsWalkable(start) || !IsInside(goal)) return false;
            if (start == goal) return true;
            for (int i = 0; i < Size; i++) parents[i] = -1;
            int first = Index(start), destination = Index(goal), head = 0, tail = 1;
            queue[0] = first; parents[first] = first;
            while (head < tail && parents[destination] < 0)
            {
                int current = queue[head++]; Vector2Int cell = Cell(current);
                for (int d = 0; d < 4; d++)
                {
                    Vector2Int candidate = cell + Directions[d];
                    if (!IsInside(candidate)) continue;
                    int target = Index(candidate);
                    if (parents[target] >= 0) continue;
                    bool unknownGoal = target == destination && allowUnknownGoal && !IsKnown(candidate)
                        && (openings[current] & Flags[d]) != 0;
                    if (!unknownGoal && !CanMove(cell, candidate)) continue;
                    parents[target] = current;
                    if (target != destination) queue[tail++] = target;
                }
            }
            if (parents[destination] < 0) return false;
            int step = destination;
            while (parents[step] != first) step = parents[step];
            next = Cell(step); return true;
        }

        // セルのyは北向きに増える。時計回り90度では北の開口を東へ回す。
        public static Vector2Int RotateClockwise(Vector2Int cell, int size, int turns = 1)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            turns = ((turns % 4) + 4) % 4;
            for (int i = 0; i < turns; i++) cell = new Vector2Int(cell.y, size - 1 - cell.x);
            return cell;
        }

        public static MazeDirection RotateDirectionsClockwise(MazeDirection openings, int turns = 1)
        {
            int bits = (int)openings & 15; turns = ((turns % 4) + 4) % 4;
            for (int i = 0; i < turns; i++) bits = ((bits << 1) | (bits >> 3)) & 15;
            return (MazeDirection)bits;
        }
        private int Index(Vector2Int cell) => cell.y * Width + cell.x;
        private Vector2Int Cell(int index) => new Vector2Int(index % Width, index / Width);
        private static int DirectionIndex(Vector2Int delta)
        { for (int i = 0; i < 4; i++) if (Directions[i] == delta) return i; return -1; }
    }
}
