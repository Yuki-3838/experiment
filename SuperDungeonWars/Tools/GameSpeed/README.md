# ゲーム速度デバッグ

`GameSpeedDebugController` を付けたGameObjectをPlay中に選択すると、InspectorからPause・0.25x・0.5x・1x・2x・5x・10x・Resetを操作できます。左上などのゲーム画面にはパネルを表示しません。

## 設置

1. `GameSpeedDebugController.cs` を対象プロジェクトの `Assets/` 以下へコピーします。
2. `Editor/GameSpeedDebugControllerEditor.cs` も `Assets/` 以下の `Editor/` フォルダへコピーします。例: `Assets/DebugTools/GameSpeed/Editor/`
3. シーン内の有効なGameObjectに `GameSpeedDebugController` を1つ追加します。
4. Playを開始し、HierarchyでそのGameObjectを選ぶとInspectorに速度ボタンが表示されます。

GameObjectへアタッチするコンポーネントは1つです。EditorスクリプトはInspectorのボタン表示用で、ビルドには含まれません。

`Reset` はコンポーネントが有効になった時点の速度へ戻します。コンポーネントを無効化するかGameObjectを破棄した場合も同じ速度へ戻します。

## 対象範囲

このツールは `Time.timeScale` を変更します。`Time.deltaTime`、`WaitForSeconds`、通常モードのAnimatorやParticle System、物理演算など、スケール時間を使う処理が対象です。高速時はシミュレーション負荷や精度が変わることがあります。

`Time.unscaledDeltaTime`、`Time.unscaledTime`、`WaitForSecondsRealtime` や独自タイマーを使う処理は対象外です。結果画面のように独自の待ち時間を使う演出には、[結果演出用の補助スクリプト](../ResultFastForward/README.md)を使うか、その待ち時間をスケール時間へ接続してください。
