using System;
using System.IO;
using System.Reflection;
using System.Text;
using SXG2025;
using SXG2025.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Inspectorから設定して使う連戦計測。入力待ちより先に処理する。
[DefaultExecutionOrder(-10000)]
public sealed class AutoMatchTest : MonoBehaviour
{
    [Header("Play前に設定：0番が勝率を測る対象")]
    public bool autoRun;
    public ComPlayerBase[] players = new ComPlayerBase[4];
    [Min(1)] public int matchCount = 24;
    public bool rotateSeats = true;
    public int firstSeed = 12345;
    [Range(20, 240)] public int fps = 120;
    [Range(1f, 8f)] public float speedMultiplier = 4f;

    [Header("実行結果：計測中は編集しない")]
    public int played;
    public int wins;
    public int draws;
    public int losses;
    public float winRate;
    public int[] scores = new int[4];
    public string state = "未実行";
    public string savePath;

    private static AutoMatchTest s_instance;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private bool m_running;
    private bool m_selected;
    private bool m_recorded;
    private int[] m_rolesBySeat;
    private int[] m_previousParticipants;
    private int m_previousRound;
    private string m_previousExitScene;
    private bool m_savedGameSettings;
    private float m_enteredAt;
    private float m_flowEnteredAt;
    private float m_resultReadyAt = -1f;
    private float m_nextPressAt;
    private float m_oldTimeScale;
    private float m_oldCaptureDeltaTime;
    private float m_oldFixedDeltaTime;
    private int m_oldVSync;
    private int m_oldTargetFrameRate;
    private bool m_oldRunInBackground;
    private BattleTanksManager.SceneFlow m_lastFlow = BattleTanksManager.SceneFlow.None;
    private BattleTanksManager m_battleManager;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { s_instance = null; }

    // 重複して読み込まれた選択シーン側のコピーは捨て、計測中の1個を保持する。
    private void Awake()
    {
        if (!autoRun) { enabled = false; return; }
        if (s_instance != null && s_instance != this)
        {
            enabled = false;
            Destroy(gameObject);
            return;
        }
        s_instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        m_oldTimeScale = Time.timeScale;
        m_oldCaptureDeltaTime = Time.captureDeltaTime;
        m_oldFixedDeltaTime = Time.fixedDeltaTime;
        m_oldVSync = QualitySettings.vSyncCount;
        m_oldTargetFrameRate = Application.targetFrameRate;
        m_oldRunInBackground = Application.runInBackground;
        try
        {
            if (SceneManager.GetActiveScene().name != "ParticipantSelection")
                throw new InvalidOperationException("ParticipantSelectionシーンからPlayしてください。");
            if (players == null || players.Length != 4 || matchCount < 1 || fps < 20 || fps > 240
                || speedMultiplier < 1f || speedMultiplier > 8f)
                throw new InvalidOperationException("Playersは4人、Match Countは1以上、FPSは20～240、倍率は1～8にしてください。");
            for (int i = 0; i < players.Length; i++)
                if (players[i] == null) throw new InvalidOperationException("Playersの" + i + "番が空です。");

            SaveOriginalGameSettings();

            played = wins = draws = losses = 0;
            winRate = 0f;
            scores = new int[4];
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "AutoMatchResults"));
            Directory.CreateDirectory(folder);
            savePath = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt");
            var header = new StringBuilder();
            for (int role = 0; role < 4; role++)
                header.Append(role).Append(": ").Append(players[role].YourName).Append(" [")
                    .Append(players[role].GetType().Name).AppendLine("]");
            header.AppendLine("試合\tSeed\t対象席\t対象点\t相手1点\t相手2点\t相手3点\t結果\t勝率");
            File.WriteAllText(savePath, header.ToString(), new UTF8Encoding(true));

            m_enteredAt = Time.realtimeSinceStartup;
            m_flowEnteredAt = m_enteredAt;
            m_running = true;
            state = "準備中";
            Application.runInBackground = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.logMessageReceived += OnLog;
            WriteStaticProperty(typeof(GameDataHolder), "GameExitSceneName", "ParticipantSelection");
        }
        catch (Exception exception)
        {
            Stop("エラー: " + exception.Message);
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        m_enteredAt = Time.realtimeSinceStartup;
        m_flowEnteredAt = m_enteredAt;
        m_selected = false;
        m_recorded = false;
        m_resultReadyAt = -1f;
        m_nextPressAt = 0f;
        m_battleManager = null;
        m_lastFlow = BattleTanksManager.SceneFlow.None;

        if (scene.name == "Game")
        {
            UnityEngine.Random.InitState(firstSeed + played);
        }
        else if (scene.name == "ParticipantSelection")
        {
            WriteStaticProperty(typeof(GameDataHolder), "GameExitSceneName", "ParticipantSelection");
        }
        else
        {
            state = "未対応のシーン: " + scene.name;
        }
    }

    private void Update()
    {
        if (!m_running) return;
        if (!autoRun) { Stop("途中停止。完了試合は保存済みです。"); return; }

        try
        {
            // fpsで時間刻みを固定し、倍率を上げて試合を早く進める。物理刻みは維持する。
            Time.timeScale = speedMultiplier;
            Time.captureDeltaTime = 1f / fps;
            Time.fixedDeltaTime = m_oldFixedDeltaTime;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            string scene = SceneManager.GetActiveScene().name;
            if (scene == "ParticipantSelection")
            {
                state = "参加者選択";
                if (played >= matchCount) { Stop("全試合完了。"); return; }
                if (!m_selected && Time.realtimeSinceStartup - m_enteredAt > 0.75f)
                    SelectParticipantsAndStart();
            }
            else if (scene == "Game")
            {
                UpdateGame();
            }
            else if (Time.realtimeSinceStartup - m_enteredAt > 5f)
            {
                throw new InvalidOperationException("未対応のシーンです: " + scene);
            }

            if (scene != "Game" && Time.realtimeSinceStartup - m_enteredAt > 30f)
                throw new TimeoutException(state + "から進めませんでした。");
        }
        catch (Exception exception)
        {
            Stop("エラー: " + exception.Message);
        }
    }

    private void SaveOriginalGameSettings()
    {
        object gameData = GetStaticProperty(typeof(GameDataHolder), "Instance");
        int[] participants = ReadProperty<int[]>(gameData, "ParticipantIndexes");
        m_previousParticipants = (int[])participants.Clone();
        object gameConfig = GetStaticProperty(typeof(GameConfigSetting), "Instance");
        m_previousRound = ReadProperty<int>(gameConfig, "RoundCount");
        m_previousExitScene = (string)GetStaticProperty(typeof(GameDataHolder), "GameExitSceneName");
        m_savedGameSettings = true;
    }

    private void SelectParticipantsAndStart()
    {
        ParticipantSelectionManager selection = FindObjectOfType<ParticipantSelectionManager>();
        if (selection == null) throw new InvalidOperationException("参加者選択の管理スクリプトがありません。");
        ParticipantList list = ReadField<ParticipantList>(selection, "m_participantList");
        ParticipantSelectionController controller = ReadField<ParticipantSelectionController>(selection, "m_controller");
        Button[] buttons = ReadField<Button[]>(selection, "m_decideButtons");
        MethodInfo updateParticipant = selection.GetType().GetMethod("UpdateParticipant", PrivateInstance);
        if (list == null || list.m_comPlayers == null || controller == null || buttons == null || buttons.Length == 0
            || updateParticipant == null)
            throw new InvalidOperationException("参加者一覧、選択欄、または決定ボタンが見つかりません。");

        int focusSeat = rotateSeats ? played % 4 : 0;
        m_rolesBySeat = new int[4];
        object gameData = GetStaticProperty(typeof(GameDataHolder), "Instance");
        int[] participantIndexes = ReadProperty<int[]>(gameData, "ParticipantIndexes");

        for (int role = 0; role < 4; role++)
        {
            int index = list.m_comPlayers.IndexOf(players[role]);
            if (index < 0) throw new InvalidOperationException("参加者リストに登録されていないPrefabです: " + players[role].name);
            int seat = (role + focusSeat) % 4;
            m_rolesBySeat[seat] = role;
            participantIndexes[seat] = index;
        }

        // 通常の選択欄にも名前を反映し、ゲーム本来の開始ボタンを押す。
        for (int seat = 0; seat < 4; seat++)
        {
            int role = m_rolesBySeat[seat];
            controller.UpdateSelectionNamePlate(seat);
            updateParticipant.Invoke(selection, new object[] { list.m_comPlayers.IndexOf(players[role]) });
        }

        object gameConfig = GetStaticProperty(typeof(GameConfigSetting), "Instance");
        WriteProperty(gameConfig, "RoundCount", played + 1);
        m_selected = true;
        foreach (Button button in buttons)
        {
            if (button == null) continue;
            button.interactable = true;
            button.onClick.Invoke();
            break;
        }
    }

    private void UpdateGame()
    {
        if (m_battleManager == null) m_battleManager = FindObjectOfType<BattleTanksManager>();
        if (m_battleManager == null) throw new InvalidOperationException("BattleTanksManagerが見つかりません。");

        BattleTanksManager.SceneFlow flow = m_battleManager.sceneFlow;
        if (flow != m_lastFlow)
        {
            m_lastFlow = flow;
            m_flowEnteredAt = Time.realtimeSinceStartup;
        }
        state = flow.ToString();
        float timeout = flow == BattleTanksManager.SceneFlow.Playing ? 180f : 60f;
        if (Time.realtimeSinceStartup - m_flowEnteredAt > timeout)
            throw new TimeoutException(state + "の進行が止まりました。");

        if (flow == BattleTanksManager.SceneFlow.ChallengersIntro || flow == BattleTanksManager.SceneFlow.CountDown)
        {
            if (Time.realtimeSinceStartup - m_flowEnteredAt > 0.5f) PressDecide();
        }
        else if (flow == BattleTanksManager.SceneFlow.Playing)
        {
            float remaining = ReadField<float>(m_battleManager, "m_gamePlayingTime");
            bool winnerDecided = ReadField<bool>(m_battleManager, "m_isWinnerDecided");
            if (remaining <= 0.05f || winnerDecided) PressDecide();
        }
        else if (flow == BattleTanksManager.SceneFlow.Result2)
        {
            ResultScreen2 resultScreen = ReadField<ResultScreen2>(m_battleManager, "m_resultScreen2UI");
            if (resultScreen == null || !resultScreen.IsEndResult) return;
            if (!m_recorded)
            {
                RecordResult(resultScreen);
                m_recorded = true;
                Debug.Log("AutoMatchTest " + played + "/" + matchCount + "試合、勝率=" + winRate.ToString("F1") + "%");
            }
            if (m_resultReadyAt < 0f) m_resultReadyAt = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - m_resultReadyAt > 0.5f) PressDecide();
        }
    }

    private void RecordResult(ResultScreen2 resultScreen)
    {
        int[] seatScores = resultScreen.TeamScore;
        if (seatScores == null || seatScores.Length != 4 || m_rolesBySeat == null)
            throw new InvalidOperationException("4人分の試合結果がありません。");

        int focusSeat = -1;
        int[] roleScores = new int[4];
        for (int seat = 0; seat < 4; seat++)
        {
            int role = m_rolesBySeat[seat];
            roleScores[role] = seatScores[seat];
            if (role == 0) focusSeat = seat;
        }
        Array.Copy(roleScores, scores, 4);

        int bestOpponent = Mathf.Max(scores[1], Mathf.Max(scores[2], scores[3]));
        string result;
        if (scores[0] > bestOpponent) { wins++; result = "勝ち"; }
        else if (scores[0] == bestOpponent) { draws++; result = "同点1位"; }
        else { losses++; result = "負け"; }

        played++;
        winRate = 100f * wins / played;
        string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8:F1}%",
            played, firstSeed + played - 1, focusSeat + 1, scores[0], scores[1], scores[2], scores[3], result, winRate);
        File.AppendAllText(savePath, line + Environment.NewLine, new UTF8Encoding(false));
    }

    // ゲーム本体の決定入力フラグを短時間だけ立て、既存の画面遷移を進める。
    private void PressDecide()
    {
        if (Time.realtimeSinceStartup < m_nextPressAt || GameInputManager.ms_instance == null) return;
        bool[] pressed = ReadField<bool[]>(GameInputManager.ms_instance, "m_wasPressed");
        if (pressed == null || pressed.Length <= (int)GameInputManager.Type.Decide) return;
        pressed[(int)GameInputManager.Type.Decide] = true;
        m_nextPressAt = Time.realtimeSinceStartup + 0.25f;
    }

    private static T ReadField<T>(object owner, string name)
    {
        FieldInfo field = owner.GetType().GetField(name, PrivateInstance);
        if (field == null) throw new MissingFieldException(owner.GetType().Name, name);
        return (T)field.GetValue(owner);
    }

    private static object GetStaticProperty(Type type, string name)
    {
        PropertyInfo property = type.GetProperty(name, PrivateStatic);
        if (property == null) throw new MissingMemberException(type.Name, name);
        return property.GetValue(null);
    }

    private static void WriteStaticProperty(Type type, string name, object value)
    {
        PropertyInfo property = type.GetProperty(name, PrivateStatic);
        if (property == null) throw new MissingMemberException(type.Name, name);
        property.SetValue(null, value);
    }

    private static T ReadProperty<T>(object owner, string name)
    {
        PropertyInfo property = owner.GetType().GetProperty(name, PrivateInstance);
        if (property == null) throw new MissingMemberException(owner.GetType().Name, name);
        return (T)property.GetValue(owner);
    }

    private static void WriteProperty(object owner, string name, object value)
    {
        PropertyInfo property = owner.GetType().GetProperty(name, PrivateInstance);
        if (property == null) throw new MissingMemberException(owner.GetType().Name, name);
        property.SetValue(owner, value);
    }

    private void OnLog(string message, string stackTrace, LogType type)
    {
        if (m_running && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
            Stop("Unityエラーで中断: " + message);
    }

    private void Stop(string message)
    {
        state = message;
        m_running = false;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Application.logMessageReceived -= OnLog;
        RestoreSettings();
        Debug.Log("AutoMatchTest: " + message + " 保存先=" + savePath);
    }

    private void RestoreSettings()
    {
        Time.timeScale = m_oldTimeScale;
        Time.captureDeltaTime = m_oldCaptureDeltaTime;
        Time.fixedDeltaTime = m_oldFixedDeltaTime;
        QualitySettings.vSyncCount = m_oldVSync;
        Application.targetFrameRate = m_oldTargetFrameRate;
        Application.runInBackground = m_oldRunInBackground;
        if (!m_savedGameSettings) return;

        object gameData = GetStaticProperty(typeof(GameDataHolder), "Instance");
        if (gameData != null) WriteProperty(gameData, "ParticipantIndexes", m_previousParticipants);
        object gameConfig = GetStaticProperty(typeof(GameConfigSetting), "Instance");
        WriteProperty(gameConfig, "RoundCount", m_previousRound);
        WriteStaticProperty(typeof(GameDataHolder), "GameExitSceneName", m_previousExitScene);
        m_savedGameSettings = false;
    }

    private void OnDestroy()
    {
        if (s_instance != this) return;
        if (m_running) Stop("Play終了。完了した試合だけ保存しました。");
        s_instance = null;
    }
}
