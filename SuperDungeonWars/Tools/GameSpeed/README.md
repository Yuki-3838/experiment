# ゲーム速度デバッグ

`GameSpeedDebugController.cs` は、ゲーム全体を一時停止・減速・加速する単体コンポーネントです。Unity標準機能だけで動き、他のコンポーネントへの参照設定や追加パッケージを必要としません。

## 使い方

1. `GameSpeedDebugController.cs` を対象プロジェクトの `Assets/` 以下へコピーします。
2. Play中に有効なまま残るGameObjectへ `GameSpeedDebugController` を1つ追加します。
3. 画面左上のボタンで速度を選びます。`Reset` はアタッチ時点の速度へ戻します。

ボタンは Pause、0.25x、0.5x、1x、2x、5x、10x です。パネルはドラッグして移動できます。Pause中も画面上の操作は使えます。コンポーネントを無効化するかGameObjectを破棄すると、アタッチ前の速度へ戻ります。

## 対象範囲

このコンポーネントは `Time.timeScale` を変更します。そのため、Unityのスケール時間を使う `Time.deltaTime`、`WaitForSeconds`、通常モードのAnimatorやParticle Systemなどが速度変更の対象です。物理演算もスケールしますが、高速時には更新負荷やシミュレーション精度が変わります。

`Time.unscaledDeltaTime`、`Time.unscaledTime`、`WaitForSecondsRealtime` を使う別処理は対象外です。結果画面のように独自の待ち時間を使う演出には、[結果演出用の補助スクリプト](../ResultFastForward/README.md)を使うか、その演出をスケール時間へ接続してください。ゲーム速度と結果演出の短縮は別々に操作できます。

## 制限

画面パネルはGameObjectが有効なシーンで表示されます。シーン遷移後も使う場合は、遷移後も有効なGameObjectへ取り付けてください。ゲーム固有の型や結果画面の情報を読み取る処理はありません。
