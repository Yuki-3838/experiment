using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Editor-only measurement code. This file is never part of the submitted AI.
[InitializeOnLoad]
public static class AriadneEvaluation
{
    private const string Key = "AriadneEvaluation";
    private const string Output = "AIExperiments/Ariadne/results";
    private static BattlePrototypeGameController controller;
    private static double wallStart;
    private static int errors;
    private static int actualSeed;
    private static int[] seedTable, originalSeeds;
    private static float nextSnapshot;
    private static readonly List<string> errorMessages = new List<string>();

    [Serializable] private sealed class Team
    {
        public string name;
        public int slot, rank, value, chests;
    }
    [Serializable] private sealed class Record
    {
        public string version = "day1", utc, unity, mode, reason;
        public int seed, actualSeed, slot, errors;
        public float simulatedSeconds;
        public double wallSeconds;
        public Team[] teams;
        public string[] errorMessages;
    }
    [Serializable] private sealed class Progress
    {
        public int index, seed, slot;
        public string mode;
        public float remaining;
        public string[] members;
    }

    static AriadneEvaluation()
    {
        EditorApplication.playModeStateChanged += OnPlayState;
        EditorApplication.update += Tick;
    }

    [MenuItem("Ariadne/Run day 1 comparison (8 matches)")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode first.");
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty)
            throw new InvalidOperationException("Save the current scene before evaluation.");
        Directory.CreateDirectory(Output);
        SessionState.SetBool(Key, true);
        SessionState.SetInt(Key + "Index", 0);
        EditorSceneManager.OpenScene("Assets/Dungeon/Scenes/MazeExplore.unity");
        EditorApplication.isPlaying = true;
    }

    private static void OnPlayState(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Key, false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            try { StartMatch(); }
            catch (Exception e) { Debug.LogException(e); Abort(e.ToString()); }
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            RestoreSeeds();
            int index = SessionState.GetInt(Key + "Index", 0) + 1;
            SessionState.SetInt(Key + "Index", index);
            if (index >= 8)
            {
                SessionState.SetBool(Key, false);
                File.WriteAllText(Output + "/complete.txt", DateTime.UtcNow.ToString("o"));
                Debug.Log("[AriadneEvaluation] COMPLETE: 8 matches");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            else EditorApplication.delayCall += () => { EditorApplication.isPlaying = true; };
        }
    }

    private static void StartMatch()
    {
        int index = SessionState.GetInt(Key + "Index", 0);
        foreach (BattlePrototypeSceneFlowController flow in UnityEngine.Object.FindObjectsByType<BattlePrototypeSceneFlowController>(FindObjectsSortMode.None))
        {
            flow.StopAllCoroutines();
            flow.enabled = false;
        }
        controller = UnityEngine.Object.FindFirstObjectByType<BattlePrototypeGameController>();
        if (controller == null) throw new InvalidOperationException("Battle controller missing.");
        BattlePartyRegistry registry = ScriptableObject.CreateInstance<BattlePartyRegistry>();
        var registryData = new SerializedObject(registry);
        SerializedProperty entries = registryData.FindProperty("m_partyPrefabs");
        entries.arraySize = 4;
        string[] opponents = { "Aggressive/SampleParty02", "Strategic/SampleParty03", "Convoy/SampleParty04" };
        int slot = Slot(index), opponent = 0;
        for (int i = 0; i < 4; i++)
        {
            string path = i == slot
                ? (index < 4 ? "Assets/Participants/Party1111111/Party1111111Party.prefab" : "Assets/Participants/Sample/TreasureReturn/SampleParty01.prefab")
                : "Assets/Participants/Sample/" + opponents[opponent++] + ".prefab";
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            ComPartyBase party = prefab != null ? prefab.GetComponent<ComPartyBase>() : null;
            if (party == null) throw new InvalidOperationException("Invalid party prefab: " + path);
            entries.GetArrayElementAtIndex(i).objectReferenceValue = party;
        }
        registryData.ApplyModifiedPropertiesWithoutUndo();
        var data = new SerializedObject(controller);
        data.FindProperty("m_battlePartyRegistry").objectReferenceValue = registry;
        SerializedProperty settings = data.FindProperty("m_mazeGenerationSettings");
        settings.FindPropertyRelative("m_useRandomSeed").boolValue = false;
        settings.FindPropertyRelative("m_seed").intValue = Seed(index);
        data.ApplyModifiedPropertiesWithoutUndo();
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Time.timeScale = 1f;
        Time.captureDeltaTime = 0.02f;
        Time.fixedDeltaTime = 0.02f;
        AudioListener.volume = 0;
        foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled = false;
        errors = 0; errorMessages.Clear();
        actualSeed = 0;
        Application.logMessageReceived += OnLog;
        // This distribution ignores MazeGenerationSettings.m_seed and chooses
        // nondeterministically from a private preapproved table. Restrict that
        // table in memory for this measurement only; restore it afterwards.
        seedTable = (int[])typeof(MazeGenerator).GetField("s_preapprovedMazeSeeds", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        originalSeeds = (int[])seedTable.Clone();
        if (Array.IndexOf(originalSeeds, Seed(index)) < 0) throw new InvalidOperationException("Use a preapproved maze seed.");
        for (int i = 0; i < seedTable.Length; i++) seedTable[i] = Seed(index);
        UnityEngine.Random.InitState(Seed(index));
        controller.PrepareBattle();
        if (actualSeed != Seed(index)) throw new InvalidOperationException("Maze seed was not applied.");
        if (controller.SpawnedPartyCount != 4) throw new InvalidOperationException("Expected four teams.");
        foreach (CharacterAnimationController animation in UnityEngine.Object.FindObjectsByType<CharacterAnimationController>(FindObjectsSortMode.None))
            if (animation.GetComponent<Animator>().runtimeAnimatorController == null)
                throw new InvalidOperationException("Restore the distribution's Mixamo animation assets before measuring.");
        controller.BattleEnded += Finished;
        wallStart = EditorApplication.timeSinceStartup;
        nextSnapshot = 0;
        controller.StartBattle();
        Debug.Log("[AriadneEvaluation] START " + index + " seed=" + Seed(index) + " slot=" + slot);
    }

    private static int Seed(int index) => (index % 4) < 2 ? 1041505397 : 1013712272;
    private static int Slot(int index) => index % 2 == 0 ? 0 : 2;
    private static void OnLog(string message, string stack, LogType type)
    {
        int marker = message.IndexOf("randomKey=", StringComparison.Ordinal);
        if (message.StartsWith("MazeGenerator:", StringComparison.Ordinal) && marker >= 0)
        {
            string seed = message.Substring(marker + 10).Split(' ')[0];
            int.TryParse(seed, out actualSeed);
        }
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        errors++;
        if (errorMessages.Count < 10) errorMessages.Add(message);
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(Key, false) || !EditorApplication.isPlaying || controller == null) return;
        if (EditorApplication.timeSinceStartup - wallStart > 180) { Abort("Wall-time timeout"); return; }
        if (Time.time < nextSnapshot) return;
        nextSnapshot = Time.time + 20;
        int index = SessionState.GetInt(Key + "Index", 0);
        var positions = new List<string>();
        MethodInfo read = typeof(ComPartyBase).GetMethod("SXG_TryGetMemberInfo", BindingFlags.NonPublic | BindingFlags.Instance);
        ComPartyBase focus = controller.SpawnedParties[Slot(index)];
        for (int i = 0; i < 4; i++)
        {
            object[] args = { i, null };
            read.Invoke(focus, args);
            var m = args[1] as PartyMemberInfo;
            positions.Add(m == null ? "missing" : m.CurrentCell + " carrying=" + m.HasTreasure + " locked=" + m.IsActionLocked);
        }
        File.WriteAllText(Output + "/progress.json", JsonUtility.ToJson(new Progress
        { index = index, seed = Seed(index), slot = Slot(index), mode = index < 4 ? "Ariadne" : "TreasureReturn", remaining = controller.RemainingBattleSeconds, members = positions.ToArray() }, true));
    }

    private static void Finished(BattleResultData result)
    {
        int index = SessionState.GetInt(Key + "Index", 0);
        var teams = new List<Team>();
        foreach (TeamBattleResult t in result.TeamResults)
            teams.Add(new Team { slot = t.TeamIndex, rank = t.Rank, value = t.TotalValue, chests = t.Treasures.Count,
                name = controller.SpawnedParties[t.TeamIndex].GetType().Name });
        var record = new Record
        {
            utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion,
            mode = index < 4 ? "Ariadne" : "TreasureReturn", seed = Seed(index), actualSeed = actualSeed, slot = Slot(index),
            reason = controller.LastBattleEndReason.ToString(), errors = errors, errorMessages = errorMessages.ToArray(),
            simulatedSeconds = 120f - controller.RemainingBattleSeconds,
            wallSeconds = EditorApplication.timeSinceStartup - wallStart, teams = teams.ToArray()
        };
        File.WriteAllText(Output + "/day1-" + index.ToString("00") + ".json", JsonUtility.ToJson(record, true));
        Debug.Log("[AriadneEvaluation] FINISH " + index + " errors=" + errors);
        Application.logMessageReceived -= OnLog;
        RestoreSeeds();
        Time.captureDeltaTime = 0;
        EditorApplication.isPlaying = false;
    }

    private static void Abort(string reason)
    {
        SessionState.SetBool(Key, false);
        Directory.CreateDirectory(Output);
        File.WriteAllText(Output + "/failure.txt", reason);
        Application.logMessageReceived -= OnLog;
        RestoreSeeds();
        Time.captureDeltaTime = 0;
        EditorApplication.isPlaying = false;
        if (Application.isBatchMode) EditorApplication.Exit(1);
    }

    private static void RestoreSeeds()
    {
        if (seedTable != null && originalSeeds != null) Array.Copy(originalSeeds, seedTable, seedTable.Length);
        seedTable = null; originalSeeds = null;
    }
}
