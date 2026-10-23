using System.Collections.Generic;
using UnityEngine;

namespace Party1111111
{
    // Unknown cells may terminate a path; they are never intermediate nodes.
    internal sealed class AriadneMap
    {
        private static readonly Vector2Int[] Directions =
            { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
        private static readonly MazeDirection[] Flags =
            { MazeDirection.North, MazeDirection.East, MazeDirection.South, MazeDirection.West };
        private readonly int width, height;
        private readonly bool[] observed;
        private readonly int[] openings, queue, exitFirst;
        internal readonly int[] ExitDistances;
        internal readonly List<int> Frontiers = new List<int>();
        internal int Size => width * height;

        internal AriadneMap(int width, int height)
        {
            this.width = width; this.height = height;
            observed = new bool[Size]; openings = new int[Size]; queue = new int[Size];
            ExitDistances = new int[Size]; exitFirst = new int[Size];
        }
        internal int Index(Vector2Int cell) => cell.y * width + cell.x;
        internal Vector2Int Cell(int index) => new Vector2Int(index % width, index / width);
        internal int Manhattan(int a, int b)
        {
            Vector2Int ca = Cell(a), cb = Cell(b);
            return Mathf.Abs(ca.x - cb.x) + Mathf.Abs(ca.y - cb.y);
        }
        internal int DistanceToCenter(int a) => Manhattan(a, (height / 2) * width + width / 2);
        internal void Refresh(KnownMapView map)
        {
            for (int i = 0; i < Size; i++)
            {
                if (observed[i]) continue;
                if (!map.TryGetCell(Cell(i), out KnownCellInfo info) || !info.IsObserved) continue;
                observed[i] = true;
                for (int d = 0; d < 4; d++) if (info.IsOpen(Flags[d])) openings[i] |= 1 << d;
            }
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
            // Four edge-center entrances are specified in the game rules.
            Seed(width / 2, ref tail); Seed((height - 1) * width + width / 2, ref tail);
            Seed((height / 2) * width, ref tail); Seed((height / 2) * width + width - 1, ref tail);
            Flood(ExitDistances, exitFirst, tail, false);
        }
        private void Seed(int cell, ref int tail)
        {
            if (!observed[cell] || openings[cell] == 0 || ExitDistances[cell] >= 0) return;
            ExitDistances[cell] = 0; exitFirst[cell] = cell; queue[tail++] = cell;
        }
        internal void Search(int start, int[] distances, int[] first)
        {
            Clear(distances, first);
            if (start < 0 || start >= Size) return;
            distances[start] = 0; first[start] = start; queue[0] = start;
            Flood(distances, first, 1, true);
        }
        private static void Clear(int[] distances, int[] first)
        {
            for (int i = 0; i < distances.Length; i++) { distances[i] = -1; first[i] = -1; }
        }
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
                        if ((openings[next] & (1 << ((d + 2) % 4))) == 0) continue;
                    }
                    else if (!allowFrontier) continue;
                    distances[next] = distances[current] + 1;
                    first[next] = distances[current] == 0 ? next : first[current];
                    if (observed[next]) queue[tail++] = next;
                }
            }
        }
        private int Neighbor(int cell, int direction)
        {
            if ((openings[cell] & (1 << direction)) == 0) return -1;
            Vector2Int p = Cell(cell) + Directions[direction];
            return p.x >= 0 && p.x < width && p.y >= 0 && p.y < height ? Index(p) : -1;
        }
        internal int ClosestExit(int[] distances)
        {
            int result = -1, best = int.MaxValue;
            for (int i = 0; i < Size; i++)
                if (ExitDistances[i] == 0 && distances[i] >= 0 && distances[i] < best)
                { best = distances[i]; result = i; }
            return result;
        }
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
