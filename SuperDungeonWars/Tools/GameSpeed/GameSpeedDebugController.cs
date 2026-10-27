using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using System.Text.RegularExpressions;
using UnityEditor;
#endif

/// <summary>
/// Game speed and SuperDungeonWars result timing controls in one portable file.
/// No references to game assemblies or Inspector object assignments are needed.
/// </summary>
[DefaultExecutionOrder(-10000)]
[DisallowMultipleComponent]
public sealed class GameSpeedDebugController : MonoBehaviour
{
    public enum ResultClock
    {
        Automatic,
        Unscaled,
        Scaled
    }

    [SerializeField]
    private bool m_followGameSpeed = true;

    [SerializeField, Min(0.01f)]
    private float m_resultSpeed = 1.0f;

    [SerializeField]
    private bool m_skipResultWaits;

    [SerializeField]
    private ResultClock m_resultClock = ResultClock.Automatic;

    private sealed class TimingBackup
    {
        internal MonoBehaviour Target;
        internal FieldInfo Field;
        internal float Original;
        internal float Applied;
        internal bool UsesUnscaledTime;
    }

    private readonly HashSet<MonoBehaviour> m_knownTargets =
        new HashSet<MonoBehaviour>();
    private readonly List<MonoBehaviour> m_resultRoots =
        new List<MonoBehaviour>();
    private readonly List<MonoBehaviour> m_children =
        new List<MonoBehaviour>();
    private readonly List<TimingBackup> m_timings =
        new List<TimingBackup>();
    private readonly Dictionary<Type, bool> m_clockCache =
        new Dictionary<Type, bool>();

    private float m_initialTimeScale = 1.0f;
    private float m_lastPositiveTimeScale = 1.0f;
    private float m_nextDiscoveryTime;
    private bool m_isControlling;

    public float ResultSpeed { get { return m_resultSpeed; } }
    public int ResultTargetCount { get { return m_knownTargets.Count; } }
    public int ResultTimingCount { get { return m_timings.Count; } }

    private void OnEnable()
    {
        if (!Application.isPlaying) return;
        m_initialTimeScale = Time.timeScale;
        m_isControlling = true;
        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshResultTimings();
    }

    private void LateUpdate()
    {
        if (!m_isControlling) return;
        SynchronizeSpeeds();

        // Find new result roots even when initially inactive or in another scene.
        if (Time.unscaledTime >= m_nextDiscoveryTime)
            DiscoverSceneTargets();

        // Lanes are instantiated by Show(). Pick them up without a scene-wide
        // search each frame, including when the game is paused.
        for (int rootIndex = 0; rootIndex < m_resultRoots.Count; rootIndex++)
        {
            MonoBehaviour root = m_resultRoots[rootIndex];
            if (root == null) continue;
            m_children.Clear();
            root.GetComponentsInChildren<MonoBehaviour>(true, m_children);
            foreach (MonoBehaviour child in m_children)
                RegisterTarget(child);
        }

        ApplyTimings();
    }

    public void SetGameSpeed(float speedMultiplier)
    {
        if (!Application.isPlaying || !isActiveAndEnabled
            || !IsFinite(speedMultiplier)) return;

        Time.timeScale = Mathf.Clamp(speedMultiplier, 0.0f, 100.0f);
        RefreshResultTimings();
    }

    public void SetResultSpeed(float speedMultiplier)
    {
        if (!Application.isPlaying || !isActiveAndEnabled
            || !IsFinite(speedMultiplier) || speedMultiplier <= 0.0f) return;

        m_followGameSpeed = false;
        m_resultSpeed = Mathf.Clamp(speedMultiplier, 0.01f, 100.0f);
        RefreshResultTimings();
    }

    public void ResetGameSpeed()
    {
        if (!Application.isPlaying || !isActiveAndEnabled) return;
        Time.timeScale = m_initialTimeScale;
        m_resultSpeed = 1.0f;
        m_skipResultWaits = false;
        RefreshResultTimings();
    }

    public void RefreshResultTimings()
    {
        if (!Application.isPlaying || !m_isControlling) return;
        SynchronizeSpeeds();
        DiscoverSceneTargets();
        ApplyTimings();
    }

    private void SynchronizeSpeeds()
    {
        if (Time.timeScale > 0.0f)
            m_lastPositiveTimeScale = Time.timeScale;
        if (m_followGameSpeed)
            m_resultSpeed = m_lastPositiveTimeScale;
        if (!IsFinite(m_resultSpeed)) m_resultSpeed = 1.0f;
        m_resultSpeed = Mathf.Clamp(m_resultSpeed, 0.01f, 100.0f);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RefreshResultTimings();
    }

    private void DiscoverSceneTargets()
    {
        m_nextDiscoveryTime = Time.unscaledTime + 0.5f;
        m_knownTargets.RemoveWhere(target => target == null);
        m_resultRoots.RemoveAll(target => target == null);
        m_timings.RemoveAll(timing => timing.Target == null);

#if UNITY_2022_2_OR_NEWER
        MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        MonoBehaviour[] behaviours =
            UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
#endif
        foreach (MonoBehaviour behaviour in behaviours)
            RegisterTarget(behaviour);
    }

    private void RegisterTarget(MonoBehaviour target)
    {
        if (target == null || m_knownTargets.Contains(target)) return;
        Type type = target.GetType();
        bool isRoot = HasTypeName(type, "BattleResultPresentation");
        if (!isRoot && !HasTypeName(type, "BattleResultTeamLane")) return;
        m_knownTargets.Add(target);
        if (isRoot) m_resultRoots.Add(target);

        bool unscaled;
        if (!m_clockCache.TryGetValue(type, out unscaled))
        {
            unscaled = DetectUnscaledTime(target);
            m_clockCache.Add(type, unscaled);
        }

        for (Type current = type;
             current != null && current != typeof(MonoBehaviour);
             current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!IsTimingField(field)) continue;
                float original = (float)field.GetValue(target);
                if (!IsFinite(original) || original <= 0.0f) continue;
                m_timings.Add(new TimingBackup
                {
                    Target = target,
                    Field = field,
                    Original = original,
                    Applied = original,
                    UsesUnscaledTime = unscaled
                });
            }
        }
    }

    private static bool HasTypeName(Type type, string name)
    {
        for (Type current = type;
             current != null && current != typeof(MonoBehaviour);
             current = current.BaseType)
            if (current.Name == name) return true;
        return false;
    }

    private static bool IsTimingField(FieldInfo field)
    {
        if (field.FieldType != typeof(float) || field.IsInitOnly
            || field.IsDefined(typeof(NonSerializedAttribute), false)) return false;
        if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), false))
            return false;
        return field.Name.EndsWith("Duration", StringComparison.OrdinalIgnoreCase)
            || field.Name.EndsWith("Delay", StringComparison.OrdinalIgnoreCase)
            || field.Name.EndsWith("Interval", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DetectUnscaledTime(MonoBehaviour target)
    {
#if UNITY_EDITOR
        // Editor source is available across assemblies. Ignore commented-out
        // realtime waits in locally patched, scaled-time versions.
        MonoScript script = MonoScript.FromMonoBehaviour(target);
        if (script != null)
        {
            string code = Regex.Replace(
                script.text, @"/\*[\s\S]*?\*/|//[^\r\n]*", "");
            if (Regex.IsMatch(code,
                @"\bWaitForSecondsRealtime\b|\bTime\s*\.\s*unscaled(?:DeltaTime|Time)\b"))
                return true;
            if (Regex.IsMatch(code,
                @"\bWaitForSeconds\b|\bTime\s*\.\s*deltaTime\b"))
                return false;
        }
#endif
        // The original game's result code uses unscaled time. For builds or
        // mixed-clock custom scripts the Inspector also offers explicit modes.
        return true;
    }

    private float GetDuration(TimingBackup timing)
    {
        if (m_skipResultWaits) return 0.0f;
        bool unscaled = m_resultClock == ResultClock.Unscaled
            || (m_resultClock == ResultClock.Automatic && timing.UsesUnscaledTime);
        double clockRate = unscaled ? 1.0 : m_lastPositiveTimeScale;
        return (float)Math.Min(
            float.MaxValue, timing.Original * clockRate / m_resultSpeed);
    }

    private void ApplyTimings()
    {
        foreach (TimingBackup timing in m_timings)
        {
            if (timing.Target == null) continue;
            float next = GetDuration(timing);
            if (next == timing.Applied) continue;
            float current = (float)timing.Field.GetValue(timing.Target);
            if (current != timing.Applied)
            {
                // A runtime Inspector edit becomes the new baseline.
                if (!IsFinite(current) || current < 0.0f) continue;
                timing.Original = current;
                next = GetDuration(timing);
            }
            timing.Field.SetValue(timing.Target, next);
            timing.Applied = next;
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (!m_isControlling) return;
        foreach (TimingBackup timing in m_timings)
        {
            if (timing.Target != null
                && (float)timing.Field.GetValue(timing.Target) == timing.Applied)
                timing.Field.SetValue(timing.Target, timing.Original);
        }
        Time.timeScale = m_initialTimeScale;
        m_timings.Clear();
        m_knownTargets.Clear();
        m_resultRoots.Clear();
        m_clockCache.Clear();
        m_isControlling = false;
    }
}

#if UNITY_EDITOR
// Kept in this file so copying a single .cs also brings the Inspector buttons.
// UnityEditor references are excluded from player builds.
[CustomEditor(typeof(GameSpeedDebugController))]
public sealed class GameSpeedDebugControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        GameSpeedDebugController controller = (GameSpeedDebugController)target;
        bool canControl = EditorApplication.isPlaying && controller.isActiveAndEnabled;
        EditorGUILayout.LabelField("Game Speed", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Current speed", Time.timeScale.ToString("0.##") + "x");
        using (new EditorGUI.DisabledScope(!canControl))
        {
            EditorGUILayout.BeginHorizontal();
            DrawGameButton(controller, "Pause", 0.0f);
            DrawGameButton(controller, "0.25x", 0.25f);
            DrawGameButton(controller, "0.5x", 0.5f);
            DrawGameButton(controller, "1x", 1.0f);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            DrawGameButton(controller, "2x", 2.0f);
            DrawGameButton(controller, "5x", 5.0f);
            DrawGameButton(controller, "10x", 10.0f);
            if (GUILayout.Button("Reset")) controller.ResetGameSpeed();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Result / Chests", EditorStyles.boldLabel);
        serializedObject.Update();
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("m_followGameSpeed"),
            new GUIContent("Follow Game Speed"));
        if (!serializedObject.FindProperty("m_followGameSpeed").boolValue)
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("m_resultSpeed"), new GUIContent("Result Speed"));
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("m_skipResultWaits"), new GUIContent("Skip Result Waits"));
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("m_resultClock"), new GUIContent("Result Clock"));
        if (serializedObject.ApplyModifiedProperties()) controller.RefreshResultTimings();

        using (new EditorGUI.DisabledScope(!canControl))
        {
            EditorGUILayout.BeginHorizontal();
            foreach (float speed in new[] { 0.25f, 0.5f, 1.0f, 2.0f, 5.0f, 10.0f })
                if (GUILayout.Button(speed.ToString("0.##") + "x"))
                    controller.SetResultSpeed(speed);
            EditorGUILayout.EndHorizontal();
        }

        if (!EditorApplication.isPlaying)
            EditorGUILayout.HelpBox("Play中に速度を操作できます。", MessageType.Info);
        else
        {
            EditorGUILayout.LabelField("Result speed", controller.ResultSpeed.ToString("0.##") + "x");
            EditorGUILayout.LabelField("Detected results / timing fields",
                controller.ResultTargetCount + " / " + controller.ResultTimingCount);
            if (controller.ResultTargetCount == 0)
                EditorGUILayout.HelpBox(
                    "結果画面のコンポーネントをまだ検出していません。生成後に自動で探します。",
                    MessageType.Info);
            if (Time.timeScale == 0.0f)
                EditorGUILayout.HelpBox(
                    "Pauseはゲーム時間を停止します。実時間で動く結果演出は進みます。",
                    MessageType.Info);
        }
    }

    public override bool RequiresConstantRepaint()
    {
        return EditorApplication.isPlaying;
    }

    private static void DrawGameButton(
        GameSpeedDebugController controller, string label, float speed)
    {
        if (GUILayout.Button(label)) controller.SetGameSpeed(speed);
    }
}
#endif
