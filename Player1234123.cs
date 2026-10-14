// このファイルは「かんたんAI作成」により自動生成されました。 (2026-10-13 06:56:42)

using UnityEngine;
using SXG2025;
using System.Collections.Generic;
using System.Text;

namespace Player1234123
{
    // AIの全体方針:
    // ・出撃時の位置と向きを基準に固定砲台として待機し、被弾などでずれたときだけ元へ戻る。
    // ・ジョイント付きパーツの配置・重さで車体のバランスを取り、砲撃反動による傾きを抑える狙い。
    //   AIからはジョイントを敵方向へ向けて脅威を分担する。発射ごとの反動を計算して逆向きに動かす処理ではない。
    // ・攻撃砲塔は敵の出撃コストを優先して割り振り、相手が動いていれば着弾位置を予測する。
    // ・自車の装甲に当たる砲塔は射線の通る相手へ回し、発射可否は標準の再装填判定に任せる。
    // ・Unity Editorでは狙い点と各砲塔の状態を確認できる。
    public class Player1234123 : ComPlayerBase
    {
        // 毎フレーム取得する自車情報。以降の移動・照準計算ではこの値を共通して使う。
        private Vector3 m_nowPosition;
        private Quaternion m_nowRotation;
        private int m_turretCount;

        // 砲塔と砲身のTransformは、回転状態を確認するために一度取得して再利用する。
        private TurretPart[] m_myTurrets = new TurretPart[0];
        private Transform[] m_barrelAimTransforms = new Transform[0];
        private Transform[] m_muzzleTransforms = new Transform[0];
        private bool[] m_blockedByOwnTankByTurret = new bool[0];
        private readonly Collider[] m_shotPathOverlapHits = new Collider[64];
        private readonly RaycastHit[] m_shotPathCastHits = new RaycastHit[64];

        // 砲塔番号ごとの照準点・標的。射撃先の記録とデバッグ表示に使う。
        private Vector3[] m_aimPointsByTurret = new Vector3[0];
        private bool[] m_hasAimPointByTurret = new bool[0];
        private int[] m_targetByTurret = new int[0];
        private TankInfo[] m_allTanksInfo;

        // 可動ジョイントの参照と、同じ敵へ集中しすぎないための割当数。
        // ジョイントに付く部品の向きや位置は車体のバランスにも関わるため、Prefab側では左右の配置も重要。
        private RotJointPart[] m_myRotJoints = new RotJointPart[0];
        private readonly int[] m_jointTargetUsageCounts = new int[GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE];

        // 前フレームの敵位置から速度を推定し、動く敵への先行照準に使う。
        private readonly Vector3[] m_previousEnemyPositions = new Vector3[GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE];
        private readonly Vector3[] m_enemyVelocities = new Vector3[GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE];
        private readonly bool[] m_hasPreviousEnemyPosition = new bool[GameConstants.MAX_PLAYER_COUNT_IN_ONE_BATTLE];

        private readonly List<int> m_enemyCandidateIds = new List<int>();
        private readonly List<int> m_unassignedTurretIds = new List<int>();
        private int[] m_highCostTargetByTurret = new int[0];
        private Vector3 m_anchorPosition;
        private Vector3 m_anchorForward;
        private bool m_anchorInitialized;
        private bool m_edgeRecoveryActive;
        private float m_currentAnchorDistance;
        private float m_currentOwnSpeed;
        private Vector3 m_smoothedOwnVelocity;
        // 被弾後の移動量から速度を見積もり、固定位置を大きく外れる前に補正を始める。
        private Vector3 m_previousOwnPosition;
        private bool m_hasPreviousOwnPosition;

        // DataTank.asset の現行砲弾速度。参加者APIからは設定値を読めない。
        private const float CANNON_SHELL_SPEED = 38.0f;

        private void Update()
        {
            // 毎フレーム、ゲームAPIから最新状態を取得してから移動・照準・射撃の順に処理する。
            SXG_GetPositionAndRotation(out m_nowPosition, out m_nowRotation);
            m_turretCount = SXG_GetCountOfMyTurrets();
            if (m_myTurrets.Length != m_turretCount) GetTurrets(CacheTurretReferences);
            m_allTanksInfo = SXG_GetAllTanksInfo();
            GetRotJoints(CacheRotJointReferences);
            UpdateEnemyVelocityEstimates();

            // 移動処理 
            DoMove();

            // 狙う 
            DoAim();

            // 射撃 
            DoFire();
        }

#region MOVE

        [Header("<Move (移動)>")]
        [SerializeField, Tooltip("この距離以上ずれたら固定位置へ戻り始める"), Range(0.1f, 3.0f)] float m_stationRecoveryStartDistance = 0.25f;
        [SerializeField, Tooltip("この速度以上で動いていたら、距離が小さくても固定位置への補正を始める"), Range(0.1f, 3.0f)] float m_stationRecoveryStartSpeed = 0.55f;
        [SerializeField, Tooltip("この距離以内に戻ったら位置補正を終える"), Range(0.05f, 1.0f)] float m_stationRecoveryResumeDistance = 0.08f;
        [SerializeField, Tooltip("基準の向きからこの角度以上ずれたら向きを戻す"), Range(1.0f, 45.0f)] float m_stationRecoveryStartAngle = 8.0f;
        [SerializeField, Tooltip("基準の向きとの差がこの角度以内なら向き補正を終える"), Range(0.5f, 15.0f)] float m_stationRecoveryResumeAngle = 2.0f;

        /// <summary>開始位置と向きを固定の基準にし、被弾や反動でずれたときだけ元へ戻す。</summary>
        void DoMove()
        {
            float fieldRadius = GameConstants.ABOUT_GAME_FIELD_RADIUS;
            if (!m_anchorInitialized)
            {
                // ゲーム側が決めた出撃位置そのものを固定点にする。
                // 人工的に内側へずらすと出撃直後から復帰扱いになり、埋め込み固定車体は射撃も止まり続ける。
                m_anchorPosition = new Vector3(m_nowPosition.x, m_nowPosition.y, m_nowPosition.z);

                m_anchorForward = m_nowRotation * Vector3.forward;
                m_anchorForward.y = 0.0f;
                if (m_anchorForward.sqrMagnitude < 0.0001f) m_anchorForward = Vector3.forward;
                m_anchorForward.Normalize();
                m_anchorInitialized = true;
            }

            Vector3 currentPosition = new Vector3(m_nowPosition.x, 0.0f, m_nowPosition.z);
            float currentRadius = currentPosition.magnitude;
            Vector3 anchorPosition = new Vector3(m_anchorPosition.x, 0.0f, m_anchorPosition.z);
            Vector3 fromAnchor = currentPosition - anchorPosition;
            m_currentAnchorDistance = fromAnchor.magnitude;

            Vector3 currentForward = m_nowRotation * Vector3.forward;
            currentForward.y = 0.0f;
            if (currentForward.sqrMagnitude < 0.0001f) currentForward = m_anchorForward;
            currentForward.Normalize();
            float headingError = Vector3.SignedAngle(currentForward, m_anchorForward, Vector3.up);

            Vector3 ownVelocity = Vector3.zero;
            if (m_hasPreviousOwnPosition && Time.deltaTime > 0.0001f && currentRadius > 0.001f)
            {
                Vector3 measuredVelocity = (m_nowPosition - m_previousOwnPosition) / Time.deltaTime;
                measuredVelocity.y = 0.0f;
                // 物理更新間の小さな座標差で射撃が一瞬止まらないよう、速度を平滑化する。
                m_smoothedOwnVelocity = Vector3.Lerp(m_smoothedOwnVelocity, measuredVelocity, 0.3f);
                ownVelocity = m_smoothedOwnVelocity;
            }
            else
            {
                m_smoothedOwnVelocity = Vector3.zero;
            }
            m_currentOwnSpeed = ownVelocity.magnitude;

            // 被弾直後の速度も先読みし、まだ大きく離れていなくても外向きに飛ばされていれば補正を開始する。
            float predictedDrift = m_currentAnchorDistance;
            if (m_currentAnchorDistance > 0.001f)
            {
                float outwardSpeed = Mathf.Max(0.0f, Vector3.Dot(ownVelocity, fromAnchor.normalized));
                predictedDrift += outwardSpeed * 0.25f;
            }

            bool nearStageEdge = currentRadius >= fieldRadius * 0.9f;
            float startDistance = Mathf.Max(m_stationRecoveryStartDistance, m_stationRecoveryResumeDistance + 0.05f);
            bool shouldRecover = predictedDrift >= startDistance
                || m_currentOwnSpeed >= m_stationRecoveryStartSpeed
                || Mathf.Abs(headingError) >= m_stationRecoveryStartAngle
                || nearStageEdge;
            if (!m_edgeRecoveryActive && shouldRecover)
            {
                m_edgeRecoveryActive = true;
            }
            else if (m_edgeRecoveryActive
                && m_currentAnchorDistance <= m_stationRecoveryResumeDistance
                && Mathf.Abs(headingError) <= m_stationRecoveryResumeAngle
                && m_currentOwnSpeed <= 0.35f
                && currentRadius < fieldRadius * 0.88f)
            {
                m_edgeRecoveryActive = false;
            }

            if (m_edgeRecoveryActive)
            {
                // 位置がずれている間は基準点へ移動する。基準点付近では初期の向きまで戻す。
                if (m_currentAnchorDistance > m_stationRecoveryResumeDistance
                    || m_currentOwnSpeed > 0.35f)
                {
                    // 位置ずれと今の速度を同時に打ち消し、全開の切り返しで行き過ぎるのを抑える。
                    Vector3 correction = anchorPosition - currentPosition - ownVelocity * 0.35f;
                    if (correction.sqrMagnitude < 0.0001f) correction = -ownVelocity;
                    float recoveryPower = Mathf.Clamp(
                        0.4f + m_currentAnchorDistance * 0.45f + m_currentOwnSpeed * 0.12f,
                        0.4f,
                        1.0f);
                    SetCaterpillarTowardWorldDir(m_nowRotation, correction.normalized, recoveryPower);
                }
                else if (Mathf.Abs(headingError) > m_stationRecoveryResumeAngle)
                {
                    float turnPower = Mathf.Clamp(headingError / 25.0f, -0.7f, 0.7f);
                    SXG_SetCaterpillarPower(-turnPower, turnPower);
                }
                else
                {
                    SXG_SetCaterpillarPower(0.0f, 0.0f);
                }
            }
            else
            {
                // 通常時はキャタピラを止めて、開始位置で固定砲台として待機する。
                SXG_SetCaterpillarPower(0.0f, 0.0f);
            }

            m_previousOwnPosition = m_nowPosition;
            m_hasPreviousOwnPosition = true;
        }

#endregion

#region MOVE_Helpers

        /// <summary>車体の向きと目標方向から左右キャタピラの出力を計算する。</summary>
        void SetCaterpillarTowardWorldDir(Quaternion tankRot, Vector3 desireDir, float forwardPower)
        {
            Vector3 front = tankRot * Vector3.forward;
            front.y = 0;
            front.Normalize();

            desireDir.y = 0;
            desireDir.Normalize();

            float dot = Vector3.Dot(front, desireDir);
            float crossY = Vector3.Cross(front, desireDir).y;   // 左右判定 

            // ざっくり旋回 
            float turn = Mathf.Clamp(crossY * 1.4f, -1.0f, +1.0f);

            float basePower = 0;
            if (0.5f < dot)
            {
                basePower = forwardPower;   // だいたい前を向いている
            }
            else if (dot < -0.5f)
            {
                basePower = -forwardPower;  // ほぼ反対を向いているので後退 
            }
            else
            {
                basePower = 0.2f;       // 旋回時は少し前進
            }

            // 左右のキャタピラの力 
            float left = Mathf.Clamp(basePower - turn, -1.0f, +1.0f);
            float right = Mathf.Clamp(basePower + turn, -1.0f, +1.0f);
            SXG_SetCaterpillarPower(left, right);
        }

        /// <summary>
        /// 最も近い戦車のIDを取得 
        /// </summary>
        /// <returns></returns>
        int FindTankIdWithMinDistance()
        {
            int nearestId = 0;
            float distance = float.MaxValue;

            for (int i=1; i < m_allTanksInfo.Length; ++i)
            {
                var tankInfo = m_allTanksInfo[i];
                if (!IsTargetableEnemy(tankInfo)) continue;
                Vector3 diff = tankInfo.Position - m_nowPosition;
                float d = diff.magnitude;
                if (d < distance)
                {
                    nearestId = i;
                    distance = d;
                }
            }
            return nearestId;
        }

        /// <summary>
        /// 残りエナジーが一番多い戦車のIDを取得 
        /// </summary>
        /// <returns></returns>
        int FindTankIdWithMaxEnergy()
        {
            int id = 0;
            int maxEnergy = 0;

            for (int i = 1; i < m_allTanksInfo.Length; ++i)
            {
                var tankInfo = m_allTanksInfo[i];
                if (!IsTargetableEnemy(tankInfo)) continue;
                if (maxEnergy < tankInfo.Energy)
                {
                    id = i;
                    maxEnergy = tankInfo.Energy;
                }
            }
            return id;
        }

        /// <summary>
        /// 残りエナジーが一番少ない戦車のIDを取得 
        /// </summary>
        /// <returns></returns>
        int FindTankIdWithMinEnergy()
        {
            int id = 0;
            int minEnergy = int.MaxValue;

            for (int i = 1; i < m_allTanksInfo.Length; ++i)
            {
                var tankInfo = m_allTanksInfo[i];
                if (!IsTargetableEnemy(tankInfo)) continue;
                if (tankInfo.Energy < minEnergy)
                {
                    id = i;
                    minEnergy = tankInfo.Energy;
                }
            }
            return id;
        }

        /// <summary>
        /// 正面に近い戦車を探す 
        /// </summary>
        /// <returns></returns>
        int FindTankIdInFront()
        {
            int id = 0;
            float maxDotProduct = 0.0f;
            Vector3 tankForward = m_nowRotation * Vector3.forward;

            for (int i=1; i < m_allTanksInfo.Length; ++i)
            {
                var tankInfo = m_allTanksInfo[i];
                if (!IsTargetableEnemy(tankInfo)) continue;

                var toTank = (tankInfo.Position - m_nowPosition).normalized;
                float dotProduct = Vector3.Dot(tankForward, toTank);
                if (maxDotProduct < dotProduct)
                {
                    id = i;
                    maxDotProduct = dotProduct;
                }
            }
            return id;
        }

        /// <summary>
        /// 背面に近い戦車を探す 
        /// </summary>
        /// <returns></returns>
        int FindTankIdBehind()
        {
            int id = 0;
            float minDotProduct = 0.0f;
            Vector3 tankForward = m_nowRotation * Vector3.forward;

            for (int i = 1; i < m_allTanksInfo.Length; ++i)
            {
                var tankInfo = m_allTanksInfo[i];
                if (!IsTargetableEnemy(tankInfo)) continue;

                var toTank = (tankInfo.Position - m_nowPosition).normalized;
                float dotProduct = Vector3.Dot(tankForward, toTank);
                if (dotProduct < minDotProduct)
                {
                    id = i;
                    minDotProduct = dotProduct;
                }
            }
            return id;
        }

#endregion
#region AIM

        [Header("<Aim (狙い)>")]
        // 空欄の砲塔には高コスト優先を適用する。ルール指定済みの砲塔はその選択方法を維持する。
        [SerializeField] EasyAI.TargetRule[] m_targetRules;

        [Header("<Debug (デバッグ)>")]
        // Consoleログと狙い点マーカーをまとめて切り替える。ログはUnity Editorでのみ出力する。
        [SerializeField, Tooltip("砲塔ごとの標的・旋回・クールダウン状態をConsoleへ表示する" )] bool m_debugTurretStatus = true;
        [SerializeField, Tooltip("デバッグ状態を出力する間隔（実時間・秒）"), Range(0.5f, 10.0f)] float m_debugLogInterval = 2.0f;
        // 0始まり。13基なら0〜12を指定して、各砲塔の狙い点を順番に確認できる。
        [SerializeField, Tooltip("Scene/Gameビューに狙い点マーカーを出す砲塔番号（0始まり）"), Range(0, 12)] int m_debugMarkerTurretIndex = 0;
        private float m_nextDebugLogTime;

        /// <summary>各砲塔の標的を決めて、照準点へ砲塔を旋回させる。</summary>
        void DoAim()
        {
            if (m_turretCount <= 0)
            {
                // 砲塔がなくなった場合は、前フレームのマーカーを消して終了する。
                SXG_DebugDrawPositionMarker(Vector3.zero, false);
                return;
            }

            EnsureAimPointBuffers();
            // 毎フレーム標的を決め直すため、前フレームの照準情報を先に消す。
            System.Array.Clear(m_hasAimPointByTurret, 0, m_hasAimPointByTurret.Length);
            System.Array.Clear(m_blockedByOwnTankByTurret, 0, m_blockedByOwnTankByTurret.Length);
            for (int i = 0; i < m_targetByTurret.Length; ++i) m_targetByTurret[i] = -1;

            // 外周への空撃ちはせず、実際に生存している敵へだけ照準を割り当てる。
            AimRotatingArmorAtThreats();
            BuildHighCostTargetAssignments();

            // 狙う 
            for (int i = 0; i < m_turretCount; ++i)
            {
                int targetTankId;
                if (UsesHighCostRule(i))
                {
                    targetTankId = i < m_highCostTargetByTurret.Length
                        ? m_highCostTargetByTurret[i]
                        : -1;
                }
                else
                {
                    targetTankId = SelectTarget(m_targetRules[i]);
                }

                if (0 < targetTankId)
                {
                    // 砲弾が届くまでの相手の移動を見越した地点へ照準する。
                    m_aimPointsByTurret[i] = CalculateLeadPoint(targetTankId);
                    m_hasAimPointByTurret[i] = true;
                    m_targetByTurret[i] = targetTankId;
                    SXG_RotateTurretToImpactPoint(i, m_aimPointsByTurret[i]);
                }
            }

            UpdateDebugAimMarker();
        }

        private void CacheTurretReferences(TurretPart[] turrets)
        {
            // 各砲塔の砲身方向を取得し、発射前の照準一致チェックに使う。
            m_myTurrets = turrets ?? new TurretPart[0];
            m_barrelAimTransforms = new Transform[m_myTurrets.Length];
            m_muzzleTransforms = new Transform[m_myTurrets.Length];
            for (int i = 0; i < m_myTurrets.Length; ++i)
            {
                Transform turretRoot = m_myTurrets[i] != null ? m_myTurrets[i].transform : null;
                if (turretRoot == null) continue;

                // 名前で軸と砲口を探す。マネージャーが砲塔を再生成しても同じ構造を参照できる。
                Transform[] children = turretRoot.GetComponentsInChildren<Transform>(true);
                for (int childIndex = 0; childIndex < children.Length; ++childIndex)
                {
                    if (children[childIndex].name == "JointPitchRotTr")
                        m_barrelAimTransforms[i] = children[childIndex];
                    else if (children[childIndex].name == "BarrelTopTr")
                        m_muzzleTransforms[i] = children[childIndex];
                }
            }
        }

        private void EnsureAimPointBuffers()
        {
            // 砲塔数の増減に合わせて、砲塔ごとの照準情報配列を作り直す。
            if (m_aimPointsByTurret.Length != m_turretCount)
            {
                m_aimPointsByTurret = new Vector3[m_turretCount];
                m_hasAimPointByTurret = new bool[m_turretCount];
                m_targetByTurret = new int[m_turretCount];
                m_blockedByOwnTankByTurret = new bool[m_turretCount];
            }
        }

        private float GetTurretAimErrorDegrees(int turretNo)
        {
            // この角度はEditorの状態表示用。発射の可否を止める条件には使わない。
            if (turretNo < 0 || turretNo >= m_aimPointsByTurret.Length
                || turretNo >= m_myTurrets.Length || turretNo >= m_barrelAimTransforms.Length
                || !m_hasAimPointByTurret[turretNo] || m_myTurrets[turretNo] == null
                || m_barrelAimTransforms[turretNo] == null)
            {
                return -1.0f;
            }

            // ゲーム側と同じ重力補正で砲弾の発射方向を計算し、実際の砲身方向との角度差を返す。
            // 砲塔が旋回中なら角度差が大きくなり、まだ狙いが合っていないと判断できる。
            Transform turretRoot = m_myTurrets[turretNo].transform;
            Vector3 localAim = turretRoot.InverseTransformPoint(m_aimPointsByTurret[turretNo] + Vector3.up * 0.5f);
            float flightTime = localAim.magnitude / CANNON_SHELL_SPEED;
            localAim -= Physics.gravity * GameConstants.CANNON_SHELL_GRAVITY_SCALE * (0.5f * flightTime * flightTime);
            if (localAim.sqrMagnitude < 0.0001f) return -1.0f;

            Vector3 desiredWorldDirection = turretRoot.TransformDirection(localAim.normalized);
            return Vector3.Angle(m_barrelAimTransforms[turretNo].forward, desiredWorldDirection);
        }

        private void UpdateDebugAimMarker()
        {
            // 選択した1基の狙い点を表示する。砲塔別の状態はConsoleログで確認する。
            if (m_debugTurretStatus && m_debugMarkerTurretIndex >= 0
                && m_debugMarkerTurretIndex < m_hasAimPointByTurret.Length
                && m_hasAimPointByTurret[m_debugMarkerTurretIndex])
            {
                SXG_DebugDrawPositionMarker(m_aimPointsByTurret[m_debugMarkerTurretIndex]);
            }
            else
            {
                SXG_DebugDrawPositionMarker(Vector3.zero, false);
            }
        }

        private void LogDebugTurretStatuses()
        {
#if UNITY_EDITOR
            // unscaledTimeを使い、ゲームの倍速設定に関係なく一定の実時間間隔で出力する。
            if (!m_debugTurretStatus || Time.unscaledTime < m_nextDebugLogTime) return;
            m_nextDebugLogTime = Time.unscaledTime + Mathf.Max(0.5f, m_debugLogInterval);

            StringBuilder status = new StringBuilder("[Player1234123] ");
            float radius = new Vector3(m_nowPosition.x, 0.0f, m_nowPosition.z).magnitude;
            float fireStopRatio = m_dontFireNearEdge ? 1.02f : 1.05f;
            bool edgeFireStop = radius >= GameConstants.ABOUT_GAME_FIELD_RADIUS * fireStopRatio;
            status.Append(m_edgeRecoveryActive ? "RETURN " : "HOLD ")
                .Append("d=").Append(m_currentAnchorDistance.ToString("0.00"))
                .Append(" v=").Append(m_currentOwnSpeed.ToString("0.00"))
                .Append(" r=").Append(radius.ToString("0.0")).Append(" ")
                .Append(edgeFireStop ? "FIRE_STOP_EDGE " : "FIRE_ALLOWED ");
            for (int i = 0; i < m_turretCount; ++i)
            {
                float errorDegrees = GetTurretAimErrorDegrees(i);
                bool canShoot = SXG_CanShoot(i);
                string targetLabel = i < m_targetByTurret.Length && m_targetByTurret[i] > 0
                    ? "P" + m_targetByTurret[i]
                    : "--";

                // AIMINGの角度差は表示だけで、発射可否には使わない。
                // BLOCKED=自車への射線 / NO_TARGET=標的なし / NO_AXIS=砲身参照なし
                // COOLDOWN=再発射待ち / READY=再発射可能
                string state;
                bool hasTarget = i < m_hasAimPointByTurret.Length && m_hasAimPointByTurret[i];
                bool pathBlocked = hasTarget
                    ? IsCurrentShotPathBlockedByOwnTank(i)
                    : i < m_blockedByOwnTankByTurret.Length && m_blockedByOwnTankByTurret[i];
                if (pathBlocked) state = "BLOCKED";
                else if (!hasTarget) state = "NO_TARGET";
                else if (errorDegrees < 0.0f) state = "NO_AXIS";
                else if (errorDegrees > 2.0f) state = "AIMING " + errorDegrees.ToString("0") + "deg";
                else if (!canShoot) state = "COOLDOWN";
                else state = "READY";

                status.Append("T").Append(i + 1).Append("=").Append(targetLabel).Append(":").Append(state).Append(" ");
            }
            Debug.Log(status.ToString(), this);
#endif
        }

        private bool UsesHighCostRule(int turretNo)
        {
            // ルール未設定の砲塔はこのAIの高コスト優先ルールを使う。
            // Inspectorで個別ルールが設定された砲塔の指定は維持する。
            return m_targetRules == null || turretNo >= m_targetRules.Length;
        }

        private bool IsTargetableEnemy(TankInfo info)
        {
            // 撃破済み、シールド中、奈落へ落ちた相手や大きく場外へ出た相手には照準を割り当てない。
            float radius = new Vector3(info.Position.x, 0.0f, info.Position.z).magnitude;
            return !info.IsDefeated
                && !info.IsInvincible
                && info.Position.y >= -1.0f
                && radius <= GameConstants.ABOUT_GAME_FIELD_RADIUS * 1.05f;
        }

        private void UpdateEnemyVelocityEstimates()
        {
            // 前回観測からの移動量をゲーム内時間で割り、相手ごとの速度を更新する。
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0.0001f) return;

            for (int i = 1; i < m_allTanksInfo.Length && i < m_previousEnemyPositions.Length; ++i)
            {
                Vector3 currentPosition = m_allTanksInfo[i].Position;
                if (m_allTanksInfo[i].IsDefeated)
                {
                    m_enemyVelocities[i] = Vector3.zero;
                    m_hasPreviousEnemyPosition[i] = false;
                    continue;
                }

                if (m_hasPreviousEnemyPosition[i])
                {
                    Vector3 measuredVelocity = (currentPosition - m_previousEnemyPositions[i]) / deltaTime;
                    // 再出撃時の座標ジャンプを速度と誤認しない。速度変化は平滑化して照準の揺れも抑える。
                    if (measuredVelocity.sqrMagnitude > 900.0f)
                    {
                        m_enemyVelocities[i] = Vector3.zero;
                    }
                    else
                    {
                        measuredVelocity = Vector3.ClampMagnitude(measuredVelocity, 30.0f);
                        m_enemyVelocities[i] = Vector3.Lerp(m_enemyVelocities[i], measuredVelocity, 0.4f);
                    }
                }
                else
                {
                    m_enemyVelocities[i] = Vector3.zero;
                    m_hasPreviousEnemyPosition[i] = true;
                }

                m_previousEnemyPositions[i] = currentPosition;
            }
        }

        private Vector3 CalculateLeadPoint(int targetTankId)
        {
            // 砲弾が飛んでいる間に相手が進む距離を加えた着弾予測点を返す。
            TankInfo target = m_allTanksInfo[targetTankId];
            Vector3 relativePosition = target.Position - m_nowPosition;
            Vector3 targetVelocity = m_enemyVelocities[targetTankId];
            float interceptTime = EstimateInterceptTime(relativePosition, targetVelocity);
            return target.Position + targetVelocity * interceptTime;
        }

        private float EstimateInterceptTime(Vector3 relativePosition, Vector3 targetVelocity)
        {
            // 相手が動く場合の着弾時間を二次方程式で求める。
            // 「相手までの予測距離」と「砲弾が同じ時間に進む距離」が一致する時刻を使う。
            float a = Vector3.Dot(targetVelocity, targetVelocity) - CANNON_SHELL_SPEED * CANNON_SHELL_SPEED;
            float b = 2.0f * Vector3.Dot(relativePosition, targetVelocity);
            float c = Vector3.Dot(relativePosition, relativePosition);
            float interceptTime = -1.0f;

            if (Mathf.Abs(a) < 0.0001f)
            {
                if (Mathf.Abs(b) > 0.0001f) interceptTime = -c / b;
            }
            else
            {
                float discriminant = b * b - 4.0f * a * c;
                if (discriminant >= 0.0f)
                {
                    float sqrtDiscriminant = Mathf.Sqrt(discriminant);
                    float t1 = (-b - sqrtDiscriminant) / (2.0f * a);
                    float t2 = (-b + sqrtDiscriminant) / (2.0f * a);
                    if (t1 > 0.0f && t2 > 0.0f) interceptTime = Mathf.Min(t1, t2);
                    else if (t1 > 0.0f) interceptTime = t1;
                    else if (t2 > 0.0f) interceptTime = t2;
                }
            }

            // 解が得られない場合は静止目標として見積もり、長距離でも予測が暴れないよう上限を置く。
            if (interceptTime <= 0.0f) interceptTime = relativePosition.magnitude / CANNON_SHELL_SPEED;
            return Mathf.Clamp(interceptTime, 0.0f, 2.5f);
        }

        private void CacheRotJointReferences(RotJointPart[] joints)
        {
            // ジョイント数を固定せず、現在Prefabに付いている全ジョイントを保持する。
            m_myRotJoints = joints ?? new RotJointPart[0];
        }

        private void AimRotatingArmorAtThreats()
        {
            // 各ジョイントを取付方向に近い敵へ向け、防御を一方向へ偏らせない。
            // ジョイント部品はカウンターウェイト配置として反動時の姿勢安定にも使えるが、
            // この処理自体は発射反動を測って逆方向へ相殺する制御ではない。
            int jointCount = Mathf.Min(SXG_GetCountOfMyRotJoints(), m_myRotJoints.Length);
            if (jointCount <= 0) return;

            System.Array.Clear(m_jointTargetUsageCounts, 0, m_jointTargetUsageCounts.Length);
            Quaternion inverseTankRotation = Quaternion.Inverse(m_nowRotation);

            for (int i = 0; i < jointCount; ++i)
            {
                if (m_myRotJoints[i] == null) continue;

                // ジョイントの取付方向を基準に脅威を分担する。取付位置が取れない場合は全周へ均等配置する。
                Vector3 localMountDirection = inverseTankRotation * (m_myRotJoints[i].transform.position - m_nowPosition);
                localMountDirection.y = 0.0f;
                if (localMountDirection.sqrMagnitude < 0.0001f)
                {
                    localMountDirection = Quaternion.AngleAxis(360.0f * i / jointCount, Vector3.up) * Vector3.forward;
                }
                localMountDirection.Normalize();

                int targetTankId = FindBestJointThreat(localMountDirection, inverseTankRotation);
                if (targetTankId <= 0)
                {
                    SXG_RotateJointToAngle(i, 0.0f);
                    continue;
                }

                m_jointTargetUsageCounts[targetTankId]++;
                Vector3 localDirection = inverseTankRotation * (m_allTanksInfo[targetTankId].Position - m_nowPosition);
                localDirection.y = 0;
                if (localDirection.sqrMagnitude < 0.0001f) continue;

                float yawAngle = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
                SXG_RotateJointToAngle(i, yawAngle);
            }
        }

        private int FindBestJointThreat(Vector3 localMountDirection, Quaternion inverseTankRotation)
        {
            // 取付方向への近さを重視し、近距離・高コストの敵を加点する。
            // すでに他のジョイントが向いている敵は減点し、担当を分散する。
            int bestTargetId = 0;
            float bestScore = float.NegativeInfinity;
            for (int i = 1; i < m_allTanksInfo.Length; ++i)
            {
                TankInfo candidate = m_allTanksInfo[i];
                if (!IsTargetableEnemy(candidate)) continue;

                Vector3 localDirection = inverseTankRotation * (candidate.Position - m_nowPosition);
                localDirection.y = 0.0f;
                float distance = localDirection.magnitude;
                if (distance < 0.001f) continue;

                float alignment = Vector3.Dot(localMountDirection, localDirection / distance);
                int priorAssignments = i < m_jointTargetUsageCounts.Length ? m_jointTargetUsageCounts[i] : 0;
                float score = alignment * 2.5f
                    - distance * 0.025f
                    + candidate.CostOfOneTank * 0.0005f
                    - priorAssignments * 1.25f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTargetId = i;
                }
            }
            return bestTargetId;
        }

        private void BuildHighCostTargetAssignments()
        {
            // 高コスト優先を使う砲塔だけを取り出し、標的ごとの担当数を決める。
            // Inspectorで個別ルールを設定した砲塔はこの割当の対象外。
            if (m_highCostTargetByTurret.Length != m_turretCount)
            {
                m_highCostTargetByTurret = new int[m_turretCount];
            }
            for (int i = 0; i < m_highCostTargetByTurret.Length; ++i)
            {
                m_highCostTargetByTurret[i] = -1;
            }

            m_enemyCandidateIds.Clear();
            for (int i = 1; i < m_allTanksInfo.Length; ++i)
            {
                if (IsTargetableEnemy(m_allTanksInfo[i]))
                {
                    m_enemyCandidateIds.Add(i);
                }
            }
            if (m_enemyCandidateIds.Count == 0) return;

            // CostOfOneTankはチーム残エナジーではなく、戦車1台の出撃コスト。
            // 撃破時の得点が大きい相手を先にし、同コストなら近い相手を先にする。
            for (int i = 1; i < m_enemyCandidateIds.Count; ++i)
            {
                int targetId = m_enemyCandidateIds[i];
                int insertAt = i;
                while (insertAt > 0 && HasHigherTargetPriority(targetId, m_enemyCandidateIds[insertAt - 1]))
                {
                    m_enemyCandidateIds[insertAt] = m_enemyCandidateIds[insertAt - 1];
                    --insertAt;
                }
                m_enemyCandidateIds[insertAt] = targetId;
            }

            m_unassignedTurretIds.Clear();
            for (int i = 0; i < m_turretCount; ++i)
            {
                if (UsesHighCostRule(i))
                {
                    m_unassignedTurretIds.Add(i);
                }
            }
            if (m_unassignedTurretIds.Count == 0) return;

            // 射線の通る砲塔のうち約3分の2を最高コストへ、残りを次点以下へ均等に配る。
            // 未設定砲塔13基なら基本は相手3台で9/2/2基。遮蔽された砲塔は次の敵へ回す。
            int topTargetQuota = (m_unassignedTurretIds.Count * 2 + 2) / 3;
            AllocateTurretsToTarget(m_enemyCandidateIds[0], topTargetQuota);

            for (int rank = 1; rank < m_enemyCandidateIds.Count && m_unassignedTurretIds.Count > 0; ++rank)
            {
                int remainingTargets = m_enemyCandidateIds.Count - rank;
                int quota = (m_unassignedTurretIds.Count + remainingTargets - 1) / remainingTargets;
                AllocateTurretsToTarget(m_enemyCandidateIds[rank], quota);
            }

            // まだ残る砲塔は最高コストへ再挑戦させる。射線が塞がる個体は残し、無理に撃たせない。
            if (m_unassignedTurretIds.Count > 0)
            {
                AllocateTurretsToTarget(m_enemyCandidateIds[0], m_unassignedTurretIds.Count);
            }
        }

        private bool HasHigherTargetPriority(int leftTargetId, int rightTargetId)
        {
            // 同じコストなら近い敵を先にし、移動中に遠い敵だけへ集中するのを避ける。
            int leftCost = m_allTanksInfo[leftTargetId].CostOfOneTank;
            int rightCost = m_allTanksInfo[rightTargetId].CostOfOneTank;
            if (leftCost != rightCost) return leftCost > rightCost;

            float leftDistance = (m_allTanksInfo[leftTargetId].Position - m_nowPosition).sqrMagnitude;
            float rightDistance = (m_allTanksInfo[rightTargetId].Position - m_nowPosition).sqrMagnitude;
            return leftDistance < rightDistance;
        }

        private void AllocateTurretsToTarget(int targetTankId, int quota)
        {
            // 射線が自車の装甲に塞がれない砲塔だけをこの敵へ割り当てる。
            // 射線が塞がる砲塔は残し、次の優先順位の敵へ割り当て直す。
            Vector3 impactPoint = CalculateLeadPoint(targetTankId);
            int assigned = 0;
            for (int candidateIndex = 0;
                candidateIndex < m_unassignedTurretIds.Count && assigned < quota;)
            {
                int selectedTurret = m_unassignedTurretIds[candidateIndex];
                if (selectedTurret >= m_myTurrets.Length || m_myTurrets[selectedTurret] == null)
                {
                    candidateIndex++;
                    continue;
                }

                if (IsShotPathBlockedByOwnTank(selectedTurret, impactPoint))
                {
                    m_blockedByOwnTankByTurret[selectedTurret] = true;
                    candidateIndex++;
                    continue;
                }

                m_highCostTargetByTurret[selectedTurret] = targetTankId;
                m_unassignedTurretIds.RemoveAt(candidateIndex);
                assigned++;
            }
        }

        /// <summary>狙い先に向けた弾道の初動が、自車の装甲や別部品に当たらないか確認する。</summary>
        private bool IsShotPathBlockedByOwnTank(int turretNo, Vector3 impactPoint)
        {
            if (turretNo < 0 || turretNo >= m_myTurrets.Length
                || turretNo >= m_muzzleTransforms.Length
                || m_myTurrets[turretNo] == null || m_muzzleTransforms[turretNo] == null)
            {
                return true;
            }

            Transform turretRoot = m_myTurrets[turretNo].transform;
            Vector3 localAim = turretRoot.InverseTransformPoint(impactPoint + Vector3.up * 0.5f);
            float flightTime = localAim.magnitude / CANNON_SHELL_SPEED;
            localAim -= Physics.gravity * GameConstants.CANNON_SHELL_GRAVITY_SCALE
                * (0.5f * flightTime * flightTime);
            if (localAim.sqrMagnitude < 0.0001f) return true;

            Vector3 launchDirection = turretRoot.TransformDirection(localAim.normalized);
            // 砲口から近距離だけ調べる。遠くの弾道まで自車の射線扱いすると、
            // 少し車体がずれただけで多数の砲塔が BLOCKED になりやすい。
            float probeTime = Mathf.Min(0.14f, flightTime);
            return IsOwnTankBlockingTrajectory(
                m_muzzleTransforms[turretNo].position,
                launchDirection,
                probeTime,
                turretRoot);
        }

        /// <summary>発射時の実際の砲身向きでも自車への直撃を避ける。</summary>
        private bool IsCurrentShotPathBlockedByOwnTank(int turretNo)
        {
            if (turretNo < 0 || turretNo >= m_myTurrets.Length
                || turretNo >= m_muzzleTransforms.Length
                || m_myTurrets[turretNo] == null || m_muzzleTransforms[turretNo] == null)
            {
                return true;
            }

            float flightTime = Mathf.Min(
                0.14f,
                Vector3.Distance(m_muzzleTransforms[turretNo].position, m_aimPointsByTurret[turretNo])
                    / CANNON_SHELL_SPEED);
            return IsOwnTankBlockingTrajectory(
                m_muzzleTransforms[turretNo].position,
                m_muzzleTransforms[turretNo].forward,
                flightTime,
                m_myTurrets[turretNo].transform);
        }

        private bool IsOwnTankBlockingTrajectory(
            Vector3 start,
            Vector3 launchDirection,
            float probeTime,
            Transform shooterTurret)
        {
            Transform ownTankRoot = transform.parent != null ? transform.parent : transform;
            float gravityScale = GameConstants.CANNON_SHELL_GRAVITY_SCALE;
            Vector3 gravity = Physics.gravity * gravityScale;
            const float shellRadius = 0.32f;
            const float sampleInterval = 0.08f;
            int shellLayerMask = 1 << Constants.OBJ_LAYER_SHELL;

            if (HasBlockingOwnColliderAt(start, shellRadius, ownTankRoot, shooterTurret, shellLayerMask))
                return true;

            Vector3 previousPosition = start;
            for (float t = Mathf.Min(sampleInterval, probeTime);
                t > 0.0f;
                t = Mathf.Min(t + sampleInterval, probeTime))
            {
                Vector3 nextPosition = start + launchDirection * CANNON_SHELL_SPEED * t
                    + gravity * (0.5f * t * t);
                Vector3 segment = nextPosition - previousPosition;
                float segmentLength = segment.magnitude;
                if (segmentLength > 0.0001f)
                {
                    int hitCount = Physics.SphereCastNonAlloc(
                        previousPosition,
                        shellRadius,
                        segment / segmentLength,
                        m_shotPathCastHits,
                        segmentLength,
                        ~0,
                        QueryTriggerInteraction.Ignore);
                    for (int hitIndex = 0; hitIndex < hitCount; ++hitIndex)
                    {
                        Collider hitCollider = m_shotPathCastHits[hitIndex].collider;
                        if (IsBlockingOwnCollider(hitCollider, ownTankRoot, shooterTurret, shellLayerMask))
                            return true;
                    }
                }

                previousPosition = nextPosition;
                if (t >= probeTime) break;
            }

            return false;
        }

        private bool HasBlockingOwnColliderAt(
            Vector3 position,
            float radius,
            Transform ownTankRoot,
            Transform shooterTurret,
            int shellLayerMask)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                position,
                radius,
                m_shotPathOverlapHits,
                ~0,
                QueryTriggerInteraction.Ignore);
            for (int hitIndex = 0; hitIndex < hitCount; ++hitIndex)
            {
                if (IsBlockingOwnCollider(
                    m_shotPathOverlapHits[hitIndex],
                    ownTankRoot,
                    shooterTurret,
                    shellLayerMask))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsBlockingOwnCollider(
            Collider collider,
            Transform ownTankRoot,
            Transform shooterTurret,
            int shellLayerMask)
        {
            if (collider == null || collider.isTrigger) return false;

            Transform colliderTransform = collider.transform;
            if (colliderTransform != ownTankRoot && !colliderTransform.IsChildOf(ownTankRoot)) return false;
            if (colliderTransform == shooterTurret || colliderTransform.IsChildOf(shooterTurret)) return false;

            // ゲーム側がShellとの衝突を除外した砲塔は、実際の弾道でも障害物に数えない。
            if ((collider.excludeLayers.value & shellLayerMask) != 0) return false;
            if (Physics.GetIgnoreLayerCollision(Constants.OBJ_LAYER_SHELL, collider.gameObject.layer)
                && (collider.includeLayers.value & shellLayerMask) == 0)
            {
                return false;
            }
            return true;
        }

        private int SelectTarget(EasyAI.TargetRule rule)
        {
            // Inspectorで明示された砲塔だけは、従来の選択ルールを使えるようにしておく。
            switch (rule)
            {
                case EasyAI.TargetRule.MinDistance:
                    return FindTankIdWithMinDistance();
                case EasyAI.TargetRule.MinEnergy:
                    return FindTankIdWithMinEnergy();
                case EasyAI.TargetRule.MaxEnergy:
                    return FindTankIdWithMaxEnergy();
                case EasyAI.TargetRule.InFront:
                    return FindTankIdInFront();
                case EasyAI.TargetRule.Behind:
                    return FindTankIdBehind();
                default:
                    return FindTankIdWithMinDistance();
            }
        }

#endregion

#region FIRE

        [Header("<Fire (射撃)>")]
        [SerializeField] float m_fireInterval = 0.2f;       // 自車全体で次の1発を撃つまでの間隔
        [SerializeField] bool m_dontFireNearEdge = false;   // 有効なら場外へ少し出た時点で射撃を止める
        [SerializeField] bool m_dontFireWhenUnstable = false;   // 車体が大きく傾いている間は射撃しない

        private int m_nextTurretId = 0;
        private float m_fireIntervalTime = 0;

        /// <summary>発射間隔・車体状態・照準を確認し、撃てる砲塔から1発ずつ撃つ。</summary>
        void DoFire()
        { 
            m_fireIntervalTime += Time.deltaTime;
            LogDebugTurretStatuses();

            // 発射反動そのものはゲーム物理側が砲身位置へ加える。
            // ここでは安全な発射タイミングを選び、反動の姿勢抑制は装甲・ジョイントの配置バランスに任せる。

            // 安定している時だけ撃つ？ 
            if (m_dontFireWhenUnstable)
            {
                if ((m_nowRotation * Vector3.up).y < 0.9f)
                {
                    return;
                }
            }

            // 固定位置へ戻っている間も射撃を続ける。移動や位置ずれだけでは射撃を止めず、
            // 車体がステージ外へ大きく出た場合だけ、無駄撃ちを避けるため停止する。
            float distanceFromCenter = new Vector3(m_nowPosition.x, 0, m_nowPosition.z).magnitude;
            // シーンの出撃位置（半径約30）とランダム配置幅（最大約4）を許容し、
            // 出撃直後から外周停止扱いにならないよう、停止境界は安全圏の外側に置く。
            // 外周が近いだけでは止めず、場外へ大きく押し出された時だけ止める。
            // 外周方向への射撃と、車体を戻す処理を両立させる。
            float fireStopRatio = m_dontFireNearEdge ? 1.02f : 1.05f;
            if (distanceFromCenter >= GameConstants.ABOUT_GAME_FIELD_RADIUS * fireStopRatio)
            {
                return;
            }

            // インターバル確認 
            if (m_fireIntervalTime < m_fireInterval)
            {
                return;
            }

            // 前回撃った砲塔の次から探す。旋回中や再装填中の砲塔を飛ばすので、
            // 1基が準備できるまで他の砲塔まで止まることがない。
            for (int i=0; i < m_turretCount; ++i)
            {
                int turretNo = (m_nextTurretId + i) % m_turretCount;

                // 狙い点があり、現在の砲口から自車の装甲へ直撃しない砲塔を使う。
                // 狭い角度しきい値で射撃を止めず、再発射可否はゲーム本来の判定に任せる。
                bool hasTarget = turretNo < m_hasAimPointByTurret.Length && m_hasAimPointByTurret[turretNo];
                if (!hasTarget || !SXG_CanShoot(turretNo)) continue;

                if (IsCurrentShotPathBlockedByOwnTank(turretNo))
                {
                    m_blockedByOwnTankByTurret[turretNo] = true;
                    continue;
                }

                SXG_Shoot(turretNo);
                m_nextTurretId = (turretNo + 1) % m_turretCount;
                m_fireIntervalTime = 0;
                break;
            }
        }

#endregion

    }
}
