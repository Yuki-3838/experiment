using System.Collections.Generic;
using UnityEngine;
using SXG2025;

namespace Player7654321
{
    /// <summary>
    /// 脚を使わず、真下へ撃った砲弾の反動で上昇する機体。
    /// 下方の敵を狙えるときだけ照準を少し動かし、それ以外は砲口を真下に保つ。
    /// </summary>
    public class Player7654321 : ComPlayerBase
    {
        // DataTank.asset の弾速。敵の移動分を先読みするために使う。
        private const float DEFAULT_SHELL_SPEED = 38.0f;
        private const float MAX_TARGET_SPEED = 24.0f;
        private const float MAX_LEAD_TIME = 1.5f;
        private const float VELOCITY_SMOOTHING = 0.55f;

        // 砲口は砲塔基部よりおよそ3m下にある。敵がこの位置より低く、
        // 真下から一定角度以内にいる場合だけ、空中からの攻撃対象にする。
        [Header("上空からの射撃")]
        [SerializeField, Range(5.0f, 60.0f)] private float m_maxDownAimAngle = 38.0f;
        [SerializeField, Min(0.0f)] private float m_muzzleDropEstimate = 3.0f;
        [SerializeField] private float m_coreAimHeight = -0.35f;

        [Header("場外からの復帰")]
        [SerializeField, Range(0.55f, 0.9f)] private float m_returnRadiusRatio = 0.78f;

        private struct EnemyTrack
        {
            public bool HasPosition;
            public Vector3 PreviousPosition;
            public Vector3 Velocity;
            public float LastObservedAt;
            public float LastEngagedAt;
        }

        private EnemyTrack[] m_enemyTracks;
        private int[] m_targetByTurret = new int[0];
        private readonly HashSet<int> m_assignedTargets = new HashSet<int>();
        private Vector3 m_position;
        private Quaternion m_rotation;

        private void Update()
        {
            SXG_GetPositionAndRotation(out m_position, out m_rotation);

            TankInfo[] tanks = SXG_GetAllTanksInfo();
            if (tanks != null)
            {
                EnsureTrackStorage(tanks.Length);
                UpdateEnemyTracks(tanks);
            }

            // キャタピラは通常止め、外周へ流されたときだけ水平に戻す。
            RecoverFromFieldEdge();
            AimAndFireVolley(tanks);
        }

        /// <summary>
        /// 全砲門を真下へ向け、撃てる砲門から発射する。
        /// 反動の向きを上向きにそろえ、1門の不調で残りの砲門まで止まらないようにする。
        /// </summary>
        private void AimAndFireVolley(TankInfo[] tanks)
        {
            int turretCount = SXG_GetCountOfMyTurrets();
            if (turretCount <= 0) return;
            EnsureTurretTargets(turretCount);

            m_assignedTargets.Clear();
            for (int turretId = 0; turretId < turretCount; turretId++)
            {
                // 基本姿勢は真下。狙える敵が機体より下にいるときだけ照準を上書きする。
                SXG_RotateTurretToAngle(turretId, 0.0f, 0.0f);

                int targetId = m_targetByTurret[turretId];
                if (!CanAimAtEnemyFromAbove(tanks, targetId))
                {
                    targetId = FindBestTarget(tanks, m_assignedTargets);
                    if (targetId < 0)
                        targetId = FindBestTarget(tanks, null);
                }

                m_targetByTurret[turretId] = targetId;
                if (targetId < 0) continue;

                m_assignedTargets.Add(targetId);
                Vector3 impactPoint = GetPredictedAimPoint(tanks[targetId], targetId);
                if (IsWithinDownwardAttackCone(impactPoint))
                {
                    SXG_RotateTurretToImpactPoint(turretId, impactPoint);
                }
                else
                {
                    // 敵が横や上にいる間は真下を維持し、反動を横向きに逃がさない。
                    m_targetByTurret[turretId] = -1;
                }
            }

            // 各砲門を個別に確認する。装填中や不調の砲門があっても、使える砲門は撃つ。
            for (int turretId = 0; turretId < turretCount; turretId++)
            {
                if (!SXG_CanShoot(turretId)) continue;
                int targetId = m_targetByTurret[turretId];
                SXG_Shoot(turretId);

                if (targetId > 0 && m_enemyTracks != null && targetId < m_enemyTracks.Length)
                {
                    EnemyTrack track = m_enemyTracks[targetId];
                    track.LastEngagedAt = Time.time;
                    m_enemyTracks[targetId] = track;
                }

                m_targetByTurret[turretId] = -1;
            }
        }

        /// <summary>予測した着弾点が砲口より下で、真下からの角度制限内かを調べる。</summary>
        private bool IsWithinDownwardAttackCone(Vector3 aimPoint)
        {
            Vector3 estimatedMuzzle = m_position + Vector3.down * m_muzzleDropEstimate;
            Vector3 toTarget = aimPoint - estimatedMuzzle;
            if (toTarget.y >= 0.0f || toTarget.sqrMagnitude < 0.001f) return false;
            return Vector3.Angle(Vector3.down, toTarget) <= m_maxDownAimAngle;
        }

        private bool CanAimAtEnemyFromAbove(TankInfo[] tanks, int targetId)
        {
            return tanks != null
                && targetId > 0
                && targetId < tanks.Length
                && targetId < GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE
                && !tanks[targetId].IsDefeated
                && !tanks[targetId].IsInvincible
                && IsWithinDownwardAttackCone(tanks[targetId].Position);
        }

        /// <summary>高コストの敵を優先し、各砲門に重複しない標的を割り当てる。</summary>
        private int FindBestTarget(TankInfo[] tanks, HashSet<int> alreadyAssigned)
        {
            if (tanks == null || m_enemyTracks == null) return -1;

            int bestId = -1;
            float bestScore = float.NegativeInfinity;
            int count = Mathf.Min(tanks.Length, GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE);
            float now = Time.time;

            for (int enemyId = 1; enemyId < count; enemyId++)
            {
                TankInfo enemy = tanks[enemyId];
                if (enemy.IsDefeated || enemy.IsInvincible) continue;
                if (alreadyAssigned != null && alreadyAssigned.Contains(enemyId)) continue;

                Vector3 impactPoint = GetPredictedAimPoint(enemy, enemyId);
                if (!IsWithinDownwardAttackCone(impactPoint)) continue;

                EnemyTrack track = m_enemyTracks[enemyId];
                float secondsSinceLastShot = now - track.LastEngagedAt;
                float costBonus = Mathf.Clamp(enemy.CostOfOneTank, 0, 3000) * 0.003f;
                float distancePenalty = Vector3.Distance(m_position, enemy.Position) * 0.01f;
                float score = secondsSinceLastShot + costBonus - distancePenalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = enemyId;
                }
            }

            return bestId;
        }

        /// <summary>位置履歴から敵の速度を推定し、弾の飛行時間ぶん先読みする。</summary>
        private void UpdateEnemyTracks(TankInfo[] tanks)
        {
            float now = Time.time;
            int count = Mathf.Min(tanks.Length, GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE);

            for (int enemyId = 1; enemyId < count; enemyId++)
            {
                TankInfo enemy = tanks[enemyId];
                EnemyTrack track = m_enemyTracks[enemyId];

                if (enemy.IsDefeated)
                {
                    track.HasPosition = false;
                    track.Velocity = Vector3.zero;
                    m_enemyTracks[enemyId] = track;
                    continue;
                }

                if (!track.HasPosition)
                {
                    track.HasPosition = true;
                    track.PreviousPosition = enemy.Position;
                    track.Velocity = Vector3.zero;
                    track.LastObservedAt = now;
                    track.LastEngagedAt = now - 0.01f;
                    m_enemyTracks[enemyId] = track;
                    continue;
                }

                float elapsed = now - track.LastObservedAt;
                if (elapsed > 0.01f && elapsed < 2.0f)
                {
                    Vector3 measuredVelocity = (enemy.Position - track.PreviousPosition) / elapsed;
                    measuredVelocity = Vector3.ClampMagnitude(measuredVelocity, MAX_TARGET_SPEED);
                    track.Velocity = Vector3.Lerp(track.Velocity, measuredVelocity, VELOCITY_SMOOTHING);
                }
                else if (elapsed >= 2.0f)
                {
                    track.Velocity = Vector3.zero;
                }

                track.PreviousPosition = enemy.Position;
                track.LastObservedAt = now;
                m_enemyTracks[enemyId] = track;
            }
        }

        private Vector3 GetPredictedAimPoint(TankInfo enemy, int enemyId)
        {
            Vector3 predictedPosition = enemy.Position;
            Vector3 velocity = m_enemyTracks[enemyId].Velocity;

            for (int iteration = 0; iteration < 2; iteration++)
            {
                float flightTime = Vector3.Distance(m_position, predictedPosition) / DEFAULT_SHELL_SPEED;
                flightTime = Mathf.Clamp(flightTime, 0.0f, MAX_LEAD_TIME);
                predictedPosition = enemy.Position + velocity * flightTime;
            }

            return predictedPosition + Vector3.up * m_coreAimHeight;
        }

        /// <summary>外周に近づいた時だけ車体を中央へ戻す。通常はキャタピラを止める。</summary>
        private void RecoverFromFieldEdge()
        {
            Vector3 flatPosition = Flatten(m_position);
            float returnRadius = GameConstants.ABOUT_GAME_FIELD_RADIUS * m_returnRadiusRatio;
            if (flatPosition.magnitude <= returnRadius)
            {
                StopMovement();
                return;
            }

            DriveToward(-flatPosition.normalized);
        }

        private void DriveToward(Vector3 desiredDirection)
        {
            desiredDirection = Flatten(desiredDirection);
            if (desiredDirection.sqrMagnitude < 0.0001f)
            {
                StopMovement();
                return;
            }

            desiredDirection.Normalize();
            Vector3 forward = Flatten(m_rotation * Vector3.forward);
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();

            float angle = Vector3.SignedAngle(forward, desiredDirection, Vector3.up);
            float absAngle = Mathf.Abs(angle);
            if (absAngle > 100.0f)
            {
                float turn = Mathf.Sign(angle);
                SXG_SetCaterpillarPower(turn, -turn);
                return;
            }

            float steer = Mathf.Clamp(angle / 75.0f, -1.0f, 1.0f);
            float drive = absAngle > 75.0f ? 0.25f : 0.75f;
            SXG_SetCaterpillarPower(
                Mathf.Clamp(drive + steer * 0.75f, -1.0f, 1.0f),
                Mathf.Clamp(drive - steer * 0.75f, -1.0f, 1.0f));
        }

        private void StopMovement()
        {
            SXG_SetCaterpillarPower(0.0f, 0.0f);
        }

        private void EnsureTrackStorage(int requiredCount)
        {
            if (m_enemyTracks != null && m_enemyTracks.Length >= requiredCount) return;

            EnemyTrack[] expanded = new EnemyTrack[requiredCount];
            if (m_enemyTracks != null)
                System.Array.Copy(m_enemyTracks, expanded, m_enemyTracks.Length);
            m_enemyTracks = expanded;
        }

        private void EnsureTurretTargets(int turretCount)
        {
            if (m_targetByTurret.Length == turretCount) return;

            m_targetByTurret = new int[turretCount];
            for (int i = 0; i < m_targetByTurret.Length; i++)
                m_targetByTurret[i] = -1;
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0.0f;
            return value;
        }
    }
}
