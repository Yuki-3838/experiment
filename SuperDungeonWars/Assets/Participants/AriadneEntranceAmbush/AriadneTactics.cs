using System.Collections.Generic;
using UnityEngine;

namespace AriadneEntranceAmbush
{
    /// <summary>
    /// 独立待ち伏せ版（2026-10-27）の連絡帳。護衛・中央担当と、入口役の取得許可を伝える。
    /// ゲーム本体や他部隊の実体を保持せず、自部隊のキャラクターIDごとに連絡帳を登録する。
    /// </summary>
    internal sealed class AriadneTactics
    {
        private static readonly Dictionary<int, AriadneTactics> Boards = new Dictionary<int, AriadneTactics>();
        private readonly HashSet<int> friends = new HashSet<int>(), threats = new HashSet<int>();
        private readonly List<int> registered = new List<int>();
        private readonly Dictionary<int, bool> ambushPickup = new Dictionary<int, bool>();
        private float observedAt;
        private int centralRunnerId = -1;
        // 自部隊の公開搬出フックでのみ確定する。敵の見えない行動から搬出を推測しない。
        internal bool CentralExported { get; private set; }

        /// <summary>今回の観測から、運搬中の仲間へ接近している敵のIDを更新する。</summary>
        internal void Refresh(PartyMemberInfo[] members, IReadOnlyList<VisibleCharacterData> visible, KnownMapView map)
        {
            Release();
            friends.Clear(); threats.Clear(); observedAt = Time.time;
            ambushPickup.Clear();
            centralRunnerId = -1;
            foreach (PartyMemberInfo member in members)
            {
                if (member == null) continue;
                friends.Add(member.CharacterId); registered.Add(member.CharacterId);
                Boards[member.CharacterId] = this;
            }
            foreach (VisibleCharacterData enemy in visible)
            {
                // 運搬中の敵は攻撃できないため、護衛が防ぐ直接の脅威には数えない。
                if (friends.Contains(enemy.CharacterId) || enemy.IsKnockedOut || enemy.HasTreasure) continue;
                foreach (PartyMemberInfo carrier in members)
                {
                    if (carrier == null || !carrier.HasTreasure || carrier.IsKnockedOut) continue;
                    Vector3 delta = enemy.WorldPosition - carrier.WorldPosition; delta.y = 0;
                    // 3ワールド単位（セル間隔2なら1.5マス）が護衛を起動する距離。
                    // 壁を挟んだ敵へ反応しないよう、既知の接続で2歩以内かも確認する。
                    if (delta.sqrMagnitude <= 9f && IsWithinTwoSteps(map, carrier.CurrentCell, enemy.CellPosition))
                    { threats.Add(enemy.CharacterId); break; }
                }
            }
        }

        internal bool IsFriend(int characterId) => friends.Contains(characterId);
        internal bool ThreatensCarrier(int characterId) => threats.Contains(characterId);

        internal void SetAmbushPickup(int characterId, bool allowed)
        {
            if (friends.Contains(characterId)) ambushPickup[characterId] = allowed;
        }

        internal static bool TryGetAmbushPickup(int memberId, out bool allowed)
        {
            allowed = false;
            if (!Boards.TryGetValue(memberId, out AriadneTactics board)) return false;
            float age = Time.time - board.observedAt;
            // 自分の部隊からの新しい許可だけを使う。0.36秒で失効する。
            return age >= 0f && age <= 0.36f && board.ambushPickup.TryGetValue(memberId, out allowed);
        }

        internal void SetCentralRunner(int characterId)
        {
            if (friends.Contains(characterId)) centralRunnerId = characterId;
        }

        /// <summary>有効な中央担当だけが、道中の通常宝箱を拾わずに進める。</summary>
        internal static bool IsCentralRunner(int memberId)
        {
            if (!Boards.TryGetValue(memberId, out AriadneTactics board)) return false;
            float age = Time.time - board.observedAt;
            // 護衛と同じ0.36ゲーム内秒。更新が止まれば通常の取得判断へ戻す。
            return age >= 0 && age <= 0.36f && board.centralRunnerId == memberId;
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
