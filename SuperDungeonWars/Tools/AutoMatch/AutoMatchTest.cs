using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// SuperDungeonWarsの4チーム連戦と勝率集計。ゲーム型へのアセンブリ参照は不要。
/// Assetsへこの1本を置き、Play中のInspectorから開始する。
/// </summary>
[DefaultExecutionOrder(-20000)]
[DisallowMultipleComponent]
public sealed class AutoMatchTest : MonoBehaviour
{
    [Header("0番が勝率を測る対象。Party用Prefabを指定")]
    public GameObject[] players = new GameObject[4];
    [Min(1)] public int matchCount = 24;
    public bool rotateSeats = true;
    [Tooltip("ON: 同じ審査済み迷路で4位置を回ってから次の迷路へ進む。")]
    public bool cycleApprovedMaps = true;
    [Min(0)] public int firstMapIndex;
    [Tooltip("AI・宝箱価値等に使うUnity乱数の開始Seed。迷路Seedは審査済み一覧から選ぶ。")]
    public int firstSeed = 12345;
    [Range(20, 240)] public int fps = 120;
    [Range(1f, 8f)] public float speedMultiplier = 4f;
    [Tooltip("カウントダウンを省略し、ResultのDuration/Delay/Intervalを0にする。")]
    public bool fastPresentation = true;
    [Min(10f)] public float matchTimeoutSeconds = 300f;

    [Serializable]
    public sealed class PartyStatistics
    {
        public string name;
        public int wins, draws, losses;
        public long scoreTotal, rankTotal;
    }

    public bool IsRunning { get { return m_running; } }
    public string State { get { return m_state; } }
    public int Played { get { return m_played; } }
    public float WinRate { get { return m_played == 0 ? 0f : 100f * m_statistics[0].wins / m_played; } }
    public string SavePath { get { return m_savePath; } }
    public PartyStatistics[] Statistics { get { return m_statistics; } }
    public int[] Scores { get { return m_scores; } }
    public int TargetSeat { get { return m_targetSeat + 1; } }
    public string MapSeed { get { return m_observedMapSeed ? m_currentMapSeed.ToString() : "生成待ち"; } }

    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private bool m_running, m_quitting, m_savedSettings, m_scenePrepared, m_recorded;
    private bool m_waitingForPreparation, m_countdownRequested, m_resultStarted;
    private bool m_observedMapSeed;
    private int m_played, m_targetSeat, m_currentMapSeed, m_expectedMapSeed, m_currentRandomSeed;
    private string m_state = "未実行", m_savePath = "", m_scenePath, m_lastFlow = "";
    private float m_matchEnteredAt, m_flowEnteredAt;
    private Component[] m_players;
    private int[] m_rolesBySeat = new int[4];
    private int[] m_scores = new int[4];
    private PartyStatistics[] m_statistics = new PartyStatistics[0];
    private MonoBehaviour m_game, m_flow, m_result, m_transition;
    private ScriptableObject m_originalRegistry, m_runtimeRegistry;
    private int[] m_mapTable, m_savedMapTable;
    private float m_oldTimeScale, m_oldCaptureDeltaTime, m_oldFixedDeltaTime;
    private int m_oldVSync, m_oldTargetFrameRate;
    private bool m_oldRunInBackground;
    private readonly List<MonoBehaviour> m_suspendedTools = new List<MonoBehaviour>();
    private readonly HashSet<MonoBehaviour> m_timingTargets = new HashSet<MonoBehaviour>();
    private readonly List<Timing> m_timings = new List<Timing>();

    private sealed class Timing
    {
        internal MonoBehaviour target;
        internal FieldInfo field;
        internal float original;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateOnce()
    {
        // 起動時だけ生成する。再読込するシーンには配置しない。
        if (FindAll<AutoMatchTest>().Length != 0) return;
        new GameObject(nameof(AutoMatchTest)).AddComponent<AutoMatchTest>();
    }
#endif

    private void Awake()
    {
        if (transform.parent != null || transform.childCount != 0 || GetComponents<Component>().Length != 2)
        {
            m_state = "専用の空のルートGameObjectで使用してください。通常は自動生成されます。";
            enabled = false;
            return;
        }
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        if (players == null || players.Length != 4 || Array.TrueForAll(players, player => player == null))
            LoadCurrentPlayers();
    }

    public void LoadCurrentPlayers()
    {
        if (m_running || !Application.isPlaying) return;
        try
        {
            MonoBehaviour game = FindInScene(SceneManager.GetActiveScene(), "BattlePrototypeGameController");
            object registry = Read(game, "m_battlePartyRegistry");
            Array prefabs = Read(registry, "PartyPrefabs") as Array;
            if (prefabs == null || prefabs.Length < 4) throw new InvalidOperationException("登録済みPartyが4チームありません。");
            var current = new GameObject[4];
            for (int role = 0; role < 4; role++)
            {
                Component prefab = prefabs.GetValue(role) as Component;
                if (prefab == null) throw new InvalidOperationException("登録済みPartyの" + role + "番が空です。");
                current[role] = prefab.gameObject;
            }
            players = current;
            m_state = "現在の4チームを読み込みました。0番が測定対象です。";
        }
        catch (Exception exception) { m_state = ErrorMessage(exception); }
    }

    public void StartMatches()
    {
        if (m_running || !Application.isPlaying || !isActiveAndEnabled) return;
        try
        {
            Scene scene = SceneManager.GetActiveScene();
            FindInScene(scene, "BattlePrototypeSceneFlowController");
            FindInScene(scene, "BattlePrototypeGameController");
            if (string.IsNullOrEmpty(scene.path) || scene.buildIndex < 0)
                throw new InvalidOperationException("MazeExploreを保存し、Build Settingsのシーンに含めてください。");
            if (FindAll<AutoMatchTest>().Length != 1)
                throw new InvalidOperationException("AutoMatchTestをシーンから外し、自動生成される1個を使ってください。");
            if (matchCount < 1 || firstMapIndex < 0 || fps < 20 || fps > 240
                || !Finite(speedMultiplier) || speedMultiplier < 1f || speedMultiplier > 8f
                || !Finite(matchTimeoutSeconds) || matchTimeoutSeconds < 10f)
                throw new InvalidOperationException("試合数1以上、Map Index 0以上、FPS 20～240、速度1～8、Timeout 10秒以上にしてください。");
            if (players == null || players.Length != 4)
                throw new InvalidOperationException("Playersは4チームを指定してください。");
            var configuredPlayers = new Component[4];
            var statistics = new PartyStatistics[4];
            for (int role = 0; role < 4; role++)
            {
                GameObject prefab = players[role];
                if (prefab == null || prefab.scene.IsValid())
                    throw new InvalidOperationException("Playersの" + role + "番にはProject内のParty用Prefabを指定してください。");
                foreach (MonoBehaviour component in prefab.GetComponents<MonoBehaviour>())
                    if (component != null && HasTypeName(component.GetType(), "ComPartyBase")) configuredPlayers[role] = component;
                if (configuredPlayers[role] == null)
                    throw new InvalidOperationException(prefab.name + "はParty用Prefabではありません。Character用Prefabは使用できません。");
                statistics[role] = new PartyStatistics { name = prefab.name + " [" + configuredPlayers[role].GetType().Name + "]" };
            }

            m_players = configuredPlayers;
            m_statistics = statistics;
            m_scenePath = scene.path;
            m_played = 0;
            m_scores = new int[4];
            m_scenePrepared = m_recorded = m_observedMapSeed = false;
            m_oldTimeScale = Time.timeScale;
            m_oldCaptureDeltaTime = Time.captureDeltaTime;
            m_oldFixedDeltaTime = Time.fixedDeltaTime;
            m_oldVSync = QualitySettings.vSyncCount;
            m_oldTargetFrameRate = Application.targetFrameRate;
            m_oldRunInBackground = Application.runInBackground;
            m_savedSettings = true;
            m_savePath = "";
            SuspendOtherDebugTools();
#if UNITY_EDITOR
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "AutoMatchResults"));
#else
            string folder = Path.Combine(Application.persistentDataPath, "AutoMatchResults");
#endif
            Directory.CreateDirectory(folder);
            m_savePath = Path.Combine(folder, "Dungeon_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".csv");
            File.WriteAllText(m_savePath,
                "Match,MapSeed,UnitySeed,TargetSeat,Score0,Score1,Score2,Score3,Rank0,Rank1,Rank2,Rank3,Exported0,Exported1,Exported2,Exported3,Result,WinRate\r\n",
                new UTF8Encoding(true));
            m_running = true;
            m_state = "初回の試合を準備中";
            m_matchEnteredAt = Time.realtimeSinceStartup;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.logMessageReceived += OnLog;
            ApplyRuntimeSettings();
            // 既に通常試合が初期化済みでも、設定を反映した新しい試合から測定する。
            if (SceneManager.LoadSceneAsync(m_scenePath) == null)
                throw new InvalidOperationException("対戦シーンを読み込めませんでした。");
        }
        catch (Exception exception)
        {
            if (m_running || m_savedSettings) StopMatches(ErrorMessage(exception));
            else m_state = ErrorMessage(exception);
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!m_running) return;
        try
        {
            if (scene.path != m_scenePath) throw new InvalidOperationException("別のシーンへ移動したため計測を停止しました。");
            if (m_scenePrepared && !m_recorded) throw new InvalidOperationException("結果を記録する前にシーンが再読み込みされました。");
            if (FindAll<AutoMatchTest>().Length != 1)
                throw new InvalidOperationException("シーン内にAutoMatchTestのコピーがあります。シーンへのアタッチを外してください。");
            m_game = FindInScene(scene, "BattlePrototypeGameController");
            m_flow = FindInScene(scene, "BattlePrototypeSceneFlowController");
            m_result = Read(m_flow, "m_battleResultPresentation") as MonoBehaviour;
            if (m_result == null) throw new InvalidOperationException("Result表示のコンポーネントがありません。");
            SuspendOtherDebugTools();
            m_originalRegistry = Read(m_game, "m_battlePartyRegistry") as ScriptableObject;
            if (m_originalRegistry == null) throw new InvalidOperationException("BattlePartyRegistryがありません。");
            if (m_runtimeRegistry == null)
            {
                m_runtimeRegistry = Instantiate(m_originalRegistry);
                m_runtimeRegistry.name = "AutoMatchTest_RuntimeRegistry";
                m_runtimeRegistry.hideFlags = HideFlags.DontSave;
            }
            FieldInfo partyField = Field(m_runtimeRegistry.GetType(), "m_partyPrefabs");
            Array order = Array.CreateInstance(partyField.FieldType.GetElementType(), 4);
            m_targetSeat = rotateSeats ? m_played % 4 : 0;
            for (int role = 0; role < 4; role++)
            {
                int seat = (role + m_targetSeat) % 4;
                order.SetValue(m_players[role], seat);
                m_rolesBySeat[seat] = role;
            }
            partyField.SetValue(m_runtimeRegistry, order);
            Field(m_game.GetType(), "m_battlePartyRegistry").SetValue(m_game, m_runtimeRegistry);
            m_observedMapSeed = false;
            int mapRound = rotateSeats ? m_played / 4 : m_played;
            m_currentRandomSeed = unchecked(firstSeed + mapRound);
            if (cycleApprovedMaps) PinApprovedMap(mapRound);
            UnityEngine.Random.InitState(m_currentRandomSeed);
            m_scenePrepared = m_waitingForPreparation = true;
            m_recorded = m_countdownRequested = m_resultStarted = false;
            m_lastFlow = "";
            m_matchEnteredAt = m_flowEnteredAt = Time.realtimeSinceStartup;
            m_state = "試合 " + (m_played + 1) + "/" + matchCount + " を準備中";
            m_timingTargets.Clear();
            m_timings.Clear();
            ApplyRuntimeSettings();
            // sceneLoadedはゲームのStart/PrepareBattleより前に呼ばれる。
        }
        catch (Exception exception) { StopMatches(ErrorMessage(exception)); }
    }

    private void PinApprovedMap(int mapRound)
    {
        Type generator = m_game.GetType().Assembly.GetType("MazeGenerator");
        FieldInfo tableField = generator == null ? null : generator.GetField("s_preapprovedMazeSeeds", StaticFlags);
        m_mapTable = tableField == null ? null : tableField.GetValue(null) as int[];
        if (m_mapTable == null || m_mapTable.Length == 0)
            throw new InvalidOperationException("審査済み迷路の一覧を取得できません。通常の迷路選択を使う場合はCycle Approved MapsをOFFにしてください。");
        m_savedMapTable = (int[])m_mapTable.Clone();
        int index = (int)(((long)firstMapIndex + mapRound) % m_mapTable.Length);
        m_expectedMapSeed = m_savedMapTable[index];
        // readonly配列自体を差し替えず、選択候補をPrepareBattleの間だけ同じSeedにする。
        // 元の一覧は生成直後、停止時、エラー時に復元する。
        for (int i = 0; i < m_mapTable.Length; i++) m_mapTable[i] = m_expectedMapSeed;
    }

    private void RestoreMapTable()
    {
        if (m_mapTable != null && m_savedMapTable != null)
            Array.Copy(m_savedMapTable, m_mapTable, m_savedMapTable.Length);
        m_mapTable = m_savedMapTable = null;
    }

    private void Update()
    {
        if (!m_running) return;
        try
        {
            ApplyRuntimeSettings();
            if (Time.realtimeSinceStartup - m_matchEnteredAt > matchTimeoutSeconds)
                throw new TimeoutException(m_state + "のまま制限時間を超えました。未完了の試合は集計していません。");
            if (!m_scenePrepared) return;
            if (m_game == null || m_flow == null) throw new InvalidOperationException("試合の管理オブジェクトが失われました。");
            if (m_waitingForPreparation)
            {
                if (Read(m_game, "MazeData") == null || Convert.ToInt32(Read(m_game, "SpawnedPartyCount")) != 4) return;
                RestoreMapTable();
                if (cycleApprovedMaps && (!m_observedMapSeed || m_currentMapSeed != m_expectedMapSeed))
                    throw new InvalidOperationException("この迷路生成版では指定した審査済みSeedを確認できません。Cycle Approved MapsをOFFにしてください。");
                m_waitingForPreparation = false;
            }
            string flow = Read(m_flow, "CurrentSceneFlow").ToString();
            if (flow != m_lastFlow)
            {
                m_lastFlow = flow;
                m_flowEnteredAt = Time.realtimeSinceStartup;
            }
            m_state = (m_played + 1) + "/" + matchCount + "  " + flow;
            switch (flow)
            {
                case "TeamIntroduction":
                    if (Time.realtimeSinceStartup - m_flowEnteredAt < 0.2f || TransitionIsPlaying()) break;
                    Call(m_flow, "HideBattleTeamIntroductionPresentation");
                    ChangeFlow("CountDown");
                    break;
                case "CountDown":
                    if (m_countdownRequested || !Convert.ToBoolean(Read(m_flow, "m_isBattleStartCountdownInputAccepted"))) break;
                    m_countdownRequested = true;
                    if (fastPresentation)
                    {
                        Call(m_flow, "HideBattleStartCountdownPresentation");
                        Call(m_flow, "OnBattleStartCountdownCompleted");
                    }
                    else
                    {
                        MonoBehaviour countdown = Read(m_flow, "m_battleStartCountdownPresentation") as MonoBehaviour;
                        if (countdown == null) throw new InvalidOperationException("カウントダウン表示がありません。");
                        countdown.StartCoroutine((IEnumerator)Call(countdown, "CoPlayCountdown"));
                    }
                    break;
                case "BattleEndWait":
                    Call(m_flow, "OnBattleEndPresentationProceedRequested");
                    break;
                case "Result":
                    UpdateResult();
                    break;
                case "Finish":
                    if (!m_recorded) throw new InvalidOperationException("結果の記録前にFinishへ進みました。");
                    break;
            }
        }
        catch (Exception exception) { StopMatches(ErrorMessage(exception)); }
    }

    private bool TransitionIsPlaying()
    {
        if (m_transition == null)
            foreach (MonoBehaviour behaviour in FindAll<MonoBehaviour>())
                if (behaviour != null && HasTypeName(behaviour.GetType(), "MazeThemeTransitionController")) m_transition = behaviour;
        return m_transition != null && Convert.ToBoolean(Read(m_transition, "IsPlaying"));
    }

    private void ChangeFlow(string name)
    {
        MethodInfo method = Method(m_flow.GetType(), "ChangeSceneFlow", 1);
        method.Invoke(m_flow, new[] { Enum.Parse(method.GetParameters()[0].ParameterType, name) });
    }

    private void UpdateResult()
    {
        if (!m_result.gameObject.activeInHierarchy) return;
        if (fastPresentation) ShortenResultTimings();
        if (!m_resultStarted)
        {
            if (TransitionIsPlaying()) return;
            object result = Read(m_game, "BattleResult");
            if (result == null) throw new InvalidOperationException("ゲームの確定結果がありません。");
            Call(m_result, "StartResultSequence", result);
            m_resultStarted = true;
        }
        if (!Convert.ToBoolean(Read(m_result, "IsResultSequenceCompleted")) || m_recorded) return;
        RecordResult();
        m_recorded = true;
        if (m_played >= matchCount) StopMatches("全" + m_played + "試合完了。");
        else ChangeFlow("Finish");
    }

    private void ShortenResultTimings()
    {
        foreach (MonoBehaviour target in m_result.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (target == null || m_timingTargets.Contains(target)) continue;
            Type type = target.GetType();
            if (!HasTypeName(type, "BattleResultPresentation") && !HasTypeName(type, "BattleResultTeamLane")) continue;
            m_timingTargets.Add(target);
            for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
                foreach (FieldInfo field in current.GetFields(InstanceFlags | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(float) || field.IsInitOnly
                        || field.IsDefined(typeof(NonSerializedAttribute), false)
                        || (!field.IsPublic && !field.IsDefined(typeof(SerializeField), false))) continue;
                    if (!field.Name.EndsWith("Duration", StringComparison.OrdinalIgnoreCase)
                        && !field.Name.EndsWith("Delay", StringComparison.OrdinalIgnoreCase)
                        && !field.Name.EndsWith("Interval", StringComparison.OrdinalIgnoreCase)) continue;
                    float value = (float)field.GetValue(target);
                    if (!Finite(value) || value <= 0f) continue;
                    m_timings.Add(new Timing { target = target, field = field, original = value });
                    field.SetValue(target, 0f);
                }
        }
    }

    private void RecordResult()
    {
        IEnumerable results = Read(Read(m_game, "BattleResult"), "TeamResults") as IEnumerable;
        if (results == null) throw new InvalidOperationException("4チーム分の結果がありません。");
        int[] scores = new int[4], ranks = new int[4], exported = new int[4];
        bool[] seen = new bool[4];
        foreach (object result in results)
        {
            int seat = Convert.ToInt32(Read(result, "TeamIndex"));
            if (seat < 0 || seat >= 4 || seen[seat]) throw new InvalidOperationException("結果のチーム番号が不正です。");
            seen[seat] = true;
            int role = m_rolesBySeat[seat];
            scores[role] = Convert.ToInt32(Read(result, "TotalValue"));
            ranks[role] = Convert.ToInt32(Read(result, "Rank"));
            foreach (object treasure in (IEnumerable)Read(result, "Treasures")) exported[role]++;
        }
        if (Array.Exists(seen, present => !present)) throw new InvalidOperationException("4チーム分の結果が揃っていません。");
        int best = Mathf.Max(Mathf.Max(scores[0], scores[1]), Mathf.Max(scores[2], scores[3]));
        int topCount = 0;
        for (int role = 0; role < 4; role++) if (scores[role] == best) topCount++;
        bool targetWins = scores[0] == best && topCount == 1;
        string outcome = scores[0] < best ? "負け" : targetWins ? "勝ち" : "同点1位";
        float nextRate = 100f * (m_statistics[0].wins + (targetWins ? 1 : 0)) / (m_played + 1);
        File.AppendAllText(m_savePath, Csv(
            m_played + 1, m_observedMapSeed ? (object)m_currentMapSeed : "", m_currentRandomSeed, m_targetSeat + 1,
            scores[0], scores[1], scores[2], scores[3], ranks[0], ranks[1], ranks[2], ranks[3],
            exported[0], exported[1], exported[2], exported[3], outcome, nextRate.ToString("F3", CultureInfo.InvariantCulture)),
            new UTF8Encoding(false));
        for (int role = 0; role < 4; role++)
        {
            PartyStatistics statistics = m_statistics[role];
            if (scores[role] < best) statistics.losses++;
            else if (topCount == 1) statistics.wins++;
            else statistics.draws++;
            statistics.scoreTotal += scores[role];
            statistics.rankTotal += ranks[role];
        }
        m_scores = scores;
        m_played++;
        WriteSummary("計測中");
        Debug.Log("AutoMatchTest " + m_played + "/" + matchCount + "試合、対象の勝率=" + WinRate.ToString("F1") + "%");
    }

    private void ApplyRuntimeSettings()
    {
        Time.timeScale = speedMultiplier;
        Time.captureDeltaTime = 1f / fps;
        Time.fixedDeltaTime = m_oldFixedDeltaTime;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Application.runInBackground = true;
    }

    private void SuspendOtherDebugTools()
    {
        foreach (MonoBehaviour tool in FindAll<MonoBehaviour>())
            if (tool != null && tool.enabled && (HasTypeName(tool.GetType(), "GameDebugController")
                || HasTypeName(tool.GetType(), "GameSpeedDebugController")
                || HasTypeName(tool.GetType(), "BattleResultFastForwardController")))
            {
                m_suspendedTools.Add(tool);
                tool.enabled = false;
            }
    }

    public void StopMatches() { StopMatches("途中停止。完了した" + m_played + "試合を保存しました。"); }

    private void StopMatches(string message)
    {
        m_running = false;
        m_state = message;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Application.logMessageReceived -= OnLog;
        RestoreMapTable();
        foreach (Timing timing in m_timings)
            if (timing.target != null && (float)timing.field.GetValue(timing.target) == 0f)
                timing.field.SetValue(timing.target, timing.original);
        m_timings.Clear();
        m_timingTargets.Clear();
        if (m_game != null && m_runtimeRegistry != null
            && ReferenceEquals(Read(m_game, "m_battlePartyRegistry"), m_runtimeRegistry))
            Field(m_game.GetType(), "m_battlePartyRegistry").SetValue(m_game, m_originalRegistry);
        if (m_runtimeRegistry != null) Destroy(m_runtimeRegistry);
        m_runtimeRegistry = null;
        if (!m_quitting && Application.isPlaying)
            foreach (MonoBehaviour tool in m_suspendedTools) if (tool != null) tool.enabled = true;
        m_suspendedTools.Clear();
        if (m_savedSettings)
        {
            Time.timeScale = m_oldTimeScale;
            Time.captureDeltaTime = m_oldCaptureDeltaTime;
            Time.fixedDeltaTime = m_oldFixedDeltaTime;
            QualitySettings.vSyncCount = m_oldVSync;
            Application.targetFrameRate = m_oldTargetFrameRate;
            Application.runInBackground = m_oldRunInBackground;
            m_savedSettings = false;
        }
        if (!string.IsNullOrEmpty(m_savePath))
        {
            try { WriteSummary(message); }
            catch (Exception exception) { m_state += " 集計ファイルの保存失敗: " + exception.Message; }
        }
        Debug.Log("AutoMatchTest: " + m_state + " CSV=" + m_savePath);
    }

    private void OnLog(string message, string stackTrace, LogType type)
    {
        if (!m_running) return;
        if (message.StartsWith("MazeGenerator:", StringComparison.Ordinal))
        {
            const string marker = "randomKey=";
            int start = message.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                start += marker.Length;
                int end = start;
                if (end < message.Length && message[end] == '-') end++;
                while (end < message.Length && char.IsDigit(message[end])) end++;
                int seed;
                if (int.TryParse(message.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                {
                    m_currentMapSeed = seed;
                    m_observedMapSeed = true;
                }
            }
        }
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            StopMatches("Unityエラーで中断: " + message);
    }

    private void WriteSummary(string status)
    {
        var text = new StringBuilder();
        text.AppendLine("SuperDungeonWars 自動対戦集計").AppendLine(status);
        text.AppendLine("完了: " + m_played + "/" + matchCount + "試合");
        text.AppendLine("勝率 = 単独1位 / 完了試合数。同点1位は別集計。");
        text.AppendLine("RotateSeats=" + rotateSeats + ", CycleApprovedMaps=" + cycleApprovedMaps + ", FirstMapIndex=" + firstMapIndex);
        text.AppendLine("FirstSeed=" + firstSeed + ", FPS=" + fps + ", Speed=" + speedMultiplier + ", FastPresentation=" + fastPresentation);
        text.AppendLine("回転ON時は4試合を1組とし、Unity開始Seedを共通にする。CycleApprovedMapsもONの場合は組内の迷路も共通。完全な再現性は保証しない。");
        for (int role = 0; role < m_statistics.Length; role++)
        {
            PartyStatistics statistics = m_statistics[role];
            if (statistics == null) continue;
            text.AppendLine(role + ": " + statistics.name);
            text.AppendFormat(CultureInfo.InvariantCulture,
                "  勝ち={0}, 同点1位={1}, 負け={2}, 勝率={3:F2}%, 平均得点={4:F2}, 平均順位={5:F2}\n",
                statistics.wins, statistics.draws, statistics.losses, m_played == 0 ? 0 : 100.0 * statistics.wins / m_played,
                m_played == 0 ? 0 : (double)statistics.scoreTotal / m_played, m_played == 0 ? 0 : (double)statistics.rankTotal / m_played);
        }
        text.AppendLine("CSVのScore/Rank/Exportedの0～3は開始位置ではなくPlayersの登録順。");
        File.WriteAllText(Path.ChangeExtension(m_savePath, ".txt"), text.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(params object[] values)
    {
        var row = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            if (i != 0) row.Append(',');
            row.Append('"').Append(Convert.ToString(values[i], CultureInfo.InvariantCulture).Replace("\"", "\"\"")).Append('"');
        }
        return row.AppendLine().ToString();
    }

    private static T[] FindAll<T>() where T : UnityEngine.Object
    {
#if UNITY_2022_2_OR_NEWER
        return UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        return UnityEngine.Object.FindObjectsOfType<T>(true);
#endif
    }

    private static MonoBehaviour FindInScene(Scene scene, string typeName)
    {
        MonoBehaviour found = null;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && behaviour.isActiveAndEnabled && HasTypeName(behaviour.GetType(), typeName))
                {
                    if (found != null) throw new InvalidOperationException(typeName + "が複数あります。");
                    found = behaviour;
                }
        if (found == null) throw new InvalidOperationException(typeName + "がありません。MazeExploreから開始してください。");
        return found;
    }

    private static bool HasTypeName(Type type, string name)
    {
        for (; type != null; type = type.BaseType) if (type.Name == name) return true;
        return false;
    }

    private static FieldInfo Field(Type type, string name)
    {
        for (Type current = type; current != null; current = current.BaseType)
        {
            FieldInfo field = current.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        throw new MissingFieldException(type.Name, name);
    }

    private static object Read(object owner, string name)
    {
        if (owner == null) throw new InvalidOperationException(name + "の参照元がありません。");
        PropertyInfo property = owner.GetType().GetProperty(name, InstanceFlags);
        return property == null ? Field(owner.GetType(), name).GetValue(owner) : property.GetValue(owner);
    }

    private static MethodInfo Method(Type type, string name, int argumentCount)
    {
        for (Type current = type; current != null; current = current.BaseType)
            foreach (MethodInfo method in current.GetMethods(InstanceFlags | BindingFlags.DeclaredOnly))
                if (method.Name == name && method.GetParameters().Length == argumentCount) return method;
        throw new MissingMethodException(type.Name, name);
    }

    private static object Call(object owner, string name, params object[] arguments)
    {
        if (owner == null) throw new InvalidOperationException(name + "の呼び出し先がありません。");
        return Method(owner.GetType(), name, arguments.Length).Invoke(owner, arguments);
    }

    private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    private static string ErrorMessage(Exception exception)
    {
        while (exception is TargetInvocationException && exception.InnerException != null) exception = exception.InnerException;
        return "エラー: " + exception.Message;
    }

    private void OnApplicationQuit() { m_quitting = true; }
    private void OnDisable()
    {
        if (m_running || m_savedSettings) StopMatches("計測終了。完了した試合だけ保存しました。");
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(AutoMatchTest))]
public sealed class AutoMatchTestEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var controller = (AutoMatchTest)target;
        serializedObject.Update();
        using (new EditorGUI.DisabledScope(controller.IsRunning)) DrawPropertiesExcluding(serializedObject, "m_Script");
        serializedObject.ApplyModifiedProperties();
        EditorGUILayout.HelpBox("Playersの0番が測定対象です。Rotate Seatsで全4位置を回し、Cycle Approved MapsもONなら4試合の迷路を揃えます。", MessageType.Info);
        using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || !controller.isActiveAndEnabled))
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(controller.IsRunning))
            {
                if (GUILayout.Button("現在の4チームを読込")) controller.LoadCurrentPlayers();
                if (GUILayout.Button("自動対戦を開始")) controller.StartMatches();
            }
            using (new EditorGUI.DisabledScope(!controller.IsRunning))
                if (GUILayout.Button("停止・保存")) controller.StopMatches();
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("状態", controller.State, EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("完了試合", controller.Played.ToString());
        EditorGUILayout.LabelField("対象の勝率", controller.WinRate.ToString("F2") + "%");
        if (controller.IsRunning)
            EditorGUILayout.LabelField("対象の位置 / 迷路Seed", controller.TargetSeat + " / " + controller.MapSeed);
        PartyStatisticsGUI(controller);
        if (!string.IsNullOrEmpty(controller.SavePath))
        {
            EditorGUILayout.LabelField("保存先", controller.SavePath, EditorStyles.wordWrappedLabel);
            if (GUILayout.Button("結果ファイルの場所を開く")) EditorUtility.RevealInFinder(controller.SavePath);
        }
        if (!EditorApplication.isPlaying) EditorGUILayout.HelpBox("Play中に設定して開始してください。シーンへの配置は不要です。", MessageType.Info);
    }

    private static void PartyStatisticsGUI(AutoMatchTest controller)
    {
        for (int role = 0; role < controller.Statistics.Length; role++)
        {
            AutoMatchTest.PartyStatistics statistics = controller.Statistics[role];
            if (statistics == null) continue;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(role + ": " + statistics.name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("勝ち / 同点1位 / 負け", statistics.wins + " / " + statistics.draws + " / " + statistics.losses);
            double rate = controller.Played == 0 ? 0 : 100.0 * statistics.wins / controller.Played;
            double mean = controller.Played == 0 ? 0 : (double)statistics.scoreTotal / controller.Played;
            EditorGUILayout.LabelField("勝率 / 平均得点", rate.ToString("F2") + "% / " + mean.ToString("F2"));
            if (controller.Played != 0) EditorGUILayout.LabelField("直前の得点", controller.Scores[role].ToString());
        }
    }

    public override bool RequiresConstantRepaint() { return EditorApplication.isPlaying; }
}
#endif
