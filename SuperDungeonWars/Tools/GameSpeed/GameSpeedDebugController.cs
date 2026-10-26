using UnityEngine;

/// <summary>
/// 画面上のボタンからゲーム全体の速度を変更するデバッグ用コンポーネント。
/// Inspector の参照設定、追加パッケージ、ゲーム固有の型は必要ない。
/// </summary>
[DisallowMultipleComponent]
public sealed class GameSpeedDebugController : MonoBehaviour
{
    private const float PanelMargin = 12.0f;
    private const float PanelWidth = 420.0f;
    private const float PanelHeight = 116.0f;

    private Rect m_panelRect =
        new Rect(PanelMargin, PanelMargin, PanelWidth, PanelHeight);

    private float m_initialTimeScale = 1.0f;

    private void Awake()
    {
        m_initialTimeScale = Time.timeScale;
    }

    private void OnGUI()
    {
        if (!enabled)
        {
            return;
        }

        float width = Mathf.Min(
            PanelWidth,
            Mathf.Max(280.0f, Screen.width - PanelMargin * 2.0f));

        m_panelRect.width = width;
        m_panelRect.x = Mathf.Clamp(
            m_panelRect.x,
            0.0f,
            Mathf.Max(0.0f, Screen.width - width));
        m_panelRect.y = Mathf.Clamp(
            m_panelRect.y,
            0.0f,
            Mathf.Max(0.0f, Screen.height - PanelHeight));

        m_panelRect = GUI.Window(
            GetInstanceID(),
            m_panelRect,
            DrawPanel,
            "DEBUG  /  GAME SPEED");
    }

    private void DrawPanel(int windowId)
    {
        GUILayout.Label(
            "Current speed: "
            + Time.timeScale.ToString("0.##")
            + "x");

        GUILayout.BeginHorizontal();
        DrawSpeedButton("Pause", 0.0f);
        DrawSpeedButton("0.25x", 0.25f);
        DrawSpeedButton("0.5x", 0.5f);
        DrawSpeedButton("1x", 1.0f);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        DrawSpeedButton("2x", 2.0f);
        DrawSpeedButton("5x", 5.0f);
        DrawSpeedButton("10x", 10.0f);

        if (GUILayout.Button("Reset", GUILayout.Height(28.0f)))
        {
            Time.timeScale = m_initialTimeScale;
        }

        GUILayout.EndHorizontal();
        GUI.DragWindow(new Rect(0.0f, 0.0f, 10000.0f, 22.0f));
    }

    private static void DrawSpeedButton(
        string label,
        float speedMultiplier)
    {
        if (GUILayout.Button(label, GUILayout.Height(28.0f)))
        {
            Time.timeScale = speedMultiplier;
        }
    }

    private void OnDisable()
    {
        Time.timeScale = m_initialTimeScale;
    }
}
