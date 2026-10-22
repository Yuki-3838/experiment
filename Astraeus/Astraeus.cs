using UnityEngine;
using SXG2025;

namespace Astraeus
{
    /// <summary>
    /// 相手の行動と距離に合わせて、標的・射程・回避方向を組み替える戦術AI。
    /// </summary>
    public sealed class Astraeus : ComPlayerBase
    {
        private const int PLAYER_COUNT = 4;
        private const float SHELL_SPEED = 38.0f;
        private const float SHELL_GRAVITY_SCALE = 2.0f;
        private const float AIM_HEIGHT_OFFSET = 0.5f;
        private const float MAX_TRACKED_SPEED = 22.0f;
        private const float DESIRED_RANGE = 24.0f;
        private const float MIN_RANGE = 18.0f;
        private const float MAX_RANGE = 38.0f;
        private const float MAX_FIRE_RANGE = 80.0f;
        private const float FIRE_ERROR_RADIUS = 4.0f;
        private const float SWITCH_TARGET_MARGIN = 0.65f;
        private const float OBSTACLE_SCAN_INTERVAL = 0.20f;
        private const float OBSTACLE_SCAN_DISTANCE = 16.0f;
        private const float OBSTACLE_CLEARANCE = 4.8f;
        private const float OBSTACLE_LOOKAHEAD = 14.0f;
        private const float PROGRESS_CHECK_INTERVAL = 0.70f;
        private const float MIN_PROGRESS_DISTANCE = 0.35f;
        private const float ESCAPE_DURATION = 1.25f;
        private const float ESCAPE_COOLDOWN = 1.8f;

        private struct EnemyTrack
        {
            public bool HasPosition;
            public Vector3 Position;
            public Vector3 Velocity;
            public float TurnRate;
            public float LastSeenTime;
        }

        private TurretPart[] m_cachedTurrets = new TurretPart[0];
        private readonly EnemyTrack[] m_enemyTracks = new EnemyTrack[PLAYER_COUNT];
        private Vector3 m_selfPosition;
        private Quaternion m_selfRotation;
        private int m_targetId = -1;
        private float m_targetSelectedAt;
        private float m_randomPhase;
        private Vector3[] m_obstaclePositions = new Vector3[0];
        private readonly RaycastHit[] m_shotPathHits = new RaycastHit[16];
        private readonly Collider[] m_shotPathOverlapHits = new Collider[16];
        private float m_nextObstacleScanTime;
        private bool m_shotPathBlocked;
        private Vector3 m_shotBlockingObstacle;
        private Vector3 m_shotTargetPoint;
        private Vector3 m_lastMuzzlePosition;
        private Vector3 m_progressSamplePosition;
        private float m_nextProgressCheckTime;
        private float m_escapeUntil;
        private float m_escapeCooldownUntil;
        private int m_stagnantChecks;
        private int m_escapeSign = 1;
        private bool m_hasProgressSample;
        private float m_nextShotTime;
        private int m_nextGun;
        private readonly float[] m_nextWeakPointScan = new float[PLAYER_COUNT];
        private readonly Vector3[] m_weakPointOffsets = new Vector3[PLAYER_COUNT];
        private readonly float[] m_weakPointQuality = new float[PLAYER_COUNT];
        private readonly RaycastHit[] m_visionHits = new RaycastHit[64];
        private readonly Collider[] m_nearShells = new Collider[64];
        private Rigidbody m_body;
        private TankInfo[] m_tacticalTanks;

        private void Start()
        {
            GetTurrets(CacheTurrets);
            m_body = GetComponentInParent<Rigidbody>();
            SXG_GetPositionAndRotation(out m_selfPosition, out m_selfRotation);
            m_randomPhase = Mathf.Atan2(m_selfPosition.z, m_selfPosition.x);
        }

        private void Update()
        {
            TankInfo[] tanks = SXG_GetAllTanksInfo();
            if (tanks == null || tanks.Length < PLAYER_COUNT)
                return;
            m_tacticalTanks = tanks;

            SXG_GetPositionAndRotation(out m_selfPosition, out m_selfRotation);
            UpdateEnemyTracks(tanks);

            int nextTarget = SelectTarget(tanks, m_targetId);
            if (nextTarget != m_targetId) m_targetSelectedAt = Time.time;
            m_targetId = nextTarget;
            if (IsEnemyAvailable(tanks, m_targetId))
            {
                AimAndFire(tanks[m_targetId]);
                DriveTactically(tanks[m_targetId]);
            }
            else
            {
                DriveToSafety();
            }
        }

        private void CacheTurrets(TurretPart[] turrets)
        {
            m_cachedTurrets = turrets ?? new TurretPart[0];
        }

        /// <summary>位置差分を平滑化して、敵の平面移動と突進を追跡する。</summary>
        private void UpdateEnemyTracks(TankInfo[] tanks)
        {
            float now = Time.time;
            float dt = Mathf.Max(Time.deltaTime, 0.001f);

            for (int id = 1; id < PLAYER_COUNT; id++)
            {
                TankInfo enemy = tanks[id];
                EnemyTrack track = m_enemyTracks[id];

                if (enemy.IsDefeated)
                {
                    track.HasPosition = false;
                    track.Velocity = Vector3.zero;
                    m_enemyTracks[id] = track;
                    continue;
                }

                if (track.HasPosition)
                {
                    float elapsed = now - track.LastSeenTime;
                    if (elapsed > 0.0001f && elapsed < 1.5f)
                    {
                        Vector3 displacement = Flatten(enemy.Position - track.Position);
                        if (displacement.sqrMagnitude > 9.0f)
                        {
                            // Re-entry / respawn teleports must not become a huge false lead.
                            track.Velocity = Vector3.zero;
                        }
                        else
                        {
                            Vector3 measured = Vector3.ClampMagnitude(displacement / elapsed, MAX_TRACKED_SPEED);
                            float responsiveness = 1.0f - Mathf.Exp(-6.0f * dt);
                            Vector3 previousVelocity = track.Velocity;
                            track.Velocity = Vector3.Lerp(track.Velocity, measured, responsiveness);
                            float turnRate = previousVelocity.sqrMagnitude > 2.0f && track.Velocity.sqrMagnitude > 2.0f
                                ? Vector3.SignedAngle(previousVelocity, track.Velocity, Vector3.up)
                                    * Mathf.Deg2Rad / elapsed : 0;
                            track.TurnRate = Mathf.Lerp(track.TurnRate, Mathf.Clamp(turnRate, -1.8f, 1.8f), responsiveness);
                        }
                    }
                    else if (elapsed >= 1.5f)
                    {
                        track.Velocity = Vector3.zero;
                    }
                }
                else
                {
                    track.Velocity = Vector3.zero;
                }

                track.HasPosition = true;
                track.Position = enemy.Position;
                track.LastSeenTime = now;
                m_enemyTracks[id] = track;
            }
        }

        /// <summary>近さ、こちらへの脅威、残りエネルギーをまとめて評価する。</summary>
        private int SelectTarget(TankInfo[] tanks, int currentTarget)
        {
            if (IsEnemyAvailable(tanks, currentTarget) && Time.time - m_targetSelectedAt < 0.8f)
                return currentTarget;
            int bestId = -1;
            float bestScore = float.NegativeInfinity;
            float currentScore = float.NegativeInfinity;

            for (int id = 1; id < PLAYER_COUNT; id++)
            {
                TankInfo enemy = tanks[id];
                if (enemy.IsDefeated || enemy.IsInvincible)
                    continue;

                if (m_cachedTurrets.Length > 0 && m_cachedTurrets[0] != null)
                {
                    m_cachedTurrets[0].GetTopPositionAndRotation(out Vector3 probeOrigin, out _);
                    FindWeakPoint(enemy, probeOrigin, id);
                }

                Vector3 toSelf = Flatten(m_selfPosition - enemy.Position);
                float distance = toSelf.magnitude;
                Vector3 enemyForward = Flatten(enemy.Rotation * Vector3.forward);
                float facingThreat = 0.0f;
                if (distance > 0.01f && enemyForward.sqrMagnitude > 0.01f)
                    facingThreat = Mathf.Clamp01(Vector3.Dot(enemyForward.normalized, toSelf / distance));

                float energyPerTank = Mathf.Max(1.0f, enemy.CostOfOneTank);
                float remainingLives = enemy.Energy / energyPerTank;
                float finishBonus = 1.6f / (1.0f + Mathf.Max(0.0f, remainingLives));
                float closeBonus = Mathf.Clamp(34.0f - distance, -10.0f, 24.0f) * 0.045f;
                float threatBonus = facingThreat * 1.35f;
                float valueBonus = Mathf.Clamp(enemy.CostOfOneTank, 100, 1000) * 0.00035f;
                float exposureBonus = Mathf.Clamp(m_weakPointQuality[id], -10.0f, 10.0f) * 0.28f;
                float score = finishBonus + closeBonus + threatBonus + valueBonus + exposureBonus;

                if (id == currentTarget)
                {
                    currentScore = score;
                    score += SWITCH_TARGET_MARGIN;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = id;
                }
            }

            if (currentTarget > 0 && currentTarget < PLAYER_COUNT
                && !tanks[currentTarget].IsDefeated && !tanks[currentTarget].IsInvincible
                && currentScore + SWITCH_TARGET_MARGIN >= bestScore)
                return currentTarget;

            return bestId;
        }

        /// <summary>急所を探し、砲口からの弾道と移動先を解いて順番に発射する。</summary>
        private void AimAndFire(TankInfo enemy)
        {
            m_shotPathBlocked = false;
            m_shotBlockingObstacle = Vector3.zero;
            int turretCount = Mathf.Min(m_cachedTurrets.Length, SXG_GetCountOfMyTurrets());
            EnemyTrack track = m_enemyTracks[m_targetId];
            if (SXG_GetCountOfMyRotJoints() > 0)
            {
                Vector3 local = Quaternion.Inverse(m_selfRotation)
                    * (enemy.Position + PredictDisplacement(track, 0.45f) - m_selfPosition);
                SXG_RotateJointToAngle(0, Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg);
            }

            int firstGun = m_nextGun;
            for (int slot = 0; slot < turretCount; slot++)
            {
                int turretId = (firstGun + slot) % turretCount;
                TurretPart turret = m_cachedTurrets[turretId];
                if (turret == null || !turret.isActiveAndEnabled)
                    continue;

                turret.GetTopPositionAndRotation(out Vector3 muzzlePosition, out Quaternion muzzleRotation);
                Vector3 offset = FindWeakPoint(enemy, muzzlePosition, m_targetId);
                Vector3 aimPoint = enemy.Position + offset;
                Vector3 requiredDirection = Vector3.zero;
                float flightTime = 0;
                bool reachable = true;
                for (int iteration = 0; iteration < 4; iteration++)
                {
                    if (!SolveBallistic(muzzlePosition, aimPoint, out requiredDirection, out flightTime))
                    {
                        reachable = false;
                        break;
                    }
                    aimPoint = enemy.Position + offset
                        + PredictDisplacement(track, flightTime);
                }
                if (!reachable || !SolveBallistic(muzzlePosition, aimPoint, out requiredDirection, out flightTime))
                    continue;

                m_shotTargetPoint = aimPoint;
                m_lastMuzzlePosition = muzzlePosition;
                Vector3 localDirection = turret.transform.InverseTransformDirection(requiredDirection);
                float yaw = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Atan2(localDirection.y,
                    new Vector2(localDirection.x, localDirection.z).magnitude) * Mathf.Rad2Deg;
                SXG_RotateTurretToAngle(turretId, yaw, pitch);

                if (!SXG_CanShoot(turretId) || Time.time < m_nextShotTime || pitch < -44.5f || pitch > 9.8f)
                    continue;

                float alignment = Vector3.Angle(muzzleRotation * Vector3.forward, requiredDirection);
                float distance = Vector3.Distance(muzzlePosition, aimPoint);
                float allowedAlignment = Mathf.Clamp(
                    Mathf.Atan2(0.50f, Mathf.Max(distance, 1.0f)) * Mathf.Rad2Deg,
                    0.40f,
                    2.0f);
                if (distance <= MAX_FIRE_RANGE && alignment <= allowedAlignment)
                {
                    if (IsShotBlockedByOwnTank(muzzlePosition, muzzleRotation * Vector3.forward,
                            distance / SHELL_SPEED, turret.transform, out _))
                        continue;

                    if (IsShotBlockedByObstacle(muzzlePosition, requiredDirection, distance,
                            out Vector3 obstaclePosition))
                    {
                        m_shotPathBlocked = true;
                        m_shotBlockingObstacle = obstaclePosition;
                        continue;
                    }
                    SXG_Shoot(turretId);
                    // Separate projectiles and distribute recoil instead of releasing one converging volley.
                    m_nextShotTime = Time.time + 0.36f;
                    m_nextGun = (turretId + 1) % turretCount;
                }
            }
        }

        private static Vector3 PredictDisplacement(EnemyTrack track, float time)
        {
            float omega = track.TurnRate;
            if (Mathf.Abs(omega) < 0.05f)
                return Vector3.ClampMagnitude(track.Velocity * time, 14.0f);
            // Integrate a turning velocity instead of extending a tangent through a corner.
            Vector3 velocity = Quaternion.AngleAxis(omega * 0.10f * Mathf.Rad2Deg, Vector3.up) * track.Velocity;
            Vector3 side = Vector3.Cross(Vector3.up, velocity);
            float angle = omega * time;
            return Vector3.ClampMagnitude(
                (velocity * Mathf.Sin(angle) + side * (1.0f - Mathf.Cos(angle))) / omega, 14.0f);
        }

        private static bool SolveBallistic(Vector3 muzzle, Vector3 target, out Vector3 direction, out float flightTime)
        {
            Vector3 delta = target - muzzle;
            Vector3 flat = Flatten(delta);
            float range = flat.magnitude;
            float gravity = -Physics.gravity.y * SHELL_GRAVITY_SCALE;
            float speedSquared = SHELL_SPEED * SHELL_SPEED;
            float discriminant = speedSquared * speedSquared
                - gravity * (gravity * range * range + 2.0f * delta.y * speedSquared);
            direction = Vector3.zero;
            flightTime = 0;
            if (range < 0.1f || discriminant <= 0) return false;
            float tangent = (speedSquared - Mathf.Sqrt(discriminant)) / (gravity * range);
            float cosine = 1.0f / Mathf.Sqrt(1.0f + tangent * tangent);
            direction = flat / range * cosine + Vector3.up * (tangent * cosine);
            flightTime = range / (SHELL_SPEED * cosine);
            return true;
        }

        private Vector3 FindWeakPoint(TankInfo enemy, Vector3 muzzle, int enemyId)
        {
            if (Time.time < m_nextWeakPointScan[enemyId]) return m_weakPointOffsets[enemyId];
            m_nextWeakPointScan[enemyId] = Time.time + 0.30f;
            Vector3 side = Vector3.Cross(Vector3.up, Flatten(enemy.Position - muzzle)).normalized;
            float bestScore = float.NegativeInfinity;
            Vector3 best = Vector3.zero;
            float[] heights = { 0.08f, -0.12f, 0.35f, 0.70f, 1.05f, 1.32f };
            foreach (float height in heights)
            for (int horizontal = -1; horizontal <= 1; horizontal++)
            {
                Vector3 offset = side * (horizontal * 0.82f) + Vector3.up * height;
                Vector3 target = enemy.Position + offset;
                if (!SolveBallistic(muzzle, target, out Vector3 direction, out float time)) continue;
                float score = ScoreVisibleImpact(muzzle, direction, time, enemy.Position)
                    - Mathf.Abs(horizontal) * 0.025f - Mathf.Abs(height) * 0.01f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = offset;
                }
                if (horizontal == 0 && score > 9.9f)
                {
                    m_weakPointOffsets[enemyId] = best;
                    m_weakPointQuality[enemyId] = bestScore;
                    return best;
                }
            }
            m_weakPointOffsets[enemyId] = best;
            m_weakPointQuality[enemyId] = bestScore;
            return best;
        }

        private float ScoreVisibleImpact(Vector3 start, Vector3 direction, float flightTime, Vector3 targetPosition)
        {
            Transform ownRoot = transform.parent != null ? transform.parent : transform;
            Vector3 gravity = Physics.gravity * SHELL_GRAVITY_SCALE;
            Vector3 previous = start;
            int shellMask = 1 << LayerMask.NameToLayer("Shell");
            for (float time = 0.06f; time < flightTime + 0.10f; time += 0.06f)
            {
                Vector3 next = start + direction * SHELL_SPEED * time + gravity * (0.5f * time * time);
                Vector3 segment = next - previous;
                float length = segment.magnitude;
                int count = Physics.SphereCastNonAlloc(previous, 0.23f, segment / length,
                    m_visionHits, length, ~shellMask, QueryTriggerInteraction.Ignore);
                Collider first = null;
                float nearest = float.PositiveInfinity;
                for (int i = 0; i < count; i++)
                {
                    var hit = m_visionHits[i];
                    if (hit.collider == null || hit.transform.IsChildOf(ownRoot)
                        || (hit.collider.excludeLayers.value & shellMask) != 0) continue;
                    if (hit.distance < nearest)
                    {
                        nearest = hit.distance;
                        first = hit.collider;
                    }
                }
                if (first != null)
                {
                    var tank = first.attachedRigidbody != null ? first.attachedRigidbody.GetComponent<BaseTank>() : null;
                    if (tank == null || Vector3.Distance(tank.transform.position, targetPosition) > 4.0f) return -20;
                    if (first.GetComponentInParent<TurretPart>() != null) return -10;
                    // Armor may also use CapsuleCollider. Vulnerable stock chassis/pilot
                    // colliders belong to BaseTank, outside the participant's part hierarchy.
                    if (first.GetComponentInParent<ComPlayerBase>() == null) return 10;
                    return 1;
                }
                previous = next;
            }
            return -5;
        }

        /// <summary>発射直後に自車の装甲や別砲塔へ当たる射線は撃たない。</summary>
        private bool IsShotBlockedByOwnTank(
            Vector3 start,
            Vector3 launchDirection,
            float flightTime,
            Transform shooterTurret,
            out string blockerName)
        {
            blockerName = null;
            Transform ownTankRoot = transform.parent != null ? transform.parent : transform;
            const float shellRadius = 0.32f;
            int shellLayer = LayerMask.NameToLayer("Shell");
            int shellLayerMask = shellLayer >= 0 ? 1 << shellLayer : 0;

            int overlapCount = Physics.OverlapSphereNonAlloc(
                start, shellRadius, m_shotPathOverlapHits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlapCount; i++)
            {
                if (IsBlockingOwnCollider(m_shotPathOverlapHits[i], ownTankRoot, shooterTurret, shellLayerMask))
                {
                    blockerName = m_shotPathOverlapHits[i].name;
                    return true;
                }
            }

            float probeTime = Mathf.Min(0.14f, flightTime);
            Vector3 gravity = Physics.gravity * SHELL_GRAVITY_SCALE;
            Vector3 previous = start;
            for (float time = Mathf.Min(0.08f, probeTime); time > 0.0f;
                time = Mathf.Min(time + 0.08f, probeTime))
            {
                Vector3 next = start + launchDirection * SHELL_SPEED * time
                    + gravity * (0.5f * time * time);
                Vector3 segment = next - previous;
                float segmentLength = segment.magnitude;
                if (segmentLength > 0.001f)
                {
                    int hitCount = Physics.SphereCastNonAlloc(
                        previous, shellRadius, segment / segmentLength, m_shotPathHits,
                        segmentLength, ~0, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < hitCount; i++)
                    {
                        Collider hit = m_shotPathHits[i].collider;
                        if (IsBlockingOwnCollider(hit, ownTankRoot, shooterTurret, shellLayerMask))
                        {
                            blockerName = hit.name;
                            return true;
                        }
                    }
                }

                previous = next;
                if (time >= probeTime)
                    break;
            }

            return false;
        }

        private static bool IsBlockingOwnCollider(
            Collider collider,
            Transform ownTankRoot,
            Transform shooterTurret,
            int shellLayerMask)
        {
            if (collider == null || collider.isTrigger)
                return false;

            Transform colliderTransform = collider.transform;
            if (colliderTransform != ownTankRoot && !colliderTransform.IsChildOf(ownTankRoot))
                return false;
            if (colliderTransform == shooterTurret || colliderTransform.IsChildOf(shooterTurret))
                return false;
            if ((collider.excludeLayers.value & shellLayerMask) != 0)
                return false;
            if (shellLayerMask != 0 && Physics.GetIgnoreLayerCollision(
                    LayerMask.NameToLayer("Shell"), collider.gameObject.layer)
                && (collider.includeLayers.value & shellLayerMask) == 0)
                return false;
            return true;
        }

        /// <summary>実弾の放物線を短い区間に分け、岩が先に弾へ当たる射線を除外する。</summary>
        private bool IsShotBlockedByObstacle(
            Vector3 muzzlePosition,
            Vector3 launchDirection,
            float distance,
            out Vector3 obstaclePosition)
        {
            obstaclePosition = Vector3.zero;
            float flightTime = distance / SHELL_SPEED;
            Vector3 gravity = Physics.gravity * SHELL_GRAVITY_SCALE;
            Vector3 previous = muzzlePosition;
            int groundMask = LayerMask.GetMask("Ground");

            for (float time = Mathf.Min(0.08f, flightTime); time <= flightTime + 0.001f;
                time = Mathf.Min(time + 0.08f, flightTime))
            {
                Vector3 next = muzzlePosition + launchDirection * SHELL_SPEED * time
                    + gravity * (0.5f * time * time);
                Vector3 segment = next - previous;
                float segmentLength = segment.magnitude;
                if (segmentLength > 0.001f)
                {
                    int hitCount = Physics.SphereCastNonAlloc(
                        previous,
                        0.28f,
                        segment / segmentLength,
                        m_shotPathHits,
                        segmentLength,
                        groundMask,
                        QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < hitCount; i++)
                    {
                        Collider hit = m_shotPathHits[i].collider;
                        if (hit == null || hit.transform.root == transform.root)
                            continue;

                        Transform part = hit.transform;
                        while (part != null)
                        {
                            if (part.CompareTag("Obstacle"))
                            {
                                obstaclePosition = hit.transform.position;
                                return true;
                            }
                            if (part == part.root)
                                break;
                            part = part.parent;
                        }
                    }
                }

                previous = next;
                if (time >= flightTime)
                    break;
            }

            return false;
        }

        /// <summary>砲弾の落下と砲塔側の照準オフセットを含めた実射線を求める。</summary>
        private static Vector3 GetRequiredLaunchDirection(TurretPart turret, Vector3 predictedPosition)
        {
            Vector3 localTarget = turret.transform.InverseTransformPoint(
                predictedPosition + Vector3.up * AIM_HEIGHT_OFFSET);
            float flightTime = localTarget.magnitude / SHELL_SPEED;
            localTarget -= Physics.gravity * SHELL_GRAVITY_SCALE * (0.5f * flightTime * flightTime);
            Vector3 worldDirection = turret.transform.TransformDirection(localTarget.normalized);
            return worldDirection.sqrMagnitude > 0.001f ? worldDirection.normalized : Vector3.forward;
        }

        /// <summary>遠距離では強く詰め、近距離では一定方向に旋回して敵の射撃を外させる。</summary>
        private void DriveTactically(TankInfo enemy)
        {
            Vector3 toEnemy = Flatten(enemy.Position - m_selfPosition);
            float distance = toEnemy.magnitude;
            if (distance < 0.01f)
            {
                DriveToSafety();
                return;
            }

            Vector3 flatPosition = Flatten(m_selfPosition);
            float fieldRadius = GameConstants.ABOUT_GAME_FIELD_RADIUS;
            if (flatPosition.magnitude > fieldRadius * 0.88f)
            {
                DriveVector(-flatPosition.normalized);
                return;
            }

            Vector3 towardEnemy = toEnemy / distance;
            Vector3 tangent = Vector3.Cross(Vector3.up, towardEnemy).normalized;
            float orbitSign = Mathf.Sin(m_randomPhase) >= 0.0f ? 1.0f : -1.0f;

            Vector3 enemyForward = Flatten(enemy.Rotation * Vector3.forward).normalized;
            Vector3 toSelf = -towardEnemy;
            float flankProgress = Vector3.Dot(tangent, enemyForward);
            if (Mathf.Abs(flankProgress) > 0.12f)
                orbitSign = -Mathf.Sign(flankProgress);
            bool enemyPressuring = Vector3.Dot(enemyForward, toSelf) > 0.45f
                || Vector3.Dot(m_enemyTracks[m_targetId].Velocity, toSelf) > 4.0f;

            float radial = Mathf.Clamp((distance - DESIRED_RANGE) / 8.0f, -1.0f, 1.0f);
            float orbitWeight = distance > MAX_RANGE ? 0.18f : 0.72f;
            Vector3 desired = towardEnemy * radial + tangent * orbitSign * orbitWeight;
            desired += tangent * (0.12f * Mathf.Sin(Time.time * 1.9f + m_randomPhase));

            if (m_shotPathBlocked)
            {
                Vector3 flank = ChooseClearFlankDirection(towardEnemy);
                if (flank.sqrMagnitude > 0.01f)
                    desired = flank * 2.0f + towardEnemy * (radial * 0.5f);
            }

            if (distance < MIN_RANGE)
                desired += -towardEnemy * (0.8f + (MIN_RANGE - distance) * 0.12f);
            else if (distance > MAX_RANGE)
                desired += towardEnemy * Mathf.Clamp((distance - MAX_RANGE) * 0.22f, 0.0f, 1.2f);

            if (enemyPressuring)
                desired += tangent * orbitSign * 0.65f - towardEnemy * 0.25f;

            float radius = flatPosition.magnitude;
            if (radius > fieldRadius * 0.70f && radius > 0.01f)
            {
                float edgeWeight = Mathf.Clamp01((radius - fieldRadius * 0.70f) / (fieldRadius * 0.22f));
                desired += -flatPosition.normalized * (1.0f + edgeWeight * 2.0f);
            }

            // Stay out of a second opponent's point-blank firing area while fighting.
            for (int id = 1; id < PLAYER_COUNT; id++)
            {
                if (id == m_targetId || m_tacticalTanks[id].IsDefeated) continue;
                Vector3 away = Flatten(m_selfPosition - m_tacticalTanks[id].Position);
                float separation = away.magnitude;
                if (separation > 0.1f && separation < 22.0f)
                    desired += away / separation * Mathf.Clamp01((22.0f - separation) / 12.0f);
            }
            DriveVector(desired);

        }

        /// <summary>岩で塞がれた射線の左右を仮想位置から調べ、抜け道がある側へ進む。</summary>
        private Vector3 ChooseClearFlankDirection(Vector3 towardEnemy)
        {
            Vector3 side = Vector3.Cross(Vector3.up, towardEnemy).normalized;
            if (side.sqrMagnitude < 0.01f)
                return Vector3.zero;

            const float flankDistance = 7.0f;
            bool leftBlocked = WouldShotBeBlockedFrom(
                m_lastMuzzlePosition + side * flankDistance,
                m_shotTargetPoint);
            bool rightBlocked = WouldShotBeBlockedFrom(
                m_lastMuzzlePosition - side * flankDistance,
                m_shotTargetPoint);

            if (leftBlocked != rightBlocked)
                return leftBlocked ? -side : side;

            Vector3 toBlocker = Flatten(m_shotBlockingObstacle - m_selfPosition);
            if (toBlocker.sqrMagnitude > 0.01f)
                return Vector3.Dot(side, toBlocker) > 0.0f ? -side : side;

            return side * Mathf.Sign(Mathf.Sin(m_randomPhase));
        }

        private bool WouldShotBeBlockedFrom(Vector3 muzzlePosition, Vector3 targetPoint)
        {
            Vector3 delta = targetPoint - muzzlePosition;
            float distance = delta.magnitude;
            float flightTime = distance / SHELL_SPEED;
            Vector3 launchDirection = (delta - Physics.gravity * SHELL_GRAVITY_SCALE
                * (0.5f * flightTime * flightTime)).normalized;
            return IsShotBlockedByObstacle(muzzlePosition, launchDirection, distance, out _);
        }

        /// <summary>敵が残っていない時は中央付近へ戻り、向きを保つ。</summary>
        private void DriveToSafety()
        {
            Vector3 flatPosition = Flatten(m_selfPosition);
            if (flatPosition.magnitude < 2.0f)
                SXG_SetCaterpillarPower(0.15f, 0.15f);
            else
                DriveVector(-flatPosition.normalized);
        }

        /// <summary>行きたい方向を旋回角に変換し、履帯出力を滑らかに決める。</summary>
        private void DriveVector(Vector3 desiredDirection)
        {
            desiredDirection = Flatten(desiredDirection);
            if (desiredDirection.sqrMagnitude < 0.001f)
            {
                SXG_SetCaterpillarPower(0.0f, 0.0f);
                return;
            }

            UpdateObstacleCache();
            desiredDirection = EvadeIncomingShells(desiredDirection);
            desiredDirection = AvoidObstacles(desiredDirection);
            if (UpdateStuckRecovery())
                return;

            desiredDirection.Normalize();
            Vector3 forward = Flatten(m_selfRotation * Vector3.forward);
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;
            forward.Normalize();

            float angle = Vector3.SignedAngle(forward, desiredDirection, Vector3.up);
            float turn = Mathf.Clamp(angle / 62.0f, -1.0f, 1.0f);
            float forwardPower = Mathf.Abs(angle) > 110.0f ? 0.0f : 1.0f;
            if (Mathf.Abs(angle) > 65.0f)
                forwardPower *= 0.25f;

            SXG_SetCaterpillarPower(
                Mathf.Clamp(forwardPower + turn * 0.82f, -1.0f, 1.0f),
                Mathf.Clamp(forwardPower - turn * 0.82f, -1.0f, 1.0f));
        }

        /// <summary>接近する実弾の衝突時刻を予測し、進行方向へ横移動を加える。</summary>
        private Vector3 EvadeIncomingShells(Vector3 desired)
        {
            if (m_body == null) return desired;
            Vector3 selfVelocity = Flatten(m_body.linearVelocity);
            int count = Physics.OverlapSphereNonAlloc(m_selfPosition, 20.0f, m_nearShells,
                LayerMask.GetMask("Shell"), QueryTriggerInteraction.Ignore);
            Vector3 dodge = Vector3.zero;
            float strongest = 0;
            for (int i = 0; i < count; i++)
            {
                Rigidbody projectile = m_nearShells[i] != null ? m_nearShells[i].attachedRigidbody : null;
                if (projectile == null || projectile.linearVelocity.sqrMagnitude < 100.0f) continue;
                Vector3 relativePosition = Flatten(projectile.position - m_selfPosition);
                Vector3 relativeVelocity = Flatten(projectile.linearVelocity) - selfVelocity;
                float closing = -Vector3.Dot(relativePosition, relativeVelocity);
                if (closing <= 0 || relativeVelocity.sqrMagnitude < 1) continue;
                float time = closing / relativeVelocity.sqrMagnitude;
                if (time < 0.06f || time > 0.70f) continue;
                Vector3 impact = projectile.position + projectile.linearVelocity * time
                    + Physics.gravity * (0.5f * SHELL_GRAVITY_SCALE * time * time);
                Vector3 futureSelf = m_selfPosition + selfVelocity * time;
                float miss = Flatten(impact - futureSelf).magnitude;
                if (miss > 2.6f || Mathf.Abs(impact.y - futureSelf.y - 0.45f) > 2.1f) continue;
                Vector3 side = Vector3.Cross(Vector3.up, relativeVelocity).normalized;
                float leftScore = Vector3.Dot(side, desired.normalized)
                    - Flatten(m_selfPosition + side * 4).magnitude * 0.045f;
                float rightScore = Vector3.Dot(-side, desired.normalized)
                    - Flatten(m_selfPosition - side * 4).magnitude * 0.045f;
                if (rightScore > leftScore) side = -side;
                float risk = (1.0f - miss / 3.0f) * (1.0f - time / 1.0f);
                if (risk > strongest) { strongest = risk; dodge = side; }
            }
            return strongest > 0 ? desired * (1.0f - strongest * 0.6f) + dodge * (strongest * 2.2f) : desired;
        }

        /// <summary>近傍の障害物を一定間隔で取得し、各候補進路の空きを評価する。</summary>
        private void UpdateObstacleCache()
        {
            if (Time.time < m_nextObstacleScanTime)
                return;

            m_obstaclePositions = SXG_GetHitObstacles(OBSTACLE_SCAN_DISTANCE);
            m_nextObstacleScanTime = Time.time + OBSTACLE_SCAN_INTERVAL;
        }

        /// <summary>先の岩まで見る簡易ローカル経路選択で、空いている側へ進路を曲げる。</summary>
        private Vector3 AvoidObstacles(Vector3 desiredDirection)
        {
            desiredDirection = Flatten(desiredDirection);
            if (desiredDirection.sqrMagnitude < 0.001f || m_obstaclePositions == null || m_obstaclePositions.Length == 0)
                return desiredDirection;

            desiredDirection.Normalize();
            float bestCost = float.PositiveInfinity;
            Vector3 bestDirection = desiredDirection;

            // -90～+90度の候補を比較し、障害物の少ない方へ進路をそらす。
            for (int angle = -90; angle <= 90; angle += 15)
            {
                Vector3 candidate = Quaternion.AngleAxis(angle, Vector3.up) * desiredDirection;
                float danger = 0.0f;
                Vector3 side = Vector3.Cross(Vector3.up, candidate);

                for (int i = 0; i < m_obstaclePositions.Length; i++)
                {
                    Vector3 toObstacle = Flatten(m_obstaclePositions[i] - m_selfPosition);
                    float along = Vector3.Dot(toObstacle, candidate);
                    if (along < -1.0f || along > OBSTACLE_LOOKAHEAD)
                        continue;

                    float lateral = Mathf.Abs(Vector3.Dot(toObstacle, side));
                    float clearanceRisk = Mathf.Clamp01((OBSTACLE_CLEARANCE - lateral) / OBSTACLE_CLEARANCE);
                    float distanceWeight = 1.0f - Mathf.Clamp01(Mathf.Max(0.0f, along) / OBSTACLE_LOOKAHEAD);
                    danger = Mathf.Max(danger, clearanceRisk * distanceWeight);
                }

                float deviationCost = Mathf.Abs(angle) / 90.0f * 1.05f;
                float tieBreak = angle * Mathf.Sign(Mathf.Sin(m_randomPhase)) * 0.0001f;
                float cost = deviationCost + danger * 2.2f + tieBreak;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestDirection = candidate;
                }
            }

            return bestDirection;
        }

        /// <summary>一定時間ほぼ動いていなければ、後退しながら旋回して岩から離れる。</summary>
        private bool UpdateStuckRecovery()
        {
            if (Time.time < m_escapeUntil)
            {
                float left = m_escapeSign > 0 ? -0.85f : -0.15f;
                float right = m_escapeSign > 0 ? -0.15f : -0.85f;
                SXG_SetCaterpillarPower(left, right);
                return true;
            }

            if (m_escapeUntil > 0.0f)
            {
                m_escapeUntil = 0.0f;
                m_escapeCooldownUntil = Time.time + ESCAPE_COOLDOWN;
                m_progressSamplePosition = m_selfPosition;
                m_nextProgressCheckTime = Time.time + PROGRESS_CHECK_INTERVAL;
                m_stagnantChecks = 0;
            }

            if (!m_hasProgressSample)
            {
                m_hasProgressSample = true;
                m_progressSamplePosition = m_selfPosition;
                m_nextProgressCheckTime = Time.time + PROGRESS_CHECK_INTERVAL;
                return false;
            }

            if (Time.time < m_nextProgressCheckTime)
                return false;

            float progress = Vector3.Distance(Flatten(m_selfPosition), Flatten(m_progressSamplePosition));
            m_progressSamplePosition = m_selfPosition;
            m_nextProgressCheckTime = Time.time + PROGRESS_CHECK_INTERVAL;

            if (progress < MIN_PROGRESS_DISTANCE)
                m_stagnantChecks++;
            else
                m_stagnantChecks = 0;

            if (m_stagnantChecks >= 2 && Time.time >= m_escapeCooldownUntil)
            {
                m_escapeSign = -m_escapeSign;
                m_escapeUntil = Time.time + ESCAPE_DURATION;
                m_stagnantChecks = 0;
                float left = m_escapeSign > 0 ? -0.85f : -0.15f;
                float right = m_escapeSign > 0 ? -0.15f : -0.85f;
                SXG_SetCaterpillarPower(left, right);
                return true;
            }

            return false;
        }

        private static bool IsEnemyAvailable(TankInfo[] tanks, int id)
        {
            return id > 0 && id < PLAYER_COUNT && !tanks[id].IsDefeated && !tanks[id].IsInvincible;
        }

        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0.0f;
            return value;
        }
    }
}
