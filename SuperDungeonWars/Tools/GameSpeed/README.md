# ゲーム速度・リザルト宝箱デバッグ

`GameSpeedDebugController.cs` 1本に、ゲーム速度、結果の宝箱開封待ち時間、Inspectorの操作ボタンをまとめています。ゲーム固有のアセンブリ参照やInspectorへのオブジェクト指定、追加パッケージは不要です。

## 設置

1. `GameSpeedDebugController.cs` を対象プロジェクトの `Assets/` 以下へコピーします。Editorフォルダには入れません。
2. 常時有効なGameObjectに `GameSpeedDebugController` を1つアタッチします。
3. Play中にそのGameObjectをHierarchyから選ぶと、Inspectorに操作ボタンが表示されます。

旧版の `Editor/GameSpeedDebugControllerEditor.cs` は削除してください。Inspector用コードもこの1本に含まれています。旧 `BattleResultFastForwardController` がアタッチされている場合は外して、同じ待ち時間を二重に短縮しないようにします。

## 操作

- **Game Speed**: Pause、0.25x、0.5x、1x、2x、5x、10x。ゲームの `Time.timeScale` を変更します。
- **Follow Game Speed**: 初期状態でONです。結果の開封待ち・金額加算・ポップアップの時間もゲームの倍率に合わせます。
- **Result / Chests の倍率ボタン**: 結果だけ別の速度にします。押すとFollow Game SpeedがOFFになります。
- **Skip Result Waits**: 対応する結果の待ち時間を0にして、最終結果までの順次処理を早めます。結果の計算や表示完了処理は元のゲームが実行します。
- **Reset**: 開始時のゲーム速度、結果1x、待ち時間スキップOFFに戻します。FollowがONなら、結果速度は戻したゲーム速度に合わせます。

結果画面が非表示でも検出し、表示時に生成されるチームレーンも追跡します。InspectorのDetected results / timing fieldsで検出数を確認できます。コンポーネントを無効化・破棄すると、変更した時間設定と開始時のゲーム速度を復元します。

## 対象と時間の扱い

SuperDungeonWarsの `BattleResultPresentation` と `BattleResultTeamLane` を型名で探し、公開またはSerializeField付きfloatのうち、Duration・Delay・Intervalで終わる時間設定だけを調整します。スコア、順位、戦闘中の行動間隔、宝箱配置の物理設定は変更しません。ゲームのC#ファイルを変更する必要はありません。

**Result Clock = Automatic**はEditorで対象スクリプトを読み、実時間を使う元の版と、スケール時間へ変更済みの版を判別します。スケール時間版はTime.timeScaleを補正して、倍率が二重にならないようにします。独自に時間を混在させた版は、Result ClockでUnscaledまたはScaledを指定してください。プレイヤービルドではAutomaticは元の版に合わせてUnscaled扱いになります。Editor用コードはビルドから除外されます。

## 制限

- すでに生成された `WaitForSecondsRealtime` の残り時間は書き換えられません。倍率やスキップは結果開始前に指定すると、最初の待ちから反映されます。
- Pauseはスケール時間を止めます。実時間で動く結果コルーチンの完全停止には対応していません。
- 対象の型名や時間フィールドの規則が異なるゲーム、コードへ直接書かれた秒数、Animator内部の遷移時間はこの時間設定変更の対象外です。
- Skip Result Waitsも、宝箱配置の完了待ちやフレーム単位のyieldを強制終了する機能ではありません。
