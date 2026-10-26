using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GameSpeedDebugController))]
public sealed class GameSpeedDebugControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        GameSpeedDebugController controller =
            (GameSpeedDebugController)target;
        bool canControl =
            EditorApplication.isPlaying
            && controller.isActiveAndEnabled;

        EditorGUILayout.LabelField(
            "Game Speed",
            EditorStyles.boldLabel);

        if (EditorApplication.isPlaying)
        {
            EditorGUILayout.LabelField(
                "Current speed",
                Time.timeScale.ToString("0.##") + "x");
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Enter Play mode to change the game speed.",
                MessageType.Info);
        }

        using (new EditorGUI.DisabledScope(!canControl))
        {
            EditorGUILayout.BeginHorizontal();
            DrawSpeedButton(controller, "Pause", 0.0f);
            DrawSpeedButton(controller, "0.25x", 0.25f);
            DrawSpeedButton(controller, "0.5x", 0.5f);
            DrawSpeedButton(controller, "1x", 1.0f);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawSpeedButton(controller, "2x", 2.0f);
            DrawSpeedButton(controller, "5x", 5.0f);
            DrawSpeedButton(controller, "10x", 10.0f);

            if (GUILayout.Button("Reset"))
            {
                controller.ResetGameSpeed();
            }

            EditorGUILayout.EndHorizontal();
        }

        if (!controller.isActiveAndEnabled)
        {
            EditorGUILayout.HelpBox(
                "Enable this component and its GameObject to use the controls.",
                MessageType.Warning);
        }
    }

    private static void DrawSpeedButton(
        GameSpeedDebugController controller,
        string label,
        float speedMultiplier)
    {
        if (GUILayout.Button(label))
        {
            controller.SetGameSpeed(speedMultiplier);
        }
    }
}
