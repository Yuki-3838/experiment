using System.Collections.Generic;
using UnityEngine;
using SXG2025;

namespace Player7654321
{
    /// <summary>
    /// Sample06のように脚を伸ばして高所を取り、全砲門を敵へ向けて攻撃する機体。
    /// </summary>
    public class Player7654321 : ComPlayerBase
    {
        // DataTank.asset の弾速。敵の移動分を先読みするために使う。
        private const float DEFAULT_SHELL_SPEED = 38.0f;
        private const float MAX_TARGET_SPEED = 24.0f;
        private const float MAX_LEAD_TIME = 1.5f;
        private const float VELOCITY_SMOOTHING = 0.55f;
        private const float LEG_EXTENSION_TIME = 4.0f;
        private const float TURRET_AIM_SETTLE_TIME = 1.25f;
        private static readonly float[] s_standUpJointAngles =
        {
            -160.0f, -160.0f, 160.0f, 160.0f,
            170.0f, 170.0f, -170.0f, -170.0f
        };

        [Header("照準")]
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
        private RotJointPart[] m_rotJointParts = new RotJointPart[0];
        private Quaternion[] m_jointBaseRotations = new Quaternion[0];
        private int[] m_parentJointByArm = new int[0];
        private int[] m_targetByArm = new int[0];
        private int[] m_targetByTurret = new int[0];
        private float[] m_targetAssignedAt = new float[0];
        private readonly HashSet<int> m_assignedTargets = new HashSet<int>();
        private readonly HashSet<int> m_assignedArmTargets = new HashSet<int>();
        private Vector3 m_position;
        private Quaternion m_rotation;
        private float m_legExtensionStartedAt;

        private void Start()
        {
            m_legExtensionStartedAt = Time.time;
            GetRotJoints(CacheRotJointReferences);
        }

        private void Update()
        {
            SXG_GetPositionAndRotation(out m_position, out m_rotation);

            TankInfo[] tanks = SXG_GetAllTanksInfo();
            if (tanks != null)
            {
                EnsureTrackStorage(tanks.Length);
                UpdateEnemyTracks(tanks);
            }

            ExtendLegsForHeight(tanks);

            // キャタピラは通常止め、外周へ流されたときだけ水平に戻す。
            RecoverFromFieldEdge();
            AimAndFireVolley(tanks);
        }

        /// <summary>Sample06「アメンボ」のように、4秒かけて8本の脚を伸ばす。</summary>
        private void ExtendLegsForHeight(TankInfo[] tanks)
        {
            if (SXG_GetCountOfMyRotJoints() != s_standUpJointAngles.Length) return;

            float progress = Mathf.Clamp01((Time.time - m_legExtensionStartedAt) / LEG_EXTENSION_TIME);
            for (int jointId = 0; jointId < 4; jointId++)
            {
                SXG_RotateJointToAngle(jointId, s_standUpJointAngles[jointId] * progress);
            }

            if (progress < 1.0f)
            {
                for (int jointId = 4; jointId < s_standUpJointAngles.Length; jointId++)
                    SXG_RotateJointToAngle(jointId, s_standUpJointAngles[jointId] * progress);
                return;
            }

            AimAppendagesAtEnemies(tanks);
        }

        /// <summary>脚の根元で高さを保ち、先端側の長い関節を敵へ向ける。</summary>
        private void AimAppendagesAtEnemies(TankInfo[] tanks)
        {
            if (tanks == null || m_rotJointParts.Length != s_standUpJointAngles.Length) return;

            m_assignedArmTargets.Clear();
            for (int jointId = 4; jointId < m_rotJointParts.Length; jointId++)
            {
                RotJointPart armJoint = m_rotJointParts[jointId];
                int parentId = jointId < m_parentJointByArm.Length ? m_parentJointByArm[jointId] : -1;
                if (armJoint == null || parentId < 0 || parentId >= m_rotJointParts.Length)
                {
                    SXG_RotateJointToAngle(jointId, s_standUpJointAngles[jointId]);
                    continue;
                }

                Transform armFrame = m_rotJointParts[parentId].transform;
                int targetId = m_targetByArm[jointId];
                if (!IsTargetable(tanks, targetId) || m_assignedArmTargets.Contains(targetId))
                    targetId = FindBestArmTarget(tanks, m_assignedArmTargets, armFrame, armJoint.transform.position);
                if (targetId < 0)
                    targetId = FindBestArmTarget(tanks, null, armFrame, armJoint.transform.position);
                m_targetByArm[jointId] = targetId;
                if (targetId < 0)
                {
                    SXG_RotateJointToAngle(jointId, s_standUpJointAngles[jointId]);
                    continue;
                }

                m_assignedArmTargets.Add(targetId);
                Vector3 targetPosition = GetPredictedAimPoint(tanks[targetId], targetId);
                Vector3 directionInParent = armFrame.InverseTransformDirection(targetPosition - armJoint.transform.position);
                Vector3 directionInBase = Quaternion.Inverse(m_jointBaseRotations[jointId]) * directionInParent;
                directionInBase.y = 0.0f;

                if (directionInBase.sqrMagnitude < 0.0001f)
                {
                    SXG_RotateJointToAngle(jointId, s_standUpJointAngles[jointId]);
                    continue;
                }

                // このジョイントの子パーツはローカルの後方(-Z)へ伸びる。
                float targetAngle = Mathf.Atan2(-directionInBase.x, -directionInBase.z) * Mathf.Rad2Deg;
                SXG_RotateJointToAngle(jointId, targetAngle);
            }
        }

        private int FindBestArmTarget(TankInfo[] tanks, HashSet<int> alreadyAssigned, Transform armFrame, Vector3 jointPosition)
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

                Vector3 localDirection = armFrame.InverseTransformDirection(enemy.Position - jointPosition);
                float distance = localDirection.magnitude;
                if (distance < 0.001f) continue;

                // 回転軸に近い方向ほど、腕のスイング面で相手まで伸ばしやすい。
                float outOfPlane = Mathf.Abs(localDirection.y) / distance;
                EnemyTrack track = m_enemyTracks[enemyId];
                float recentShotBonus = (now - track.LastEngagedAt) * 0.03f;
                float costBonus = Mathf.Clamp(enemy.CostOfOneTank, 0, 3000) * 0.003f;
                float distancePenalty = distance * 0.01f;
                float score = recentShotBonus + costBonus - distancePenalty - outOfPlane * 8.0f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = enemyId;
                }
            }

            return bestId;
        }

        private void CacheRotJointReferences(RotJointPart[] joints)
        {
            m_rotJointParts = joints ?? new RotJointPart[0];
            m_jointBaseRotations = new Quaternion[m_rotJointParts.Length];
            m_parentJointByArm = new int[m_rotJointParts.Length];
            m_targetByArm = new int[m_rotJointParts.Length];

            for (int jointId = 0; jointId < m_rotJointParts.Length; jointId++)
            {
                RotJointPart joint = m_rotJointParts[jointId];
                m_jointBaseRotations[jointId] = joint != null ? joint.transform.localRotation : Quaternion.identity;
                m_parentJointByArm[jointId] = -1;
                m_targetByArm[jointId] = -1;

                if (jointId < 4 || joint == null) continue;
                for (int parentId = 0; parentId < 4; parentId++)
                {
                    if (m_rotJointParts[parentId] != null && joint.transform.IsChildOf(m_rotJointParts[parentId].transform))
                    {
                        m_parentJointByArm[jointId] = parentId;
                        break;
                    }
                }
            }
        }

        /// <summary>敵を砲門ごとに割り当て、照準が追いついた砲門から発射する。</summary>
        private void AimAndFireVolley(TankInfo[] tanks)
        {
            int turretCount = SXG_GetCountOfMyTurrets();
            if (turretCount <= 0) return;
            EnsureTurretTargets(turretCount);

            m_assignedTargets.Clear();
            for (int turretId = 0; turretId < turretCount; turretId++)
            {
                int targetId = m_targetByTurret[turretId];
                if (!IsTargetable(tanks, targetId) || m_assignedTargets.Contains(targetId))
                {
                    targetId = FindBestTarget(tanks, m_assignedTargets);
                    if (targetId < 0)
                        targetId = FindBestTarget(tanks, null);
                }

                if (targetId != m_targetByTurret[turretId])
                    m_targetAssignedAt[turretId] = Time.time;

                m_targetByTurret[turretId] = targetId;
                if (targetId < 0)
                {
                    SXG_RotateTurretToAngle(turretId, 0.0f, 0.0f);
                    continue;
                }

                m_assignedTargets.Add(targetId);
                Vector3 impactPoint = GetPredictedAimPoint(tanks[targetId], targetId);
                SXG_RotateTurretToImpactPoint(turretId, impactPoint);
            }

            // 砲塔の最大旋回時間を待ってから撃ち、照準前の初弾が前方へ飛ぶのを防ぐ。
            for (int turretId = 0; turretId < turretCount; turretId++)
            {
                int targetId = m_targetByTurret[turretId];
                if (targetId < 0 || !SXG_CanShoot(turretId)) continue;
                if (Time.time - m_targetAssignedAt[turretId] < TURRET_AIM_SETTLE_TIME) continue;

                SXG_Shoot(turretId);

                if (m_enemyTracks != null && targetId < m_enemyTracks.Length)
                {
                    EnemyTrack track = m_enemyTracks[targetId];
                    track.LastEngagedAt = Time.time;
                    m_enemyTracks[targetId] = track;
                }
            }
        }

        private bool IsTargetable(TankInfo[] tanks, int targetId)
        {
            return tanks != null
                && targetId > 0
                && targetId < tanks.Length
                && targetId < GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE
                && !tanks[targetId].IsDefeated
                && !tanks[targetId].IsInvincible;
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
            m_targetAssignedAt = new float[turretCount];
            for (int i = 0; i < m_targetByTurret.Length; i++)
            {
                m_targetByTurret[i] = -1;
                m_targetAssignedAt[i] = float.NegativeInfinity;
            }
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0.0f;
            return value;
        }
    }
}
