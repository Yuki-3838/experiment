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
/// Game speed, result timing and spectator camera controls in one portable file.
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

    public enum CameraTeam
    {
        [InspectorName("赤")] Red,
        [InspectorName("青")] Blue,
        [InspectorName("黄")] Yellow,
        [InspectorName("緑")] Green
    }

    [SerializeField]
    private CameraTeam m_cameraTeam;

    [SerializeField]
    private bool m_followGameSpeed = true;

    [SerializeField, Min(0.01f)]
    private float m_resultSpeed = 1.0f;

    [SerializeField]
    private bool m_skipResultWaits;

    [SerializeField]
    private ResultClock m_resultClock = ResultClock.Automatic;

    private sealed class DebugSessionState
    {
        internal float InitialSpeed, GameSpeed, LastPositiveSpeed, ResultSpeed;
        internal bool Enabled, FollowGameSpeed, SkipWaits, CameraFocusRequested;
        internal ResultClock Clock;
        internal CameraTeam SelectedTeam, FocusedTeam;
        internal int FocusedMember;
#if UNITY_EDITOR
        internal bool InspectorWasSelected;
#endif
    }

    private static GameSpeedDebugController s_sessionOwner;
    private static DebugSessionState s_session;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetDebugSession()
    {
        // A scene reload keeps the session; entering a new Play session clears it,
        // including when the Editor's domain reload option is disabled.
        s_sessionOwner = null;
        s_session = null;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateDebugControllerOnce()
    {
        // This callback runs once at startup, not on subsequent scene loads.
        // Keep debug objects out of gameplay scenes so loops create no copies.
        if (s_sessionOwner != null) return;
#if UNITY_2022_2_OR_NEWER
        GameSpeedDebugController[] existing =
            UnityEngine.Object.FindObjectsByType<GameSpeedDebugController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        GameSpeedDebugController[] existing =
            UnityEngine.Object.FindObjectsOfType<GameSpeedDebugController>(true);
#endif
        // Respect a manually attached controller, including a disabled one.
        if (existing.Length != 0) return;
        new GameObject(nameof(GameSpeedDebugController))
            .AddComponent<GameSpeedDebugController>();
    }
#endif

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
    private float m_currentGameSpeed = 1.0f;
    private float m_lastPositiveTimeScale = 1.0f;
    private float m_nextDiscoveryTime;
    private bool m_isControlling;
    private readonly CameraFocusBridge m_debugCamera = new CameraFocusBridge();
    private bool m_restoreSpeedAfterSceneStart;
    private bool m_cameraFocusRequested, m_waitingForCamera;
    private CameraTeam m_focusedCameraTeam;
    private int m_focusedCameraMember;

    public float ResultSpeed { get { return m_resultSpeed; } }
    public int ResultTargetCount { get { return m_knownTargets.Count; } }
    public int ResultTimingCount { get { return m_timings.Count; } }
    public CameraTeam SelectedCameraTeam
    {
        get { return m_cameraTeam; }
        set { m_cameraTeam = value; }
    }
    public int SelectedCameraTeamIndex { get { return m_debugCamera.TeamIndex(m_cameraTeam); } }
    public Color SelectedCameraTeamColor { get { return m_debugCamera.TeamColor(m_cameraTeam); } }
    public string CameraFocusStatus
    {
        get
        {
            return IsCameraFocusPending
                ? "Waiting: " + m_focusedCameraTeam + " / " + m_focusedCameraMember
                    + "（次のキャラクター生成待ち）"
                : m_debugCamera.Status;
        }
    }
    public bool IsManualCameraFocus { get { return m_debugCamera.IsManual; } }
    public bool IsCameraFocusPending { get { return m_cameraFocusRequested && m_waitingForCamera; } }

    public bool CanFocusCameraMember(int memberNumber)
    {
        return Application.isPlaying && isActiveAndEnabled
            && m_debugCamera.CanFocus(m_cameraTeam, memberNumber);
    }

    public bool FocusCameraMember(int memberNumber)
    {
        if (!Application.isPlaying || !isActiveAndEnabled) return false;
        RefreshResultTimings();
        if (!m_debugCamera.Focus(m_cameraTeam, memberNumber)) return false;
        m_cameraFocusRequested = true;
        m_waitingForCamera = false;
        m_focusedCameraTeam = m_cameraTeam;
        m_focusedCameraMember = memberNumber;
        SaveSession();
        return true;
    }

    public void ReturnAutomaticCamera()
    {
        m_cameraFocusRequested = m_waitingForCamera = false;
        m_debugCamera.Release();
        SaveSession();
    }

    private void OnEnable()
    {
        if (!Application.isPlaying) return;
        if (s_sessionOwner != null && s_sessionOwner != this)
        {
            // A reloaded scene includes another copy of the component. Never
            // allow that copy to replace the live controls or reset the clock.
            enabled = false;
#if UNITY_EDITOR
            if (Selection.activeGameObject == gameObject || Selection.activeObject == this)
                Selection.activeGameObject = s_sessionOwner.gameObject;
#endif
            if (IsDedicatedDebugObject()) Destroy(gameObject);
            else Destroy(this);
            return;
        }

        bool sameOwner = s_sessionOwner == this;
        bool restoreDisabled = !sameOwner && s_session != null && !s_session.Enabled;
        s_sessionOwner = this;
        if (!sameOwner)
        {
            if (s_session != null) RestoreSession();
            else
            {
                m_initialTimeScale = m_currentGameSpeed = Time.timeScale;
                m_lastPositiveTimeScale = Time.timeScale > 0.0f ? Time.timeScale : 1.0f;
            }
        }
        // Keeping an arbitrary manager or Canvas alive would preserve game
        // state too. Only a dedicated, empty debug root is made persistent.
        // Shared hosts use the session snapshot when their scene recreates them.
        if (IsDedicatedDebugObject()) DontDestroyOnLoad(gameObject);
        if (restoreDisabled)
        {
            RestoreInspectorSelection();
            enabled = false;
            return;
        }
        Time.timeScale = m_currentGameSpeed;
        m_restoreSpeedAfterSceneStart = true;
        m_isControlling = true;
        m_waitingForCamera = m_cameraFocusRequested;
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        RestoreInspectorSelection();
        RefreshResultTimings();
    }

    private void LateUpdate()
    {
        if (!m_isControlling) return;
        if (m_restoreSpeedAfterSceneStart)
        {
            // Start() in the new scene can reset timeScale after sceneLoaded.
            Time.timeScale = m_currentGameSpeed;
            m_restoreSpeedAfterSceneStart = false;
        }
        else m_currentGameSpeed = Time.timeScale;
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
        // This script runs before the spectator camera's LateUpdate. Reapply the
        // manual target after the game's automatic selection in Update().
        bool wasManual = m_debugCamera.IsManual;
        m_debugCamera.Tick();
        if (IsCameraFocusPending)
        {
            if (m_debugCamera.CanFocus(m_focusedCameraTeam, m_focusedCameraMember)
                && m_debugCamera.Focus(m_focusedCameraTeam, m_focusedCameraMember))
                m_waitingForCamera = false;
        }
        else if (wasManual && !m_debugCamera.IsManual)
        {
            // A target removed inside the same match still returns to Auto.
            m_cameraFocusRequested = false;
        }
        SaveSession();
    }

    public void SetGameSpeed(float speedMultiplier)
    {
        if (!Application.isPlaying || !isActiveAndEnabled
            || !IsFinite(speedMultiplier)) return;

        Time.timeScale = Mathf.Clamp(speedMultiplier, 0.0f, 100.0f);
        m_currentGameSpeed = Time.timeScale;
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
        m_currentGameSpeed = Time.timeScale;
        m_lastPositiveTimeScale = Time.timeScale > 0.0f ? Time.timeScale : 1.0f;
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
        SaveSession();
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
        Time.timeScale = m_currentGameSpeed;
        m_restoreSpeedAfterSceneStart = true;
        RestoreInspectorSelection();
        RefreshResultTimings();
        if (m_cameraFocusRequested && !m_debugCamera.IsManual)
            m_waitingForCamera = true;
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (m_debugCamera.ReferencesScene(scene.handle))
        {
            m_waitingForCamera = m_cameraFocusRequested;
            m_debugCamera.Shutdown();
        }
        SaveSession(true);
    }

    private bool IsDedicatedDebugObject()
    {
        return transform.parent == null && transform.childCount == 0
            && GetComponents<Component>().Length == 2;
    }

    private void SaveSession(bool preserveSelection = false)
    {
        if (!m_isControlling || s_sessionOwner != this) return;
        if (s_session == null) s_session = new DebugSessionState();
        s_session.InitialSpeed = m_initialTimeScale;
        s_session.GameSpeed = m_currentGameSpeed;
        s_session.LastPositiveSpeed = m_lastPositiveTimeScale;
        s_session.ResultSpeed = m_resultSpeed;
        s_session.Enabled = enabled;
        s_session.FollowGameSpeed = m_followGameSpeed;
        s_session.SkipWaits = m_skipResultWaits;
        s_session.Clock = m_resultClock;
        s_session.SelectedTeam = m_cameraTeam;
        s_session.CameraFocusRequested = m_cameraFocusRequested;
        s_session.FocusedTeam = m_focusedCameraTeam;
        s_session.FocusedMember = m_focusedCameraMember;
#if UNITY_EDITOR
        if (!preserveSelection || Selection.activeObject != null)
            s_session.InspectorWasSelected = Selection.activeGameObject == gameObject;
#endif
    }

    private void RestoreSession()
    {
        m_initialTimeScale = s_session.InitialSpeed;
        m_currentGameSpeed = s_session.GameSpeed;
        m_lastPositiveTimeScale = s_session.LastPositiveSpeed;
        m_resultSpeed = s_session.ResultSpeed;
        m_followGameSpeed = s_session.FollowGameSpeed;
        m_skipResultWaits = s_session.SkipWaits;
        m_resultClock = s_session.Clock;
        m_cameraTeam = s_session.SelectedTeam;
        m_cameraFocusRequested = s_session.CameraFocusRequested;
        m_focusedCameraTeam = s_session.FocusedTeam;
        m_focusedCameraMember = s_session.FocusedMember;
    }

    private void RestoreInspectorSelection()
    {
#if UNITY_EDITOR
        if (s_session != null && s_session.InspectorWasSelected)
            Selection.activeGameObject = gameObject;
#endif
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
        {
            RegisterTarget(behaviour);
            m_debugCamera.Discover(behaviour);
        }
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
        SaveSession(true);
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        m_debugCamera.Shutdown();
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

    private void OnDestroy()
    {
        if (s_sessionOwner == this) s_sessionOwner = null;
    }

    private sealed class CameraFocusBridge
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly int[] m_teamIndices = { 0, 3, 2, 1 };
        private readonly Color[] m_teamColors =
        {
            new Color(1.0f, 0.34f, 0.05f), new Color(0.05f, 0.78f, 0.3f),
            new Color(1.0f, 0.82f, 0.08f), new Color(0.05f, 0.48f, 1.0f)
        };
        private readonly Dictionary<Type, MethodInfo> m_memberMethods =
            new Dictionary<Type, MethodInfo>();
        private MonoBehaviour m_game, m_camera, m_indicator, m_target;
        private MonoBehaviour m_previousCharacter, m_previousTreasure;
        private PropertyInfo m_parties;
        private FieldInfo m_focusParty;
        private MethodInfo m_setCharacter, m_snap, m_setIndicator, m_resumeAutomatic;
        private object[] m_focusArguments, m_indicatorArguments;
        private int m_targetTeam, m_previousTeam;
        private int m_gameSceneHandle = -1, m_focusSceneHandle = -1;

        public bool IsManual { get; private set; }
        public string Status { get; private set; } = "Auto";
        public int TeamIndex(CameraTeam team)
        {
            return m_teamIndices[Mathf.Clamp((int)team, 0, 3)];
        }
        public Color TeamColor(CameraTeam team) { return m_teamColors[TeamIndex(team)]; }
        public bool ReferencesScene(int handle)
        {
            return m_gameSceneHandle == handle || m_focusSceneHandle == handle;
        }

        public void Discover(MonoBehaviour behaviour)
        {
            if (behaviour == null
                || !HasTypeName(behaviour.GetType(), "BattlePrototypeGameController")) return;
            if (m_game != null && m_camera != null
                && (m_game == behaviour || m_game.isActiveAndEnabled)) return;
            Release();
            m_game = behaviour;
            m_gameSceneHandle = behaviour.gameObject.scene.handle;
            m_camera = ReadField(m_game, "m_battleSpectatorCameraController") as MonoBehaviour;
            m_indicator = ReadField(m_game, "m_battleSpectatorFocusIndicatorView") as MonoBehaviour;
            m_parties = m_game.GetType().GetProperty("SpawnedParties", InstanceFlags);
            m_focusParty = FindField(m_game, "m_spectatorFocusedPartyIndex");
            m_setCharacter = FindMethod(m_camera, "SetFocusCharacter", 1);
            m_snap = FindMethod(m_camera, "SnapToCurrentFocus", 0);
            m_setIndicator = FindMethod(m_indicator, "SetFocusCharacter", 2);
            m_resumeAutomatic =
                FindMethod(m_game, "RefreshSpectatorFocusByDeveloperSetting", 0)
                ?? FindMethod(m_game, "InitializeSpectatorFocus", 0);
            ReadTeamColors();
        }

        public bool CanFocus(CameraTeam team, int memberNumber)
        {
            MonoBehaviour member;
            return TryGetMember(team, memberNumber, out member);
        }

        private bool TryGetMember(CameraTeam team, int memberNumber, out MonoBehaviour member)
        {
            member = null;
            if (memberNumber < 1 || memberNumber > 4 || m_game == null
                || m_camera == null || !m_camera.isActiveAndEnabled
                || m_parties == null || m_setCharacter == null) return false;
            var parties = m_parties.GetValue(m_game, null) as System.Collections.IList;
            int teamIndex = TeamIndex(team);
            if (parties == null || teamIndex >= parties.Count) return false;
            MonoBehaviour party = parties[teamIndex] as MonoBehaviour;
            if (party == null) return false;
            MethodInfo getMember;
            if (!m_memberMethods.TryGetValue(party.GetType(), out getMember))
            {
                getMember = FindMethod(party, "TryGetMember", 2);
                m_memberMethods.Add(party.GetType(), getMember);
            }
            if (getMember == null) return false;
            object[] arguments = { memberNumber - 1, null };
            object found = getMember.Invoke(party, arguments);
            if (!(found is bool) || !(bool)found) return false;
            member = arguments[1] as MonoBehaviour;
            return member != null && member.gameObject.activeInHierarchy
                && m_setCharacter.GetParameters()[0].ParameterType.IsInstanceOfType(member);
        }

        public bool Focus(CameraTeam team, int memberNumber)
        {
            MonoBehaviour member;
            if (!TryGetMember(team, memberNumber, out member))
            {
                Status = "対象が未生成・非表示、または観戦カメラが未検出です。";
                return false;
            }
            if (!IsManual)
            {
                m_previousCharacter = ReadField(m_camera, "m_focusCharacter") as MonoBehaviour;
                m_previousTreasure = ReadField(m_camera, "m_focusTreasureChest") as MonoBehaviour;
                m_previousTeam = m_focusParty == null ? -1 : (int)m_focusParty.GetValue(m_game);
            }
            m_target = member;
            m_focusSceneHandle = member.gameObject.scene.handle;
            m_targetTeam = TeamIndex(team);
            m_focusArguments = new object[] { member };
            m_indicatorArguments = new object[] { member, m_targetTeam };
            IsManual = true;
            Status = new[] { "赤", "青", "黄", "緑" }[Mathf.Clamp((int)team, 0, 3)]
                + " / " + memberNumber + " : " + member.name;
            Tick();
            if (m_snap != null) m_snap.Invoke(m_camera, null);
            return true;
        }

        public void Tick()
        {
            if (!IsManual) return;
            if (m_game == null || m_camera == null || !m_camera.isActiveAndEnabled
                || m_target == null || !m_target.gameObject.activeInHierarchy)
            {
                Release();
                return;
            }
            m_setCharacter.Invoke(m_camera, m_focusArguments);
            if (m_focusParty != null) m_focusParty.SetValue(m_game, m_targetTeam);
            if (m_indicator != null && m_setIndicator != null)
                m_setIndicator.Invoke(m_indicator, m_indicatorArguments);
        }

        public void Release()
        {
            if (!IsManual) return;
            IsManual = false;
            if (Application.isPlaying && m_game != null
                && m_game.isActiveAndEnabled && m_resumeAutomatic != null)
            {
                m_resumeAutomatic.Invoke(m_game, null);
            }
            else if (Application.isPlaying && m_camera != null)
            {
                if (m_game != null && m_focusParty != null)
                    m_focusParty.SetValue(m_game, m_previousTeam);
                if (m_previousCharacter != null && m_setCharacter != null)
                {
                    m_setCharacter.Invoke(m_camera, new object[] { m_previousCharacter });
                    if (m_indicator != null && m_setIndicator != null)
                        m_setIndicator.Invoke(m_indicator,
                            new object[] { m_previousCharacter, m_previousTeam });
                }
                else
                {
                    MethodInfo restoreTreasure = FindMethod(m_camera, "SetFocusTreasureChest", 1);
                    if (m_previousTreasure != null && restoreTreasure != null)
                        restoreTreasure.Invoke(m_camera, new object[] { m_previousTreasure });
                    else
                        InvokeIfPresent(m_camera, "ClearFocus");
                    InvokeIfPresent(m_indicator, "ClearFocus");
                }
            }
            m_target = m_previousCharacter = m_previousTreasure = null;
            m_focusArguments = m_indicatorArguments = null;
            Status = "Auto";
        }

        public void Shutdown()
        {
            Release();
            m_game = m_camera = m_indicator = null;
            m_gameSceneHandle = m_focusSceneHandle = -1;
            m_memberMethods.Clear();
        }

        private void ReadTeamColors()
        {
            // Obtain the actual palette without a reference to the game assembly.
            string prefix = string.IsNullOrEmpty(m_game.GetType().Namespace)
                ? "" : m_game.GetType().Namespace + ".";
            Type colors = m_game.GetType().Assembly.GetType(prefix + "CharacterTeamColorConstants");
            MethodInfo getColor = colors == null ? null : colors.GetMethod(
                "GetTeamBaseColor", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(int) }, null);
            if (getColor == null || getColor.ReturnType != typeof(Color)) return;
            float[] hues = new float[4];
            for (int index = 0; index < 4; index++)
            {
                m_teamColors[index] = (Color)getColor.Invoke(null, new object[] { index });
                float saturation, value;
                Color.RGBToHSV(m_teamColors[index], out hues[index], out saturation, out value);
                if (saturation < 0.1f) return;
            }
            // Evaluate the 24 unique assignments so two dropdown colors cannot
            // resolve to the same team. Orange is the red slot in this palette.
            float best = float.PositiveInfinity;
            for (int red = 0; red < 4; red++)
            for (int blue = 0; blue < 4; blue++)
            for (int yellow = 0; yellow < 4; yellow++)
            for (int green = 0; green < 4; green++)
            {
                if (red == blue || red == yellow || red == green
                    || blue == yellow || blue == green || yellow == green) continue;
                float score = HueDistance(hues[red], 0.0f)
                    + HueDistance(hues[blue], 2.0f / 3.0f)
                    + HueDistance(hues[yellow], 1.0f / 6.0f)
                    + HueDistance(hues[green], 1.0f / 3.0f);
                if (score >= best) continue;
                best = score;
                m_teamIndices[0] = red; m_teamIndices[1] = blue;
                m_teamIndices[2] = yellow; m_teamIndices[3] = green;
            }
        }

        private static float HueDistance(float left, float right)
        {
            float difference = Mathf.Abs(left - right);
            return Mathf.Min(difference, 1.0f - difference);
        }

        private static FieldInfo FindField(object target, string name)
        {
            for (Type type = target == null ? null : target.GetType();
                 type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        private static object ReadField(object target, string name)
        {
            FieldInfo field = FindField(target, name);
            return field == null ? null : field.GetValue(target);
        }

        private static MethodInfo FindMethod(object target, string name, int argumentCount)
        {
            for (Type type = target == null ? null : target.GetType();
                 type != null; type = type.BaseType)
                foreach (MethodInfo method in type.GetMethods(InstanceFlags | BindingFlags.DeclaredOnly))
                    if (method.Name == name && method.GetParameters().Length == argumentCount)
                        return method;
            return null;
        }

        private static void InvokeIfPresent(object target, string name)
        {
            MethodInfo method = FindMethod(target, name, 0);
            if (method != null) method.Invoke(target, null);
        }
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
        DrawCameraFocus(controller, canControl);
    }

    public override bool RequiresConstantRepaint()
    {
        return EditorApplication.isPlaying;
    }

    private void DrawCameraFocus(GameSpeedDebugController controller, bool canControl)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Camera Focus", EditorStyles.boldLabel);
        serializedObject.Update();
        EditorGUILayout.PropertyField(
            serializedObject.FindProperty("m_cameraTeam"), new GUIContent("Team"));
        serializedObject.ApplyModifiedProperties();
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.ColorField(
                "Team " + (controller.SelectedCameraTeamIndex + 1),
                controller.SelectedCameraTeamColor);
        using (new EditorGUI.DisabledScope(!canControl))
        {
            EditorGUILayout.BeginHorizontal();
            for (int number = 1; number <= 4; number++)
                using (new EditorGUI.DisabledScope(!controller.CanFocusCameraMember(number)))
                    if (GUILayout.Button(number.ToString()))
                        controller.FocusCameraMember(number);
            if (GUILayout.Button("Auto")) controller.ReturnAutomaticCamera();
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.LabelField("Focus", controller.CameraFocusStatus);
        if (canControl && !controller.CanFocusCameraMember(1)
            && !controller.CanFocusCameraMember(2)
            && !controller.CanFocusCameraMember(3)
            && !controller.CanFocusCameraMember(4))
            EditorGUILayout.HelpBox(
                "選んだチームのキャラクターか観戦カメラがまだ生成・表示されていません。",
                MessageType.Info);
    }

    private static void DrawGameButton(
        GameSpeedDebugController controller, string label, float speed)
    {
        if (GUILayout.Button(label)) controller.SetGameSpeed(speed);
    }
}
#endif
