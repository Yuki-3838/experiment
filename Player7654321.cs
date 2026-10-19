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
        private const int DEFAULT_LEFT_ATTACK_ARM_JOINT_ID = 8;
        private const int DEFAULT_RIGHT_ATTACK_ARM_JOINT_ID = 9;
        private const int DEFAULT_CENTER_ATTACK_ARM_JOINT_ID = 10;
        // GetTurrets() array indices used if the inspector references are unset.
        private const int DEFAULT_LEFT_ARM_TURRET_ID = 3;
        private const int DEFAULT_RIGHT_ARM_TURRET_ID = 2;
        private const int DEFAULT_CENTER_ARM_TURRET_ID = 4;
        private const int DEFAULT_LEFT_ATTITUDE_TURRET_ID = 1;
        private const int DEFAULT_RIGHT_ATTITUDE_TURRET_ID = 0;
        private const int ATTACK_ARM_LINK_COUNT = 10;
        private const float ATTACK_ARM_LINK_THICKNESS = 0.24f;
        private const float ATTACK_ARM_ZIGZAG_AMPLITUDE = 0.65f;
        private const float MIN_ATTACK_ARM_LENGTH = 3.0f;
        private const float MAX_ATTACK_ARM_LENGTH = 80.0f;
        private const float ATTACK_ARM_EXTENSION_SPEED = 60.0f;
        private const float SHOT_STAGGER_INTERVAL = 0.4f;
        private const float STABILIZER_LINEAR_DAMPING = 6.0f;
        private const float STABILIZER_ANGULAR_DAMPING = 5.0f;
        private const float MAX_STABILIZED_PLANAR_SPEED = 7.5f;
        private const float MAX_STABILIZED_VERTICAL_SPEED = 6.0f;
        private const float MAX_STABILIZED_ANGULAR_SPEED = 2.0f;
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

        [Header("攻撃アームのジョイント")]
        [SerializeField] private RotJointPart m_leftAttackArmJoint;
        [SerializeField] private RotJointPart m_rightAttackArmJoint;
        [SerializeField] private RotJointPart m_centerAttackArmJoint;

        [Header("攻撃アームの砲塔")]
        [SerializeField] private TurretPart m_leftArmTurret;
        [SerializeField] private TurretPart m_rightArmTurret;
        [SerializeField] private TurretPart m_centerArmTurret;

        [Header("姿勢制御")]
        [SerializeField] private TurretPart m_leftAttitudeTurret;
        [SerializeField] private TurretPart m_rightAttitudeTurret;
        [SerializeField, Min(0.0f)] private float m_uprightCorrectionStrength = 12.0f;
        [SerializeField, Min(0.0f)] private float m_uprightAngularDamping = 4.0f;
        [SerializeField, Range(0.05f, 0.5f)] private float m_rollCorrectionThreshold = 0.18f;

        [Header("場外からの復帰")]
        [SerializeField, Range(0.55f, 0.9f)] private float m_returnRadiusRatio = 0.7f;
        [SerializeField, Min(0.0f)] private float m_returnAcceleration = 24.0f;

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
        private int m_leftAttackArmJointId = DEFAULT_LEFT_ATTACK_ARM_JOINT_ID;
        private int m_rightAttackArmJointId = DEFAULT_RIGHT_ATTACK_ARM_JOINT_ID;
        private int m_centerAttackArmJointId = DEFAULT_CENTER_ATTACK_ARM_JOINT_ID;
        private int m_leftArmTurretId = DEFAULT_LEFT_ARM_TURRET_ID;
        private int m_rightArmTurretId = DEFAULT_RIGHT_ARM_TURRET_ID;
        private int m_centerArmTurretId = DEFAULT_CENTER_ARM_TURRET_ID;
        private int m_leftAttitudeTurretId = DEFAULT_LEFT_ATTITUDE_TURRET_ID;
        private int m_rightAttitudeTurretId = DEFAULT_RIGHT_ATTITUDE_TURRET_ID;
        private Transform[][] m_attackArmLinks = new Transform[3][];
        private float[] m_attackArmLengths = { 4.0f, 4.0f, 4.0f };
        private Rigidbody m_stabilityBody;
        private int[] m_targetByTurret = new int[0];
        private float[] m_targetAssignedAt = new float[0];
        private readonly HashSet<int> m_assignedTargets = new HashSet<int>();
        private Vector3 m_position;
        private Quaternion m_rotation;
        private float m_legExtensionStartedAt;
        private float m_nextShotAt;
        private float m_attitudeTurretsReadyAt;
        private int m_nextShotTurret;

        private void Start()
        {
            m_legExtensionStartedAt = Time.time;
            m_nextShotAt = Time.time;
            m_stabilityBody = GetComponent<Rigidbody>();
            if (m_stabilityBody != null)
            {
                m_stabilityBody.linearDamping = Mathf.Max(m_stabilityBody.linearDamping, STABILIZER_LINEAR_DAMPING);
                m_stabilityBody.angularDamping = Mathf.Max(m_stabilityBody.angularDamping, STABILIZER_ANGULAR_DAMPING);
                m_stabilityBody.maxAngularVelocity = Mathf.Min(m_stabilityBody.maxAngularVelocity, MAX_STABILIZED_ANGULAR_SPEED);
            }
            GetRotJoints(CacheRotJointReferences);
            GetTurrets(CacheTurretReferences);
            ResolveAttackArmJointIds();
            ResolveAttackArmTurretIds();
            ResolveAttitudeTurretIds();
            m_nextShotTurret = m_leftArmTurretId;
            m_attitudeTurretsReadyAt = Time.time + 0.5f;
            InitializeAttackArmLinks();
            IgnoreArmRamCollisions();
        }

        private void FixedUpdate()
        {
            if (m_stabilityBody == null) return;

            Vector3 velocity = m_stabilityBody.linearVelocity;
            Vector3 planarVelocity = new Vector3(velocity.x, 0.0f, velocity.z);
            if (planarVelocity.sqrMagnitude > MAX_STABILIZED_PLANAR_SPEED * MAX_STABILIZED_PLANAR_SPEED)
                planarVelocity = planarVelocity.normalized * MAX_STABILIZED_PLANAR_SPEED;

            velocity.x = planarVelocity.x;
            velocity.z = planarVelocity.z;
            velocity.y = Mathf.Clamp(velocity.y, -MAX_STABILIZED_VERTICAL_SPEED, MAX_STABILIZED_VERTICAL_SPEED);
            m_stabilityBody.linearVelocity = velocity;
            m_stabilityBody.angularVelocity = Vector3.ClampMagnitude(
                m_stabilityBody.angularVelocity, MAX_STABILIZED_ANGULAR_SPEED);

            Vector3 uprightError = GetUprightRotationError(m_stabilityBody.transform);
            Vector3 tiltAngularVelocity = Vector3.ProjectOnPlane(m_stabilityBody.angularVelocity, Vector3.up);
            Vector3 uprightTorque = uprightError * m_uprightCorrectionStrength
                - tiltAngularVelocity * m_uprightAngularDamping;
            m_stabilityBody.AddTorque(uprightTorque, ForceMode.Acceleration);

            Vector3 planarPosition = Flatten(m_stabilityBody.position);
            float radius = planarPosition.magnitude;
            float returnRadius = GameConstants.ABOUT_GAME_FIELD_RADIUS * m_returnRadiusRatio;
            if (radius > returnRadius && radius > 0.001f)
            {
                Vector3 inward = -planarPosition / radius;
                float inwardSpeed = Vector3.Dot(planarVelocity, inward);
                float desiredInwardSpeed = Mathf.Min(7.0f, 3.0f + (radius - returnRadius) * 0.75f);
                float returnAcceleration = Mathf.Clamp(
                    (desiredInwardSpeed - inwardSpeed) * 2.0f,
                    -m_returnAcceleration,
                    m_returnAcceleration);
                m_stabilityBody.AddForce(inward * returnAcceleration, ForceMode.Acceleration);
            }
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
            ControlAttitudeTurrets();
            AimAndFireVolley(tanks);
            ExtendAttackArms(tanks);
        }

        /// <summary>Sample06「アメンボ」と同じ8個の回転ジョイントを4秒かけて展開する。</summary>
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
            if (!IsValidAttackArmJointId(m_leftAttackArmJointId)
                || !IsValidAttackArmJointId(m_rightAttackArmJointId)
                || !IsValidAttackArmJointId(m_centerAttackArmJointId)) return;

            AimAttackArm(tanks, 0, m_leftAttackArmJointId, m_leftArmTurretId, 70.0f);
            AimAttackArm(tanks, 1, m_rightAttackArmJointId, m_rightArmTurretId, -70.0f);
            AimAttackArm(tanks, 2, m_centerAttackArmJointId, m_centerArmTurretId, 180.0f);
        }

        private void AimAttackArm(TankInfo[] tanks, int armIndex, int jointId, int turretId, float retractAngle)
        {
            if (turretId < 0 || turretId >= m_targetByTurret.Length || turretId >= m_turretParts.Length)
            {
                RetractAttackArm(armIndex, jointId, turretId, retractAngle);
                return;
            }

            int targetId = m_targetByTurret[turretId];
            if (!IsTargetable(tanks, targetId))
            {
                RetractAttackArm(armIndex, jointId, turretId, retractAngle);
                return;
            }

            RotJointPart attackJoint = m_rotJointParts[jointId];
            TurretPart turret = m_turretParts[turretId];
            if (attackJoint == null || turret == null)
            {
                RetractAttackArm(armIndex, jointId, turretId, retractAngle);
                return;
            }

            Transform[] armLinks = GetAttackArmLinks(armIndex);
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
            SetAttackArmLength(armIndex, armLinks, turret.transform, armLength, pitchAngle);
        }

        private void RetractAttackArm(int armIndex, int jointId, int turretId, float retractAngle)
        {
            SXG_RotateJointToAngle(jointId, retractAngle);

            if (turretId < 0 || turretId >= m_turretParts.Length || m_turretParts[turretId] == null)
                return;

            RotJointPart attackJoint = m_rotJointParts[jointId];
            if (attackJoint == null) return;

            Transform[] armLinks = GetAttackArmLinks(armIndex);
            if (armLinks != null)
                SetAttackArmLength(armIndex, armLinks, m_turretParts[turretId].transform, MIN_ATTACK_ARM_LENGTH, 0.0f);
        }

        private void InitializeAttackArmLinks()
        {
            for (int armIndex = 0; armIndex < m_attackArmLinks.Length; armIndex++)
            {
                int jointId = GetAttackArmJointId(armIndex);
                if (!IsValidAttackArmJointId(jointId)) continue;

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

        /// <summary>砲塔が敵に触れて機体を弾き飛ばさないよう、接触だけを無効にする。</summary>
        private void IgnoreArmRamCollisions()
        {
            if (m_stabilityBody == null) return;

            Collider[] sceneColliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            for (int turretId = 0; turretId < m_turretParts.Length; turretId++)
            {
                if (!IsAttackArmTurret(turretId)) continue;
                if (turretId >= m_turretParts.Length || m_turretParts[turretId] == null) continue;

                Collider[] armTurretColliders = m_turretParts[turretId].GetComponentsInChildren<Collider>(true);
                foreach (Collider armCollider in armTurretColliders)
                {
                    if (armCollider == null) continue;
                    foreach (Collider sceneCollider in sceneColliders)
                    {
                        if (sceneCollider == null || sceneCollider.attachedRigidbody == m_stabilityBody) continue;
                        BaseTank otherTank = sceneCollider.GetComponentInParent<BaseTank>();
                        if (otherTank == null) continue;
                        Physics.IgnoreCollision(armCollider, sceneCollider, true);
                    }
                }
            }
        }

        private Transform[] GetAttackArmLinks(int armIndex)
        {
            if (armIndex < 0 || armIndex >= m_attackArmLinks.Length) return null;
            return m_attackArmLinks[armIndex];
        }

        private void SetAttackArmLength(int armIndex, Transform[] links, Transform turretTransform, float targetLength, float pitchAngle)
        {
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

        private void ResolveAttackArmJointIds()
        {
            m_leftAttackArmJointId = FindRotJointId(m_leftAttackArmJoint, DEFAULT_LEFT_ATTACK_ARM_JOINT_ID);
            m_rightAttackArmJointId = FindRotJointId(m_rightAttackArmJoint, DEFAULT_RIGHT_ATTACK_ARM_JOINT_ID);
            m_centerAttackArmJointId = FindRotJointId(m_centerAttackArmJoint, DEFAULT_CENTER_ATTACK_ARM_JOINT_ID);
        }

        private int FindRotJointId(RotJointPart selectedJoint, int fallbackId)
        {
            if (selectedJoint == null) return fallbackId;

            for (int jointId = 0; jointId < m_rotJointParts.Length; jointId++)
            {
                if (m_rotJointParts[jointId] == selectedJoint) return jointId;
            }

            return fallbackId;
        }

        private int GetAttackArmJointId(int armIndex)
        {
            switch (armIndex)
            {
                case 0: return m_leftAttackArmJointId;
                case 1: return m_rightAttackArmJointId;
                case 2: return m_centerAttackArmJointId;
                default: return -1;
            }
        }

        private bool IsValidAttackArmJointId(int jointId)
        {
            return jointId >= 0 && jointId < m_rotJointParts.Length && m_rotJointParts[jointId] != null;
        }

        private void CacheTurretReferences(TurretPart[] turrets)
        {
            m_turretParts = turrets ?? new TurretPart[0];
        }

        private void ResolveAttackArmTurretIds()
        {
            m_leftArmTurretId = FindTurretId(m_leftArmTurret, DEFAULT_LEFT_ARM_TURRET_ID);
            m_rightArmTurretId = FindTurretId(m_rightArmTurret, DEFAULT_RIGHT_ARM_TURRET_ID);
            m_centerArmTurretId = FindTurretId(m_centerArmTurret, DEFAULT_CENTER_ARM_TURRET_ID);
        }

        private void ResolveAttitudeTurretIds()
        {
            m_leftAttitudeTurretId = FindTurretId(m_leftAttitudeTurret, DEFAULT_LEFT_ATTITUDE_TURRET_ID);
            m_rightAttitudeTurretId = FindTurretId(m_rightAttitudeTurret, DEFAULT_RIGHT_ATTITUDE_TURRET_ID);
        }

        private int FindTurretId(TurretPart selectedTurret, int fallbackId)
        {
            if (selectedTurret == null) return fallbackId;

            for (int turretId = 0; turretId < m_turretParts.Length; turretId++)
            {
                if (m_turretParts[turretId] == selectedTurret) return turretId;
            }

            return fallbackId;
        }

        /// <summary>敵を砲門ごとに割り当て、照準が追いついた砲門から発射する。</summary>
        private void AimAndFireVolley(TankInfo[] tanks)
        {
            int turretCount = SXG_GetCountOfMyTurrets();
            if (turretCount <= 0) return;
            EnsureTurretTargets(turretCount);

            m_assignedTargets.Clear();
            // 腕の3砲塔を先に割り当て、敵が3台いる場合はそれぞれ別の敵を狙わせる。
            for (int priorityPass = 0; priorityPass < 2; priorityPass++)
            {
                bool prioritizeArms = priorityPass == 0;
                for (int turretId = 0; turretId < turretCount; turretId++)
                {
                    if (IsAttitudeControlTurret(turretId))
                    {
                        m_targetByTurret[turretId] = -1;
                        continue;
                    }
                    if (IsAttackArmTurret(turretId) != prioritizeArms) continue;

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
            }

            // 同フレームに複数砲を撃たず、反動を一発ずつに分散する。
            if (Time.time < m_nextShotAt) return;
            for (int shotOrder = 0; shotOrder < turretCount; shotOrder++)
            {
                int turretId = (m_nextShotTurret + shotOrder) % turretCount;
                if (IsAttitudeControlTurret(turretId)) continue;
                int targetId = m_targetByTurret[turretId];
                if (targetId < 0 || !SXG_CanShoot(turretId)) continue;
                if (Time.time - m_targetAssignedAt[turretId] < TURRET_AIM_SETTLE_TIME) continue;
                if (!IsAttackArmReadyToShoot(turretId, targetId, tanks)) continue;

                SXG_Shoot(turretId);
                m_nextShotAt = Time.time + SHOT_STAGGER_INTERVAL;
                m_nextShotTurret = (turretId + 1) % turretCount;

                if (m_enemyTracks != null && targetId < m_enemyTracks.Length)
                {
                    EnemyTrack track = m_enemyTracks[targetId];
                    track.LastEngagedAt = Time.time;
                    m_enemyTracks[targetId] = track;
                }

                break;
            }
        }

        private bool IsAttackArmTurret(int turretId)
        {
            return turretId == m_leftArmTurretId
                || turretId == m_rightArmTurretId
                || turretId == m_centerArmTurretId;
        }

        private bool IsAttitudeControlTurret(int turretId)
        {
            return turretId == m_leftAttitudeTurretId || turretId == m_rightAttitudeTurretId;
        }

        private bool IsValidAttitudeTurretId(int turretId)
        {
            return turretId >= 0 && turretId < m_turretParts.Length && m_turretParts[turretId] != null;
        }

        private bool IsAttackArmReadyToShoot(int turretId, int targetId, TankInfo[] tanks)
        {
            int armIndex = GetAttackArmIndexForTurret(turretId);
            if (armIndex < 0) return true;

            int jointId = GetAttackArmJointId(armIndex);
            if (!IsValidAttackArmJointId(jointId) || m_attackArmLinks[armIndex] == null) return false;

            Vector3 targetPosition = GetPredictedAimPoint(tanks[targetId], targetId);
            float requiredLength = Mathf.Clamp(
                Vector3.Distance(m_rotJointParts[jointId].transform.position, targetPosition) + 0.8f,
                MIN_ATTACK_ARM_LENGTH,
                MAX_ATTACK_ARM_LENGTH);
            return m_attackArmLengths[armIndex] >= requiredLength - 1.0f;
        }

        private int GetAttackArmIndexForTurret(int turretId)
        {
            if (turretId == m_leftArmTurretId) return 0;
            if (turretId == m_rightArmTurretId) return 1;
            if (turretId == m_centerArmTurretId) return 2;
            return -1;
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

        /// <summary>左右の下向き砲塔をロール補正に使い、発射時の反動で傾きを戻す。</summary>
        private void ControlAttitudeTurrets()
        {
            if (!IsValidAttitudeTurretId(m_leftAttitudeTurretId)
                || !IsValidAttitudeTurretId(m_rightAttitudeTurretId)
                || m_leftAttitudeTurretId == m_rightAttitudeTurretId) return;

            // 砲身を下向きに保つ。下向き弾の反動は機体を押し上げる。
            SXG_RotateTurretToAngle(m_leftAttitudeTurretId, 0.0f, 0.0f);
            SXG_RotateTurretToAngle(m_rightAttitudeTurretId, 0.0f, 0.0f);
            if (Time.time < m_attitudeTurretsReadyAt || m_stabilityBody == null) return;

            Vector3 heading = Vector3.ProjectOnPlane(m_stabilityBody.transform.forward, Vector3.up);
            if (heading.sqrMagnitude < 0.001f) return;
            heading.Normalize();

            Vector3 rollCorrection = Vector3.Cross(m_stabilityBody.transform.up, Vector3.up);
            float rollError = Vector3.Dot(rollCorrection, heading);
            if (Mathf.Abs(rollError) < m_rollCorrectionThreshold) return;

            float rollRate = Vector3.Dot(m_stabilityBody.angularVelocity, heading);
            // 連続トルクがすでに傾きを戻している間は砲撃しない。
            if (rollError * rollRate > 0.0f) return;
            if (Time.time < m_nextShotAt) return;

            int correctionTurretId = rollError < 0.0f ? m_leftAttitudeTurretId : m_rightAttitudeTurretId;
            if (!SXG_CanShoot(correctionTurretId)) return;

            SXG_Shoot(correctionTurretId);
            m_nextShotAt = Time.time + SHOT_STAGGER_INTERVAL;
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

        private static Vector3 GetUprightRotationError(Transform bodyTransform)
        {
            Vector3 bodyUp = bodyTransform.up;
            Vector3 axis = Vector3.Cross(bodyUp, Vector3.up);
            float upDot = Mathf.Clamp(Vector3.Dot(bodyUp, Vector3.up), -1.0f, 1.0f);

            if (axis.sqrMagnitude < 0.000001f)
            {
                if (upDot > 0.0f) return Vector3.zero;

                // 180度ひっくり返った時は外積がゼロになるので、機体前方向を軸に起こす。
                axis = Vector3.ProjectOnPlane(bodyTransform.forward, Vector3.up);
                if (axis.sqrMagnitude < 0.000001f)
                    axis = Vector3.ProjectOnPlane(bodyTransform.right, Vector3.up);
                if (axis.sqrMagnitude < 0.000001f) axis = Vector3.forward;
                return axis.normalized * Mathf.PI;
            }

            float angle = Mathf.Acos(upDot);
            return axis.normalized * angle;
        }
    }
}
