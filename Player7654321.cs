using System.Collections.Generic;
using UnityEngine;
using SXG2025;

namespace Player7654321
{
    /// <summary>
    /// Sample06の脚で高所を取り、脚とは別の伸縮式攻撃腕を敵へ伸ばして砲撃する機体。
    /// </summary>
    public class Player7654321 : ComPlayerBase
    {
        private const int STAND_JOINT_COUNT = 8;
        private const int LEFT_ATTACK_ARM_JOINT_ID = 8;
        private const int RIGHT_ATTACK_ARM_JOINT_ID = 9;
        private const int CENTER_ATTACK_ARM_JOINT_ID = 10;
        private const int LEFT_ARM_TURRET_ID = 2;
        private const int RIGHT_ARM_TURRET_ID = 3;
        private const int CENTER_ARM_TURRET_ID = 4;
        private const int ATTACK_ARM_LINK_COUNT = 10;
        private const float ATTACK_ARM_LINK_THICKNESS = 0.24f;
        private const float ATTACK_ARM_ZIGZAG_AMPLITUDE = 0.65f;
        private const float MIN_ATTACK_ARM_LENGTH = 3.0f;
        private const float MAX_ATTACK_ARM_LENGTH = 80.0f;
        private const float ATTACK_ARM_EXTENSION_SPEED = 60.0f;
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
        private TurretPart[] m_turretParts = new TurretPart[0];
        private Transform[][] m_attackArmLinks = new Transform[3][];
        private float[] m_attackArmLengths = { 4.0f, 4.0f, 4.0f };
        private int[] m_targetByTurret = new int[0];
        private float[] m_targetAssignedAt = new float[0];
        private readonly HashSet<int> m_assignedTargets = new HashSet<int>();
        private int m_sharedAttackTarget = -1;
        private Vector3 m_position;
        private Quaternion m_rotation;
        private float m_legExtensionStartedAt;

        private void Start()
        {
            m_legExtensionStartedAt = Time.time;
            GetRotJoints(CacheRotJointReferences);
            GetTurrets(CacheTurretReferences);
            InitializeAttackArmLinks();
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

            ExtendLegsForHeight();

            // キャタピラは通常止め、外周へ流されたときだけ水平に戻す。
            RecoverFromFieldEdge();
            AimAndFireVolley(tanks);
            ExtendAttackArms(tanks);
        }

        /// <summary>Sample06「アメンボ」のように、4秒かけて8本の脚を伸ばす。</summary>
        private void ExtendLegsForHeight()
        {
            if (SXG_GetCountOfMyRotJoints() < STAND_JOINT_COUNT) return;

            float progress = Mathf.Clamp01((Time.time - m_legExtensionStartedAt) / LEG_EXTENSION_TIME);
            for (int jointId = 0; jointId < STAND_JOINT_COUNT; jointId++)
            {
                SXG_RotateJointToAngle(jointId, s_standUpJointAngles[jointId] * progress);
            }
        }

        /// <summary>脚とは別の伸縮式3本腕を、先端砲塔が狙う標的へ伸ばす。</summary>
        private void ExtendAttackArms(TankInfo[] tanks)
        {
            if (m_rotJointParts.Length <= CENTER_ATTACK_ARM_JOINT_ID) return;

            AimAttackArm(tanks, LEFT_ATTACK_ARM_JOINT_ID, LEFT_ARM_TURRET_ID, 70.0f);
            AimAttackArm(tanks, RIGHT_ATTACK_ARM_JOINT_ID, RIGHT_ARM_TURRET_ID, -70.0f);
            AimAttackArm(tanks, CENTER_ATTACK_ARM_JOINT_ID, CENTER_ARM_TURRET_ID, 180.0f);
        }

        private void AimAttackArm(TankInfo[] tanks, int jointId, int turretId, float retractAngle)
        {
            if (turretId < 0 || turretId >= m_targetByTurret.Length || turretId >= m_turretParts.Length)
            {
                RetractAttackArm(jointId, turretId, retractAngle);
                return;
            }

            int targetId = m_targetByTurret[turretId];
            if (!IsTargetable(tanks, targetId))
            {
                RetractAttackArm(jointId, turretId, retractAngle);
                return;
            }

            RotJointPart attackJoint = m_rotJointParts[jointId];
            TurretPart turret = m_turretParts[turretId];
            if (attackJoint == null || turret == null)
            {
                RetractAttackArm(jointId, turretId, retractAngle);
                return;
            }

            Transform[] armLinks = GetAttackArmLinks(jointId);
            if (armLinks == null) return;

            Vector3 targetDirection = transform.InverseTransformDirection(
                GetPredictedAimPoint(tanks[targetId], targetId) - attackJoint.transform.position);
            float horizontalDistance = new Vector2(targetDirection.x, targetDirection.z).magnitude;
            float distance = targetDirection.magnitude;
            if (distance < 0.001f) return;

            float directionAngle = Mathf.Atan2(-targetDirection.x, -targetDirection.z) * Mathf.Rad2Deg;
            float pitchAngle = Mathf.Atan2(targetDirection.y, horizontalDistance) * Mathf.Rad2Deg;
            // 砲塔を相手の中心まで押し込み、3本が同じ場所へ刺さるよう少しだけ奥へ出す。
            float armLength = Mathf.Clamp(distance + 0.8f, MIN_ATTACK_ARM_LENGTH, MAX_ATTACK_ARM_LENGTH);
            SXG_RotateJointToAngle(jointId, directionAngle);
            SetAttackArmLength(jointId, armLinks, turret.transform, armLength, pitchAngle);
        }

        private void RetractAttackArm(int jointId, int turretId, float retractAngle)
        {
            SXG_RotateJointToAngle(jointId, retractAngle);

            if (turretId < 0 || turretId >= m_turretParts.Length || m_turretParts[turretId] == null)
                return;

            RotJointPart attackJoint = m_rotJointParts[jointId];
            if (attackJoint == null) return;

            Transform[] armLinks = GetAttackArmLinks(jointId);
            if (armLinks != null)
                SetAttackArmLength(jointId, armLinks, m_turretParts[turretId].transform, MIN_ATTACK_ARM_LENGTH, 0.0f);
        }

        private void InitializeAttackArmLinks()
        {
            for (int armIndex = 0; armIndex < m_attackArmLinks.Length; armIndex++)
            {
                int jointId = LEFT_ATTACK_ARM_JOINT_ID + armIndex;
                if (jointId >= m_rotJointParts.Length || m_rotJointParts[jointId] == null) continue;

                Transform armRoot = m_rotJointParts[jointId].transform;
                Transform firstLink = armRoot.Find("AttackArmSegment");
                if (firstLink == null) continue;

                Transform[] links = new Transform[ATTACK_ARM_LINK_COUNT];
                links[0] = firstLink;
                Collider firstCollider = firstLink.GetComponent<Collider>();
                if (firstCollider != null) firstCollider.enabled = false;

                for (int linkIndex = 1; linkIndex < links.Length; linkIndex++)
                {
                    GameObject linkObject = Instantiate(firstLink.gameObject, armRoot);
                    linkObject.name = "AttackArmLink_" + linkIndex;
                    Transform link = linkObject.transform;
                    link.localPosition = Vector3.zero;
                    link.localRotation = Quaternion.identity;
                    link.localScale = new Vector3(ATTACK_ARM_LINK_THICKNESS, ATTACK_ARM_LINK_THICKNESS, 1.0f);
                    Collider linkCollider = linkObject.GetComponent<Collider>();
                    if (linkCollider != null) linkCollider.enabled = false;
                    links[linkIndex] = link;
                }

                m_attackArmLinks[armIndex] = links;
                m_attackArmLengths[armIndex] = Mathf.Max(firstLink.localScale.z, MIN_ATTACK_ARM_LENGTH);
            }
        }

        private Transform[] GetAttackArmLinks(int jointId)
        {
            int armIndex = jointId - LEFT_ATTACK_ARM_JOINT_ID;
            if (armIndex < 0 || armIndex >= m_attackArmLinks.Length) return null;
            return m_attackArmLinks[armIndex];
        }

        private void SetAttackArmLength(int jointId, Transform[] links, Transform turretTransform, float targetLength, float pitchAngle)
        {
            int armIndex = jointId - LEFT_ATTACK_ARM_JOINT_ID;
            if (armIndex < 0 || armIndex >= m_attackArmLengths.Length || links == null || links.Length == 0) return;

            float length = Mathf.MoveTowards(m_attackArmLengths[armIndex], targetLength, ATTACK_ARM_EXTENSION_SPEED * Time.deltaTime);
            m_attackArmLengths[armIndex] = length;

            float pitchRadians = pitchAngle * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(0.0f, Mathf.Sin(pitchRadians), -Mathf.Cos(pitchRadians));
            int linkCount = links.Length;
            for (int linkIndex = 0; linkIndex < linkCount; linkIndex++)
            {
                Vector3 start = GetAttackArmPoint(linkIndex, linkCount, length, forward, armIndex);
                Vector3 end = GetAttackArmPoint(linkIndex + 1, linkCount, length, forward, armIndex);
                Vector3 linkDirection = end - start;
                Transform link = links[linkIndex];
                if (link == null || linkDirection.sqrMagnitude < 0.0001f) continue;

                link.localPosition = (start + end) * 0.5f;
                link.localRotation = Quaternion.FromToRotation(Vector3.back, linkDirection.normalized);
                link.localScale = new Vector3(ATTACK_ARM_LINK_THICKNESS, ATTACK_ARM_LINK_THICKNESS, linkDirection.magnitude);
            }

            turretTransform.localPosition = forward * length;
        }

        private Vector3 GetAttackArmPoint(int pointIndex, int linkCount, float length, Vector3 forward, int armIndex)
        {
            float progress = (float)pointIndex / linkCount;
            float offset = 0.0f;
            if (pointIndex > 0 && pointIndex < linkCount)
            {
                float side = ((pointIndex + armIndex) % 2 == 0) ? 1.0f : -1.0f;
                offset = side * ATTACK_ARM_ZIGZAG_AMPLITUDE * Mathf.Sin(progress * Mathf.PI);
            }

            return forward * (length * progress) + Vector3.right * offset;
        }

        private void CacheRotJointReferences(RotJointPart[] joints)
        {
            m_rotJointParts = joints ?? new RotJointPart[0];
        }

        private void CacheTurretReferences(TurretPart[] turrets)
        {
            m_turretParts = turrets ?? new TurretPart[0];
        }

        /// <summary>敵を砲門ごとに割り当て、照準が追いついた砲門から発射する。</summary>
        private void AimAndFireVolley(TankInfo[] tanks)
        {
            int turretCount = SXG_GetCountOfMyTurrets();
            if (turretCount <= 0) return;
            EnsureTurretTargets(turretCount);

            m_assignedTargets.Clear();
            if (!IsTargetable(tanks, m_sharedAttackTarget))
                m_sharedAttackTarget = FindBestTarget(tanks, null);

            for (int turretId = 0; turretId < turretCount; turretId++)
            {
                int targetId;
                if (IsAttackArmTurret(turretId))
                {
                    // 3本の腕は同じ敵の中心へ集中させ、砲塔をまとめて突き刺す。
                    targetId = m_sharedAttackTarget;
                }
                else
                {
                    targetId = m_targetByTurret[turretId];
                    if (!IsTargetable(tanks, targetId) || m_assignedTargets.Contains(targetId))
                    {
                        targetId = FindBestTarget(tanks, m_assignedTargets);
                        if (targetId < 0)
                            targetId = FindBestTarget(tanks, null);
                    }
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

        private bool IsAttackArmTurret(int turretId)
        {
            return turretId == LEFT_ARM_TURRET_ID
                || turretId == RIGHT_ARM_TURRET_ID
                || turretId == CENTER_ARM_TURRET_ID;
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
