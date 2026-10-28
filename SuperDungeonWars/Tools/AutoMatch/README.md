# 自動対戦・勝率集計

SuperTankWarsのAutoMatchTestと同様に、4チームを自動で対戦させ、指定試合数まで勝率を集計するSuperDungeonWars用ツールです。`AutoMatchTest.cs` 1本に実行処理とInspectorを含みます。ゲームのアセンブリ参照、Inspectorへの管理オブジェクトの割り当て、追加パッケージは不要です。

## 導入・操作

1. `AutoMatchTest.cs` をプロジェクトの `Assets/` 以下へコピーします。Editorフォルダには置きません。
2. `MazeExplore` をBuild Settingsのシーンに含めてPlayします。
3. Hierarchyの `DontDestroyOnLoad > AutoMatchTest` を選びます。起動時に1個だけ自動生成されるので、シーンへのアタッチは不要です。
4. 現在のBattlePartyRegistryの先頭4チームがPlayersへ自動で入ります。再取得する場合は **現在の4チームを読込** を押します。
5. **Playersの0番が測定対象** です。変更するときは、Project内のParty用Prefabをドラッグします。Character用PrefabやHierarchyの実体は指定しません。同じPrefabを複数枠に指定することもできます。
6. Match Countなどを設定し、**自動対戦を開始** を押します。設定を反映するため一度シーンを読み直してから、1試合目を始めます。
7. 指定数で停止し、最後の結果画面を残します。途中で終了する場合は **停止・保存**。完了した試合の記録は残ります。

Playを停止すると、Inspectorの設定と集計値は消えます。CSVと集計TXTは残ります。EditorのPlayとDevelopment Buildでは起動時に自動生成します。操作用InspectorはEditor用です。

## 設定

- **Match Count**: 試合数。初期値24。全4位置を同じ回数で比較する場合は4の倍数にします。
- **Rotate Seats**: 0番の開始位置を1→2→3→4と回します。他の3チームも相対的な順序を保って回転します。OFFでは0番を位置1に固定します。
- **Cycle Approved Maps**: ゲームの審査済み迷路を順に使います。Rotate SeatsがONなら同じ迷路で4位置を回り、次の迷路へ進みます。OFFではゲーム本来の迷路選択を使います。
- **First Map Index**: 審査済み迷路一覧の開始番号。0始まりで、末尾の次は先頭へ戻ります。
- **First Seed**: Unity乱数の開始Seed。回転ONでは4試合を1組として `First Seed + 組番号`、回転OFFでは `First Seed + 完了試合数` を使用します。迷路Seedとは別です。
- **FPS**: `Time.captureDeltaTime = 1 / FPS` の時間刻み。初期値120。VSyncを切り、描画のフレーム数上限を外して進めます。物理のfixedDeltaTimeは開始前の値を維持します。
- **Speed Multiplier**: ゲーム速度1～8倍。初期値4倍。
- **Fast Presentation**: 初期値ON。カウントダウンを省略し、Resultの待ち・金額加算・ポップアップ時間を0にします。宝箱配置の完了待ちと必要なフレーム待ちは残ります。OFFでは通常のカウントダウン・結果演出を待ちます。
- **Match Timeout Seconds**: 1試合を準備するところからの実時間上限。初期値300秒。超過した試合は集計せずに停止します。

## 集計

- 4チームすべての勝ち・同点1位・負け・勝率・平均得点をInspectorに表示します。
- **勝ち**: 他の3チームすべてより得点が高い、単独1位。
- **同点1位**: 最高得点で他チームと同点。勝ちには含めません。
- **負け**: 最高得点に届かなかった試合。
- **勝率**: 単独1位の試合数 ÷ 完了試合数 × 100。
- ゲームが確定した `BattleResult.TeamResults` の得点・順位を読みます。結果演出の完了を確認してから1回だけ記録します。勝敗用の得点を独自に再計算しません。
- 開始位置を回しても、InspectorとCSVの0～3はPlayersの登録順に対応します。

## 保存先

EditorではUnityプロジェクト直下の `AutoMatchResults/` にUTF-8のCSVと集計TXTを作成します。Inspectorの **結果ファイルの場所を開く** から開けます。Assets以下には出力しません。ビルドでは `Application.persistentDataPath/AutoMatchResults/` です。

- `Dungeon_日時.csv`: 各試合の迷路Seed、Unity開始Seed、対象の開始位置、4チームの得点・順位・搬出数、対象の勝敗、累積勝率。
- 同名の `.txt`: 設定、4チームのPrefab名・型名、勝敗数・勝率・平均得点・平均順位。
- CSVは試合ごとに追記し、TXTも各試合後と終了時に更新します。エラー・例外・タイムアウトで中断しても、それまで完了した試合は残ります。

## ゲームとの接続

- 参加者一覧はBattlePartyRegistryの実行中コピーへ設定します。元の登録アセットは編集しません。
- 紹介・カウントダウン・試合終了・Resultの入力待ちは、既存のSceneFlowや演出のメソッドをリフレクションで呼んで進めます。計測中のキー操作は不要です。
- 次の試合は既存のFinish処理によるシーン再読み込みで開始します。ツール自身はDontDestroyOnLoadに残り、再生成しません。
- 元のMazeGeneratorは `m_seed` の設定値を使わず、審査済み一覧からSeedを選びます。計測時は一覧の配列要素を迷路生成の間だけ同じSeedへ揃え、生成直後に元へ戻します。停止・エラー時にも復元します。生成ログから実際のSeedを照合し、固定を確認できない版では停止します。
- GameSpeedDebugControllerとBattleResultFastForwardControllerは計測中だけ無効にし、終了時に有効状態を戻します。時間設定・FPS・バックグラウンド実行設定も戻します。

## 確認範囲・制限

- 現在のSuperDungeonWarsの型名・メンバー名・ログ形式を使います。ゲーム側のAPIが変更された版では対応が必要です。
- 同じ迷路とUnity開始Seedを使用しても、物理処理やほかの乱数の利用があるため、完全に同じ試合結果になることは保証しません。
- シーンやPrefabにこのツールを配置すると再読み込み時のコピーが生まれるため、配置せず自動生成を使ってください。
- ビルドにはInspectorがないため、実行時の設定・開始には別途呼び出し元が必要です。通常はEditorのPlayで使います。
- この追加時点では自動連戦を実行しておらず、実測勝率や実際の画面遷移は未確認です。
