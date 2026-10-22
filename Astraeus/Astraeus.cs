using UnityEngine;
using SXG2025;

namespace Astraeus
{
    /// <summary>
    /// 相手の行動と距離に合わせて、標的・射程・回避方向を組み替える戦術AI。
    /// 毎フレーム「敵の追跡 → 標的選択 → 照準・射撃 → 移動」の順で判断する。
    /// 機体構成は砲台3基と回転ジョイント1基。参照はPrefabの配列に登録する。
    /// 学習済みモデルは使わず、観測した位置と物理計算から行動を決める。
    /// </summary>
    public sealed class Astraeus : ComPlayerBase
    {
        // このゲームでは1 Unity単位を距離1として扱う。時間は秒、APIへ渡す角度は度。
        // 弾速と重力倍率は予測用の値であり、ここを書き換えても実際の砲弾性能は変わらない。
        private const int PLAYER_COUNT = 4;                  // APIの配列は[0]が自分、[1]～[3]が敵。
        private const float SHELL_SPEED = 38.0f;             // 標準砲弾の初速。弾道計算と到達時間に使う。
        private const float SHELL_GRAVITY_SCALE = 2.0f;      // 標準砲弾に加わる重力はPhysics.gravityの2倍。
        private const float AIM_HEIGHT_OFFSET = 0.5f;        // 現在未使用の旧照準補助関数だけで使う高さ。
        private const float MAX_TRACKED_SPEED = 22.0f;       // 速度推定の上限。位置の飛びで予測が暴れないようにする。

        // 移動時の間合い。近すぎれば後退、遠すぎれば横移動を減らして接近する。
        private const float DESIRED_RANGE = 24.0f;           // 基本的に維持したい敵との距離。
        private const float MIN_RANGE = 18.0f;               // これより近いと追加の後退成分を加える。
        private const float MAX_RANGE = 38.0f;               // これより遠いと接近を優先する。
        private const float MAX_FIRE_RANGE = 80.0f;          // 発射を許可する距離の上限。命中を保証する距離ではない。
        private const float FIRE_ERROR_RADIUS = 4.0f;        // 旧調整値。現在の発射可否判定では参照しない。
        private const float SWITCH_TARGET_MARGIN = 0.65f;    // 現在の敵に加える継続ボーナス。頻繁な標的変更を抑える。

        // 岩の検出範囲と、候補進路に必要な余裕。岩の形状を厳密に経路探索する値ではない。
        private const float OBSTACLE_SCAN_INTERVAL = 0.20f;  // 毎フレーム再検索せず、この間隔で障害物を更新する。
        private const float OBSTACLE_SCAN_DISTANCE = 16.0f;  // 周囲の障害物を取得する範囲。
        private const float OBSTACLE_CLEARANCE = 4.8f;       // 候補進路の横方向で確保したい余裕。
        private const float OBSTACLE_LOOKAHEAD = 14.0f;      // 候補進路の前方で危険度を調べる長さ。

        // 進んだ距離を一定間隔で測り、停滞が続いた時だけ後退旋回へ切り替える。
        private const float PROGRESS_CHECK_INTERVAL = 0.70f; // 移動距離を測り直す間隔。
        private const float MIN_PROGRESS_DISTANCE = 0.35f;   // この距離未満なら停滞1回と数える。
        private const float ESCAPE_DURATION = 1.25f;         // 脱出用の後退旋回を続ける時間。
        private const float ESCAPE_COOLDOWN = 1.8f;          // 脱出終了後、次の脱出を開始できるまでの待ち時間。

        // 敵ごとの前回観測。速度と旋回角速度はゲームAPIから直接得ず、位置差分から推定する。
        private struct EnemyTrack
        {
            public bool HasPosition;  // 前回位置が有効か。初回観測や敗退後は差分を使わない。
            public Vector3 Position;  // 前回のワールド座標。
            public Vector3 Velocity;  // 平滑化した水平速度。上下の跳ねは偏差予測に入れない。
            public float TurnRate;    // 水平速度の向きが変わる速さ。単位はラジアン/秒。
            public float LastSeenTime; // 前回観測したTime.time。
        }

        // 自分の姿勢と敵の追跡状態。敵IDはSXG_GetAllTanksInfoの配列添字を使う。
        private TurretPart[] m_cachedTurrets = new TurretPart[0];
        private readonly EnemyTrack[] m_enemyTracks = new EnemyTrack[PLAYER_COUNT];
        private Vector3 m_selfPosition;
        private Quaternion m_selfRotation;
        private int m_targetId = -1;
        private float m_targetSelectedAt;
        private float m_randomPhase; // 初期位置から作る位相。周回方向や同点時の選択に使う。

        // 物理クエリの結果配列を再利用し、照準判定のたびに配列を作る負担を減らす。
        private Vector3[] m_obstaclePositions = new Vector3[0];
        private readonly RaycastHit[] m_shotPathHits = new RaycastHit[16];
        private readonly Collider[] m_shotPathOverlapHits = new Collider[16];
        private float m_nextObstacleScanTime;

        // 射撃側が記録し、移動側が利用する情報。岩で射線が塞がれたら横へ回り込む。
        private bool m_shotPathBlocked;
        private Vector3 m_shotBlockingObstacle;
        private Vector3 m_shotTargetPoint;
        private Vector3 m_lastMuzzlePosition;

        // 停滞判定と脱出動作の状態。脱出中は通常の進路指定より後退旋回を優先する。
        private Vector3 m_progressSamplePosition;
        private float m_nextProgressCheckTime;
        private float m_escapeUntil;
        private float m_escapeCooldownUntil;
        private int m_stagnantChecks;
        private int m_escapeSign = 1;
        private bool m_hasProgressSample;

        // 砲台共通の発射間隔と、次に優先して試す砲台の番号。
        private float m_nextShotTime;
        private int m_nextGun;

        // 急所の位置・評価は敵ごとに短時間キャッシュし、複数砲台でも共有する。
        private readonly float[] m_nextWeakPointScan = new float[PLAYER_COUNT];
        private readonly Vector3[] m_weakPointOffsets = new Vector3[PLAYER_COUNT];
        private readonly float[] m_weakPointQuality = new float[PLAYER_COUNT];
        private readonly RaycastHit[] m_visionHits = new RaycastHit[64];

        // 接近弾の検索結果、自分の速度を読むRigidbody、今フレームの全機体情報。
        private readonly Collider[] m_nearShells = new Collider[64];
        private Rigidbody m_body;
        private TankInfo[] m_tacticalTanks;

        /// <summary>砲台参照と自車の物理情報を取得し、初期位置から行動の位相を決める。</summary>
        private void Start()
        {
            GetTurrets(CacheTurrets);
            m_body = GetComponentInParent<Rigidbody>();
            SXG_GetPositionAndRotation(out m_selfPosition, out m_selfRotation);
            m_randomPhase = Mathf.Atan2(m_selfPosition.z, m_selfPosition.x);
        }

        /// <summary>追跡・標的・射撃・移動を更新する、AI全体の入口。</summary>
        private void Update()
        {
            // 必ず[0]が自分。[1]～[3]が相手なので、開始席が変わっても同じ添字で処理できる。
            TankInfo[] tanks = SXG_GetAllTanksInfo();
            if (tanks == null || tanks.Length < PLAYER_COUNT)
                return;
            m_tacticalTanks = tanks;

            SXG_GetPositionAndRotation(out m_selfPosition, out m_selfRotation);
            UpdateEnemyTracks(tanks);

            // 相手を変えた時刻を残し、SelectTargetで最低限の狙い続ける時間を確保する。
            int nextTarget = SelectTarget(tanks, m_targetId);
            if (nextTarget != m_targetId) m_targetSelectedAt = Time.time;
            m_targetId = nextTarget;
            if (IsEnemyAvailable(tanks, m_targetId))
            {
                // 射撃を先に処理すると、そのフレームで見つかった岩を移動判断へ渡せる。
                AimAndFire(tanks[m_targetId]);
                DriveTactically(tanks[m_targetId]);
            }
            else
            {
                DriveToSafety();
            }
        }

        /// <summary>GetTurretsの取得結果を保存する。取得できなければ空配列で扱う。</summary>
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
                            // 1回の観測差が3を超えたら再出撃などの位置飛びとして扱う。
                            // 瞬間移動を移動速度に変換すると、偏差照準が大きく外れてしまう。
                            track.Velocity = Vector3.zero;
                        }
                        else
                        {
                            // 指数平滑化で小さな位置ぶれを吸収する。係数をdtから求めてフレーム時間に対応する。
                            Vector3 measured = Vector3.ClampMagnitude(displacement / elapsed, MAX_TRACKED_SPEED);
                            float responsiveness = 1.0f - Mathf.Exp(-6.0f * dt);
                            Vector3 previousVelocity = track.Velocity;
                            track.Velocity = Vector3.Lerp(track.Velocity, measured, responsiveness);
                            // ほぼ停止中は向きの変化が不安定になるため、十分な速度がある時だけ旋回を推定する。
                            float turnRate = previousVelocity.sqrMagnitude > 2.0f && track.Velocity.sqrMagnitude > 2.0f
                                ? Vector3.SignedAngle(previousVelocity, track.Velocity, Vector3.up)
                                    * Mathf.Deg2Rad / elapsed : 0;
                            track.TurnRate = Mathf.Lerp(track.TurnRate, Mathf.Clamp(turnRate, -1.8f, 1.8f), responsiveness);
                        }
                    }
                    else if (elapsed >= 1.5f)
                    {
                        // 観測の間が空いた場合、古い速度をそのまま次の照準へ持ち越さない。
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

        /// <summary>倒し切りやすさ、距離、こちらへの脅威、急所の狙いやすさを合算して敵を選ぶ。</summary>
        private int SelectTarget(TankInfo[] tanks, int currentTarget)
        {
            // 有効な相手は最低0.8秒狙い続ける。標的の往復で砲台の旋回が終わらない状態を避ける。
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

                // 先頭砲台の位置から、急所を狙った時の着弾先の評価を更新する。
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

                // 内積が1に近ければ敵の車体正面がこちら向き。砲口の向きを直接測っているわけではない。
                // 残りエネルギー/1機のコストは残機の目安として使い、少ない相手ほど加点する。
                float energyPerTank = Mathf.Max(1.0f, enemy.CostOfOneTank);
                float remainingLives = enemy.Energy / energyPerTank;
                float finishBonus = 1.6f / (1.0f + Mathf.Max(0.0f, remainingLives));
                float closeBonus = Mathf.Clamp(34.0f - distance, -10.0f, 24.0f) * 0.045f;
                float threatBonus = facingThreat * 1.35f;
                float valueBonus = Mathf.Clamp(enemy.CostOfOneTank, 100, 1000) * 0.00035f;
                // 露出した本体に当たる射線は、砲台や岩に阻まれる射線より高く評価する。
                float exposureBonus = Mathf.Clamp(m_weakPointQuality[id], -10.0f, 10.0f) * 0.28f;
                float score = finishBonus + closeBonus + threatBonus + valueBonus + exposureBonus;

                if (id == currentTarget)
                {
                    // 現在の敵には継続ボーナスを付け、少し良い敵が現れただけでは乗り換えない。
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
            // 前フレームの射線遮蔽を持ち越さず、今回の砲口と狙点で判定し直す。
            m_shotPathBlocked = false;
            m_shotBlockingObstacle = Vector3.zero;
            int turretCount = Mathf.Min(m_cachedTurrets.Length, SXG_GetCountOfMyTurrets());
            EnemyTrack track = m_enemyTracks[m_targetId];
            if (SXG_GetCountOfMyRotJoints() > 0)
            {
                // 車体の周回中も低い3砲台を敵側へ向ける。ここでは0.45秒先の予測方向を使う。
                // ワールド方向を機体のローカル方向へ戻してからジョイントの角度に変換する。
                Vector3 local = Quaternion.Inverse(m_selfRotation)
                    * (enemy.Position + PredictDisplacement(track, 0.45f) - m_selfPosition);
                SXG_RotateJointToAngle(0, Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg);
            }

            int firstGun = m_nextGun;
            for (int slot = 0; slot < turretCount; slot++)
            {
                // 前回発射した次の砲台から試す。先頭の砲台だけに発射機会が偏らないようにする。
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
                // 到達時間は狙点で変わり、狙点は到達時間で変わる。4回反復して両者を近づける。
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

                // 移動側の回り込み判断用に保存し、射撃方向は各砲台のローカル角へ変換する。
                m_shotTargetPoint = aimPoint;
                m_lastMuzzlePosition = muzzlePosition;
                Vector3 localDirection = turret.transform.InverseTransformDirection(requiredDirection);
                float yaw = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Atan2(localDirection.y,
                    new Vector2(localDirection.x, localDirection.z).magnitude) * Mathf.Rad2Deg;
                SXG_RotateTurretToAngle(turretId, yaw, pitch);

                // 旋回命令は全砲台に送るが、発射は装填・全体間隔・仰俯角の条件を満たした時だけ。
                if (!SXG_CanShoot(turretId) || Time.time < m_nextShotTime || pitch < -44.5f || pitch > 9.8f)
                    continue;

                float alignment = Vector3.Angle(muzzleRotation * Vector3.forward, requiredDirection);
                float distance = Vector3.Distance(muzzlePosition, aimPoint);
                // 命令した角度ではなく現在の砲口の向きを使う。距離が遠いほど角度の許容を狭める。
                float allowedAlignment = Mathf.Clamp(
                    Mathf.Atan2(0.50f, Mathf.Max(distance, 1.0f)) * Mathf.Rad2Deg,
                    0.40f,
                    2.0f);
                if (distance <= MAX_FIRE_RANGE && alignment <= allowedAlignment)
                {
                    // 自分の装甲・別砲台、次に岩の順で確認する。塞がれている間は発射を見送る。
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
                    // 同時斉射による弾の集中と反動を分散する。全砲台共通で次の発射まで0.36秒空ける。
                    m_nextShotTime = Time.time + 0.36f;
                    m_nextGun = (turretId + 1) % turretCount;
                }
            }
        }

        /// <summary>推定速度と一定の旋回角速度から、指定秒後までの水平移動量を求める。</summary>
        private static Vector3 PredictDisplacement(EnemyTrack track, float time)
        {
            float omega = track.TurnRate;
            // 直進に近い時は通常の速度×時間。どちらの予測も移動量を14以内に制限する。
            if (Mathf.Abs(omega) < 0.05f)
                return Vector3.ClampMagnitude(track.Velocity * time, 14.0f);
            // 曲がっている敵は速度の向きを回しながら積分し、旋回の外側へ照準が流れるのを抑える。
            // 0.10秒ぶん先の接線方向を使い、観測と平滑化による遅れを見込む。
            Vector3 velocity = Quaternion.AngleAxis(omega * 0.10f * Mathf.Rad2Deg, Vector3.up) * track.Velocity;
            Vector3 side = Vector3.Cross(Vector3.up, velocity);
            float angle = omega * time;
            return Vector3.ClampMagnitude(
                (velocity * Mathf.Sin(angle) + side * (1.0f - Mathf.Cos(angle))) / omega, 14.0f);
        }

        /// <summary>重力下の投射式を解き、低い弾道の初期方向と到達時間を返す。解けなければfalse。</summary>
        private static bool SolveBallistic(Vector3 muzzle, Vector3 target, out Vector3 direction, out float flightTime)
        {
            Vector3 delta = target - muzzle;
            Vector3 flat = Flatten(delta);
            float range = flat.magnitude;
            float gravity = -Physics.gravity.y * SHELL_GRAVITY_SCALE;
            float speedSquared = SHELL_SPEED * SHELL_SPEED;
            // 判別式：v^4 - g(gR^2 + 2hv^2)。Rは水平距離、hは狙点と砲口の高低差。
            float discriminant = speedSquared * speedSquared
                - gravity * (gravity * range * range + 2.0f * delta.y * speedSquared);
            direction = Vector3.zero;
            flightTime = 0;
            if (range < 0.1f || discriminant <= 0) return false;
            // 平方根を引く側が低い弾道。上向きの成分を含めて正規化した発射方向を作る。
            float tangent = (speedSquared - Mathf.Sqrt(discriminant)) / (gravity * range);
            float cosine = 1.0f / Mathf.Sqrt(1.0f + tangent * tangent);
            direction = flat / range * cosine + Vector3.up * (tangent * cosine);
            flightTime = range / (SHELL_SPEED * cosine);
            return true;
        }

        /// <summary>高さ6段×横3点の候補を調べ、弾道の最初の着弾先が有利な狙点を選ぶ。</summary>
        private Vector3 FindWeakPoint(TankInfo enemy, Vector3 muzzle, int enemyId)
        {
            // 探索結果は敵ごとに0.30秒共有する。砲台ごとの再探索を減らすための近似。
            if (Time.time < m_nextWeakPointScan[enemyId]) return m_weakPointOffsets[enemyId];
            m_nextWeakPointScan[enemyId] = Time.time + 0.30f;
            Vector3 side = Vector3.Cross(Vector3.up, Flatten(enemy.Position - muzzle)).normalized;
            float bestScore = float.NegativeInfinity;
            Vector3 best = Vector3.zero;
            // sideは敵の正面方向ではなく、こちらから見た左右方向。
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
                    // 中央で本体への有効な射線が見つかったら、残りの候補探索を打ち切る。
                    m_weakPointOffsets[enemyId] = best;
                    m_weakPointQuality[enemyId] = bestScore;
                    return best;
                }
            }
            m_weakPointOffsets[enemyId] = best;
            m_weakPointQuality[enemyId] = bestScore;
            return best;
        }

        /// <summary>仮想砲弾の最初の接触先を調べ、本体10・装甲1・砲台-10などの点数で返す。</summary>
        private float ScoreVisibleImpact(Vector3 start, Vector3 direction, float flightTime, Vector3 targetPosition)
        {
            Transform ownRoot = transform.parent != null ? transform.parent : transform;
            Vector3 gravity = Physics.gravity * SHELL_GRAVITY_SCALE;
            Vector3 previous = start;
            int shellMask = 1 << LayerMask.NameToLayer("Shell");
            // 放物線を0.06秒ごとの区間に分け、球の太さも含めて遮蔽物を調べる。
            for (float time = 0.06f; time < flightTime + 0.10f; time += 0.06f)
            {
                Vector3 next = start + direction * SHELL_SPEED * time + gravity * (0.5f * time * time);
                Vector3 segment = next - previous;
                float length = segment.magnitude;
                int count = Physics.SphereCastNonAlloc(previous, 0.23f, segment / length,
                    m_visionHits, length, ~shellMask, QueryTriggerInteraction.Ignore);
                Collider first = null;
                float nearest = float.PositiveInfinity;
                // NonAllocのヒット配列は距離順とは限らないため、この区間で最も近い接触を探す。
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
                    // 狙った位置の近くにいる戦車への着弾か確認する。岩や別の場所なら低評価。
                    var tank = first.attachedRigidbody != null ? first.attachedRigidbody.GetComponent<BaseTank>() : null;
                    if (tank == null || Vector3.Distance(tank.transform.position, targetPosition) > 4.0f) return -20;
                    if (first.GetComponentInParent<TurretPart>() != null) return -10;
                    // 相手の装甲にもCapsuleColliderは使えるので、Colliderの形だけでは急所と判断しない。
                    // 標準の本体・操縦席は参加者のComPlayerBase配下にないことを使って区別する。
                    if (first.GetComponentInParent<ComPlayerBase>() == null) return 10;
                    return 1;
                }
                previous = next;
            }
            // どこにも当たらない予測射線も、本体・装甲に届く射線より低く評価する。
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

            // 球のCastだけでは開始点に重なる物体を拾えない場合があるので、砲口の重なりも調べる。
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
            // 自車との干渉は発射直後の0.14秒までを確認する。遠方の敵判定とは分けて扱う。
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

        /// <summary>発射砲台自身と弾が衝突しない部品を除き、自車の遮蔽物だけを判定する。</summary>
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
            // Collider個別の除外設定と、レイヤー間の衝突設定を反映する。
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

            // 0.08秒ごとの短い球Castで弾道を近似する。地形レイヤーから岩のタグを探す。
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
                        // Colliderが岩の子にある場合も拾えるよう、親階層までObstacleタグを確認する。
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
        /// <remarks>旧方式の補助関数。現在のAimAndFireでは使わず、SolveBallisticで方向を求める。</remarks>
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
            // 外周では戦闘中の間合い調整より中央へ戻ることを優先する。
            if (flatPosition.magnitude > fieldRadius * 0.88f)
            {
                DriveVector(-flatPosition.normalized);
                return;
            }

            Vector3 towardEnemy = toEnemy / distance;
            Vector3 tangent = Vector3.Cross(Vector3.up, towardEnemy).normalized;
            float orbitSign = Mathf.Sin(m_randomPhase) >= 0.0f ? 1.0f : -1.0f;

            // 敵の車体正面を横切り続けないよう、向きとの位置関係から周回側を選ぶ。
            Vector3 enemyForward = Flatten(enemy.Rotation * Vector3.forward).normalized;
            Vector3 toSelf = -towardEnemy;
            float flankProgress = Vector3.Dot(tangent, enemyForward);
            if (Mathf.Abs(flankProgress) > 0.12f)
                orbitSign = -Mathf.Sign(flankProgress);
            bool enemyPressuring = Vector3.Dot(enemyForward, toSelf) > 0.45f
                || Vector3.Dot(m_enemyTracks[m_targetId].Velocity, toSelf) > 4.0f;

            // radialは接近/後退、tangentは横方向。両者を足し合わせて間合いを保ちながら周回する。
            float radial = Mathf.Clamp((distance - DESIRED_RANGE) / 8.0f, -1.0f, 1.0f);
            float orbitWeight = distance > MAX_RANGE ? 0.18f : 0.72f;
            Vector3 desired = towardEnemy * radial + tangent * orbitSign * orbitWeight;
            // 小さな周期変化を加え、横移動を完全な一定方向にしない。
            desired += tangent * (0.12f * Mathf.Sin(Time.time * 1.9f + m_randomPhase));

            if (m_shotPathBlocked)
            {
                // 岩に撃ち続けず、仮想砲口から射線を調べて横に回り込む。
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

            // 外周の70%から中央向きの成分を徐々に強め、端まで追い込まれるのを抑える。
            float radius = flatPosition.magnitude;
            if (radius > fieldRadius * 0.70f && radius > 0.01f)
            {
                float edgeWeight = Mathf.Clamp01((radius - fieldRadius * 0.70f) / (fieldRadius * 0.22f));
                desired += -flatPosition.normalized * (1.0f + edgeWeight * 2.0f);
            }

            // 今の標的だけを見ず、22以内にいる別の敵からも距離を取って集中砲火を受けにくくする。
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
            // 実際に移動せず、砲口が左右へ7ずれた場合の射線を比較する。
            bool leftBlocked = WouldShotBeBlockedFrom(
                m_lastMuzzlePosition + side * flankDistance,
                m_shotTargetPoint);
            bool rightBlocked = WouldShotBeBlockedFrom(
                m_lastMuzzlePosition - side * flankDistance,
                m_shotTargetPoint);

            if (leftBlocked != rightBlocked)
                return leftBlocked ? -side : side;

            // 両側の判定が同じなら岩から離れる側、判断できなければ初期位置由来の側を選ぶ。
            Vector3 toBlocker = Flatten(m_shotBlockingObstacle - m_selfPosition);
            if (toBlocker.sqrMagnitude > 0.01f)
                return Vector3.Dot(side, toBlocker) > 0.0f ? -side : side;

            return side * Mathf.Sign(Mathf.Sin(m_randomPhase));
        }

        /// <summary>仮想砲口から重力補正した概算射線を作り、回り込み候補を評価する。</summary>
        private bool WouldShotBeBlockedFrom(Vector3 muzzlePosition, Vector3 targetPoint)
        {
            Vector3 delta = targetPoint - muzzlePosition;
            float distance = delta.magnitude;
            float flightTime = distance / SHELL_SPEED;
            Vector3 launchDirection = (delta - Physics.gravity * SHELL_GRAVITY_SCALE
                * (0.5f * flightTime * flightTime)).normalized;
            return IsShotBlockedByObstacle(muzzlePosition, launchDirection, distance, out _);
        }

        /// <summary>有効な標的がいない時は中央へ戻る。中央付近では左右同出力でゆっくり前進する。</summary>
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
            // 戦術上の進みたい方向を、弾の回避→岩の回避の順に補正する。
            desiredDirection = EvadeIncomingShells(desiredDirection);
            desiredDirection = AvoidObstacles(desiredDirection);
            if (UpdateStuckRecovery())
                // 脱出処理が履帯出力を設定済みなら、通常操舵で上書きしない。
                return;

            desiredDirection.Normalize();
            Vector3 forward = Flatten(m_selfRotation * Vector3.forward);
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;
            forward.Normalize();

            float angle = Vector3.SignedAngle(forward, desiredDirection, Vector3.up);
            float turn = Mathf.Clamp(angle / 62.0f, -1.0f, 1.0f);
            // 大きく向きがずれた時は前進を抑え、左右履帯の出力差でその場の旋回を優先する。
            float forwardPower = Mathf.Abs(angle) > 110.0f ? 0.0f : 1.0f;
            if (Mathf.Abs(angle) > 65.0f)
                forwardPower *= 0.25f;

            SXG_SetCaterpillarPower(
                Mathf.Clamp(forwardPower + turn * 0.82f, -1.0f, 1.0f),
                Mathf.Clamp(forwardPower - turn * 0.82f, -1.0f, 1.0f));
        }

        /// <summary>実弾との相対速度から最接近時刻を概算し、危険なら横方向の回避を加える。</summary>
        private Vector3 EvadeIncomingShells(Vector3 desired)
        {
            if (m_body == null) return desired;
            Vector3 selfVelocity = Flatten(m_body.linearVelocity);
            // 20以内にある砲弾だけを調べる。遅すぎる物体や離れていく弾は回避対象から外す。
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
                // t=-dot(相対位置,相対速度)/|相対速度|^2で水平面の最接近時刻を求める。
                float time = closing / relativeVelocity.sqrMagnitude;
                if (time < 0.06f || time > 0.70f) continue;
                // 時刻の推定後に弾の重力落下も反映し、水平距離と高さの両方で危険を絞り込む。
                Vector3 impact = projectile.position + projectile.linearVelocity * time
                    + Physics.gravity * (0.5f * SHELL_GRAVITY_SCALE * time * time);
                Vector3 futureSelf = m_selfPosition + selfVelocity * time;
                float miss = Flatten(impact - futureSelf).magnitude;
                if (miss > 2.6f || Mathf.Abs(impact.y - futureSelf.y - 0.45f) > 2.1f) continue;
                Vector3 side = Vector3.Cross(Vector3.up, relativeVelocity).normalized;
                // 弾道に対する左右のうち、今の移動方向と中央への戻りやすさに合う側を選ぶ。
                float leftScore = Vector3.Dot(side, desired.normalized)
                    - Flatten(m_selfPosition + side * 4).magnitude * 0.045f;
                float rightScore = Vector3.Dot(-side, desired.normalized)
                    - Flatten(m_selfPosition - side * 4).magnitude * 0.045f;
                if (rightScore > leftScore) side = -side;
                float risk = (1.0f - miss / 3.0f) * (1.0f - time / 1.0f);
                // 複数の弾への回避が打ち消し合わないよう、最も危険な1発に対する方向を採用する。
                if (risk > strongest) { strongest = risk; dodge = side; }
            }
            return strongest > 0 ? desired * (1.0f - strongest * 0.6f) + dodge * (strongest * 2.2f) : desired;
        }

        /// <summary>近傍の障害物の位置を一定間隔で取得し、AvoidObstaclesで使うため保存する。</summary>
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

                    // alongは候補方向に沿う距離、lateralは候補方向から横へ離れた距離。
                    // 近くて進路の中央にある岩ほど危険とし、その候補で最大の危険度を残す。
                    float lateral = Mathf.Abs(Vector3.Dot(toObstacle, side));
                    float clearanceRisk = Mathf.Clamp01((OBSTACLE_CLEARANCE - lateral) / OBSTACLE_CLEARANCE);
                    float distanceWeight = 1.0f - Mathf.Clamp01(Mathf.Max(0.0f, along) / OBSTACLE_LOOKAHEAD);
                    danger = Mathf.Max(danger, clearanceRisk * distanceWeight);
                }

                float deviationCost = Mathf.Abs(angle) / 90.0f * 1.05f;
                // 目的方向からのずれと危険度を合算。微小なtieBreakで左右同点時の迷いを抑える。
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
                // 両履帯を後退させつつ出力差を付ける。通常操舵とは別に一定時間やり切る。
                float left = m_escapeSign > 0 ? -0.85f : -0.15f;
                float right = m_escapeSign > 0 ? -0.15f : -0.85f;
                SXG_SetCaterpillarPower(left, right);
                return true;
            }

            if (m_escapeUntil > 0.0f)
            {
                // 脱出終了時に距離の基準を取り直し、すぐ再発動しない待ち時間を設ける。
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
                // 0.70秒ごとの測定で2回続けて進めなければ脱出する。毎回旋回側を反転して試す。
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

        /// <summary>配列範囲内にいて、敗退も出撃直後の無敵状態にもなっていない相手か確認する。</summary>
        private static bool IsEnemyAvailable(TankInfo[] tanks, int id)
        {
            return id > 0 && id < PLAYER_COUNT && !tanks[id].IsDefeated && !tanks[id].IsInvincible;
        }

        /// <summary>高さを無視した水平面の判断用に、Y成分だけ0にする。</summary>
        private static Vector3 Flatten(Vector3 value)
        {
            value.y = 0.0f;
            return value;
        }
    }
}
