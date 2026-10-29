using System.Collections.Generic;
using UnityEngine;

namespace Party0004838
{
    /// <summary>
    /// 2026-10-29。公開観測による中央担当・護衛対象・奪取対象を個人AIへ伝える。
    /// ゲーム本体や他部隊の実体を保持せず、自部隊のキャラクターIDごとに連絡帳を登録する。
    /// </summary>
    internal sealed class AriadneTactics
    {
        private static readonly Dictionary<int, AriadneTactics> Boards = new Dictionary<int, AriadneTactics>();
        private readonly HashSet<int> friends = new HashSet<int>(), threats = new HashSet<int>();
        private readonly List<int> registered = new List<int>();
        private PartyMemberInfo[] currentMembers;
        private IReadOnlyList<VisibleCharacterData> currentVisible;
        private KnownMapView currentMap;
        private bool groundGold;
        private Vector2Int groundGoldCell;
        private Vector3 groundGoldPosition;
        private float observedAt;
        private int centralRunnerId = -1, escortId = -1;
        private int goldCarrierId = -1, hunterId = -1, hunterTarget = -1;
        private float hunterUntil;
        private bool hunterEngaged;
        private bool hostilesPresent;
        private readonly HashSet<int> carriers = new HashSet<int>();
        internal bool NeedsFighter => hostilesPresent && carriers.Count >= 3;
        // 自部隊の公開搬出フックでのみ確定する。敵の見えない行動から搬出を推測しない。
        internal bool CentralExported { get; private set; }

        /// <summary>今回の観測から、運搬中の仲間へ接近している敵のIDを更新する。</summary>
        internal void Refresh(PartyMemberInfo[] members, IReadOnlyList<VisibleCharacterData> visible, KnownMapView map, int[][] distances, AriadneMap graph, IReadOnlyList<VisibleTreasureData> treasures)
        {
            Release();
            currentMembers=members;currentVisible=visible;currentMap=map;groundGold=false;
            foreach(VisibleTreasureData t in treasures)
                if(t.TreasureType==TreasureType.CentralChest && !t.IsCarried)
                {groundGold=true;groundGoldCell=t.CellPosition;groundGoldPosition=t.WorldPosition;}
            friends.Clear(); threats.Clear(); observedAt = Time.time;
            centralRunnerId = escortId = -1; hostilesPresent = false; carriers.Clear(); hunterEngaged=false;
            foreach (PartyMemberInfo member in members)
            {
                if (member == null) continue;
                friends.Add(member.CharacterId); registered.Add(member.CharacterId);
                if (member.HasTreasure) carriers.Add(member.CharacterId);
                Boards[member.CharacterId] = this;
            }
            goldCarrierId = -1;
            foreach (VisibleTreasureData treasure in treasures)
                if (treasure.TreasureType == TreasureType.CentralChest && treasure.IsCarried
                    && !friends.Contains(treasure.OwnerCharacterId)) goldCarrierId = treasure.OwnerCharacterId;
            foreach (VisibleCharacterData enemy in visible)
            {
                // 運搬中の敵は攻撃できないため、護衛が防ぐ直接の脅威には数えない。
                if (friends.Contains(enemy.CharacterId) || enemy.IsKnockedOut || enemy.HasTreasure) continue;
                hostilesPresent = true;
                for (int i = 0; i < members.Length; i++)
                {
                    PartyMemberInfo carrier = members[i];
                    if (carrier == null || !carrier.HasTreasure || carrier.IsKnockedOut || carrier.IsActionLocked) continue;
                    Vector3 delta = enemy.WorldPosition - carrier.WorldPosition; delta.y = 0;
                    // 8ワールド単位以内の敵が、護衛を検討する候補になる。
                    // 壁を挟んだ敵へ反応しないよう、既知の接続で5歩以内かも確認する。
                    if (delta.sqrMagnitude <= 64f && distances[i][graph.Index(enemy.CellPosition)] >= 0
                        && distances[i][graph.Index(enemy.CellPosition)] <= 5)
                    { threats.Add(enemy.CharacterId); break; }
                }
            }
        }

        // 地上の金宝箱へ向かう人数を、今見えている周囲の戦力で判断する。
        // 候補本人は到着する1人として数え、ほかは実際に近くにいる手ぶらの味方だけを数える。
        internal bool CanApproachGold(int memberId)
        {
            if(!groundGold || currentMap==null || currentVisible==null || currentMembers==null) return true;
            int enemies=0,allies=1;
            foreach(VisibleCharacterData e in currentVisible)
            {
                if(friends.Contains(e.CharacterId) || e.IsKnockedOut || e.HasTreasure) continue;
                Vector3 d=e.WorldPosition-groundGoldPosition;d.y=0;
                if(d.sqrMagnitude<=16f && IsWithinTwoSteps(currentMap,e.CellPosition,groundGoldCell)) enemies++;
            }
            if(enemies==0) return true;
            foreach(PartyMemberInfo m in currentMembers)
            {
                if(m==null || m.CharacterId==memberId || m.IsKnockedOut || m.HasTreasure
                    || carriers.Contains(m.CharacterId) || m.IsActionLocked) continue;
                Vector3 d=m.WorldPosition-groundGoldPosition;d.y=0;
                if(d.sqrMagnitude<=36f && IsWithinTwoSteps(currentMap,m.CurrentCell,groundGoldCell)) allies++;
            }
            return allies>=enemies+1;
        }

        internal bool IsFriend(int characterId) => friends.Contains(characterId);
        internal bool ThreatensCarrier(int characterId) => threats.Contains(characterId);

        internal void SetCentralRunner(int characterId)
        {
            if (friends.Contains(characterId)) centralRunnerId = characterId;
        }

        /// <summary>発見済みの金宝箱への回収担当だけが、道中の通常宝箱を見送る。</summary>
        internal static bool IsCentralRunner(int memberId)
        {
            if (!Boards.TryGetValue(memberId, out AriadneTactics board)) return false;
            float age = Time.time - board.observedAt;
            // 護衛と同じ0.36ゲーム内秒。更新が止まれば通常の取得判断へ戻す。
            return age >= 0 && age <= 0.36f && board.centralRunnerId == memberId;
        }

        internal bool IsGoldCarrier(int enemyId) => goldCarrierId == enemyId;
        internal void SetHunter(int memberId, int enemyId) { hunterId = memberId; hunterTarget = enemyId; hunterUntil = Time.time + 1f; hunterEngaged=true; }
        internal static bool IsHunting(int memberId, int enemyId)
        {
            return Boards.TryGetValue(memberId, out AriadneTactics board) && board.hunterId == memberId
                && board.hunterTarget == enemyId && Time.time <= board.hunterUntil;
        }
        internal static bool IsGoldTarget(int memberId, int enemyId)
        {
            return Boards.TryGetValue(memberId, out AriadneTactics board) && board.goldCarrierId == enemyId
                && Time.time - board.observedAt >= 0 && Time.time - board.observedAt <= 0.36f;
        }
        internal void SetEscort(int memberId) { escortId = memberId; }

        // 敵を観測中は戦闘役を残し、金宝箱は近くの戦力が足りる場合に取得する。
        internal static bool CanPickUp(int memberId, TreasureType type)
        {
            if (!Boards.TryGetValue(memberId, out AriadneTactics board)) return true;
            float age = Time.time - board.observedAt;
            if (age < 0 || age > 0.36f) return true;
            if(type==TreasureType.CentralChest && !board.CanApproachGold(memberId)) return false;
            // 金宝箱の奪取中は、道中の通常宝で運搬役へ変わらず追跡を続ける。
            if(type!=TreasureType.CentralChest && board.hunterEngaged && board.hunterId==memberId
                && board.goldCarrierId>=0 && board.goldCarrierId==board.hunterTarget) return false;
            if (type != TreasureType.CentralChest
                && (board.escortId == memberId || (board.NeedsFighter && !board.carriers.Contains(memberId)
                    && !(board.hunterId == memberId && Time.time <= board.hunterUntil)))) return false;
            board.carriers.Add(memberId);
            return true;
        }

        internal static void NotifyCentralExported(int memberId)
        {
            if (Boards.TryGetValue(memberId, out AriadneTactics board)) board.CentralExported = true;
        }

        /// <summary>個人AIが、自分の部隊から届いた新鮮な護衛対象だけを参照する。</summary>
        internal static bool IsDefenseTarget(int memberId, int enemyId)
        {
            if (!Boards.TryGetValue(memberId, out AriadneTactics board)) return false;
            // 思考間隔0.12秒の3回分だけ有効。古い観測や時刻の巻き戻りを引き継がない。
            float age = Time.time - board.observedAt;
            return age >= 0 && age <= 0.36f && board.threats.Contains(enemyId);
        }

        /// <summary>終了した部隊の登録を外す。別の部隊が同じIDを登録済みなら削除しない。</summary>
        internal void Release()
        {
            foreach (int id in registered)
                if (Boards.TryGetValue(id, out AriadneTactics board) && ReferenceEquals(board, this)) Boards.Remove(id);
            registered.Clear();
        }

        private static bool IsWithinTwoSteps(KnownMapView map, Vector2Int from, Vector2Int to)
        {
            if (from == to) return true;
            if (map.CanMove(from, to)) return true;
            if (Mathf.Abs(from.x - to.x) + Mathf.Abs(from.y - to.y) != 2) return false;
            // 2歩の候補は公開マップの開口を両側とも確認する。未知の壁を通れるとは仮定しない。
            return Via(map, from, from + Vector2Int.up, to) || Via(map, from, from + Vector2Int.right, to)
                || Via(map, from, from + Vector2Int.down, to) || Via(map, from, from + Vector2Int.left, to);
        }

        private static bool Via(KnownMapView map, Vector2Int from, Vector2Int middle, Vector2Int to)
            => map.CanMove(from, middle) && map.CanMove(middle, to);
    }
}
