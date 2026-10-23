using System.Collections.Generic;
using UnityEngine;

namespace Party1111111
{
    // Ariadne, day 1. Decisions use only the public, observed participant view.
    public sealed class Party1111111Party : ComPartyBase
    {
        private readonly Dictionary<int, TreasureMemory> treasures = new Dictionary<int, TreasureMemory>();
        private readonly List<int> expired = new List<int>(), reserved = new List<int>();
        private AriadneMap graph;
        private PartyMemberInfo[] members;
        private int[][] distances, firstSteps;
        private int[] goals;
        private bool[] assigned;
        private float startedAt, nextThink;

        private sealed class TreasureMemory
        {
            internal int Cell;
            internal Vector3 Position;
            internal float Value, SeenAt;
        }

        protected override void SXG_OnPartyThink()
        {
            if (Time.time < nextThink) return;
            nextThink = Time.time + 0.12f;
            KnownMapView map = SXG_GetKnownMap();
            if (map == null) return;
            if (graph == null)
            {
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
            graph.Refresh(map);
            reserved.Clear();
            for (int i = 0; i < members.Length; i++)
            {
                SXG_TryGetMemberInfo(i, out members[i]);
                assigned[i] = members[i] == null || members[i].IsKnockedOut || members[i].IsActionLocked;
                if (assigned[i]) continue;
                graph.Search(graph.Index(members[i].CurrentCell), distances[i], firstSteps[i]);
                if (!members[i].HasTreasure) continue;
                assigned[i] = true;
                int exit = graph.ClosestExit(distances[i]);
                if (exit >= 0) Move(i, exit); else SXG_StopMember(i);
                goals[i] = -1;
            }
            RememberTreasures();
            // Choose the best member/job pair first, reserving each chest once.
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
                        if (d < 0 || back < 0) continue;
                        if (d * 0.72f + back * 1.05f + 3f > 120f - (Time.time - startedAt)) continue;
                        float score = 12f * t.Value / (4f + d + back * 1.4f);
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
                        float score = (2.5f + graph.InformationGain(frontier) * 0.25f) / (d + 2f);
                        score *= 1f + 0.25f / (1f + graph.DistanceToCenter(frontier) * 0.1f);
                        foreach (int other in reserved)
                            if (graph.Manhattan(frontier, other) <= 3) score *= 0.25f;
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
                // A narrow corridor may have fewer frontiers than scouts.
                int target = -1, length = int.MaxValue;
                foreach (int frontier in graph.Frontiers)
                    if (distances[i][frontier] > 0 && distances[i][frontier] < length)
                    { length = distances[i][frontier]; target = frontier; }
                if (target >= 0) Move(i, target); else SXG_StopMember(i);
            }
        }

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
                foreach (PartyMemberInfo m in members)
                    if (m != null && graph.Manhattan(graph.Index(m.CurrentCell), pair.Value.Cell) <= 1)
                        checkedCell = true;
                if (checkedCell || Time.time - pair.Value.SeenAt > 45f) expired.Add(pair.Key);
            }
            foreach (int id in expired) treasures.Remove(id);
            for (int i = 0; i < visible.Count; i++)
            {
                VisibleTreasureData t = visible[i];
                if (t.IsCarried) { treasures.Remove(t.TreasureId); continue; }
                treasures[t.TreasureId] = new TreasureMemory
                {
                    Cell = graph.Index(t.CellPosition), Position = t.WorldPosition,
                    Value = t.TreasureType == TreasureType.CentralChest ? 2.7f : 1f, SeenAt = Time.time
                };
            }
        }

        private void Move(int member, int target)
        {
            if (graph.Index(members[member].CurrentCell) != target)
            {
                int next = firstSteps[member][target];
                if (next >= 0) SXG_SetMemberMoveTargetCell(member, graph.Cell(next));
                return;
            }
            // Scattered chests are not necessarily at cell centers.
            foreach (TreasureMemory t in treasures.Values)
                if (t.Cell == target) { SXG_SetMemberMoveTargetWorld(member, t.Position); return; }
            SXG_StopMember(member);
        }
    }
}
