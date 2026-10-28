# SuperDungeonWars — 回収AI・独立待ち伏せAI・共通地形推定

正式受付番号 **0004838** の回収AIと、別の実験用待ち伏せAI。4日計画は完了済み。その後の依頼で待ち伏せ版を分離し、2026年10月28日は入口待機の修正と、任意のAIから使える回転対称地形推定を追加した。

## 使うAIと必要ファイル

| AI | フォルダー | 必要なC# |
|---|---|---|
| 通常のAriadne | [Party0004838](Assets/Participants/Party0004838) | 自身の4本＋共通1本。中央の金宝箱へ1人、ほかは近場回収・探索等 |
| 独立待ち伏せ版 | [AriadneEntranceAmbush](Assets/Participants/AriadneEntranceAmbush/README.md) | 自身の5本＋共通1本。1人が回収、自陣以外の3入口を1人ずつ守る |
| 他のAIでも使える地形推定 | [Common](Assets/Participants/Common/README.md) | C4SymmetricMap.csの1本。Ariadneの型には依存しない |

両AIを同じプロジェクトへ置く場合は共通クラスを1回だけ配置する。補助クラスのアタッチは不要。各Partyの観測・推定インスタンスは共有しない。

## 導入

配布済みのUnityプロジェクトへ追加するスクリプト。ゲーム本体・Prefab・meta・外部アセットはGitに含めない。今回の確認用Unityは6000.3.27f1。

1. 通常版は `Assets/Participants/Party0004838/` のC#4本、待ち伏せ版は `Assets/Participants/AriadneEntranceAmbush/` のC#5本を使用する。通常版のPrefabがなければ **プロバト → 挑戦者作成** から0004838で生成する。既存metaは維持する。
2. **Assets/Participants/Common/C4SymmetricMap.csも必要。** 両版で1本を共有する。
3. Party用PrefabのMember Prefabsを対応するCharacterのPrefabへ指定し、BattlePartyRegistryへPartyのPrefabを登録する。待ち伏せ版のPrefab作成は [別AIの説明](Assets/Participants/AriadneEntranceAmbush/README.md)。
4. `Assets/Dungeon/Scenes/MazeExplore.unity` を開き、Playから紹介・カウントダウンへ進む。

このローカル環境は、シーンのControllerが `AriadneEntranceAmbushRegistry.asset` を参照するよう変更済み。待ち伏せPrefabの一時回収はOFF。開いているUnityがPlay中なら停止して再生し直す。通常版へ戻す場合はControllerのBattle Party Registryを元の `BattlePartyRegistry.asset` へ変更する。両Registryの内容は別に保持している。

## 現在の待ち伏せ配置：3入口へ1人ずつ

前版は1入口へ3人を集中する設定だった。最新の指摘に合わせ、メンバー0を回収、1〜3を自陣以外の3入口へ1人ずつ固定配置する。各自の入口から1マス内側で待ち、迎撃・回収範囲と状態表示も担当ごとに決める。[分担配置の変更点](AIExperiments/Ariadne/AMBUSH_SPREAD_CHANGES.md)。

今回の分担配置は隔離プロジェクトでコンパイルのみ確認。再生・物理待機・対戦比較は未実施。以下の物理確認は前版の1入口集中配置の記録であり、分担配置の動作確認として扱わない。

## 入口待機に関する前回の修正

保存されたシーンが回収版を選んでいたこと、待ち伏せPrefabで一時回収がONだったことを確認し、ローカル設定を修正した。ON/OFF機能は残す。OFFでも迎撃・奪取・搬出では配置を離れる。

押し戻しの確認では、論理セルだけが配置セルのままで実座標が隣セルにずれるケースを再現した。修正版は、公開された進入方向へ少し入った点を待機位置とし、押された後はその位置へ戻って停止する。迎撃範囲も入口そのものを中心とする2マス以内へ狭めた。ユーザーの実際の試合が同じ原因だったとは断定していない。

## 地形の共通クラス

`C4SymmetricMap` は公開KnownMapViewを受け取り、観測した地形を90度ずつ回転して推定する。実測と推定を区別し、矛盾を観測したら全推定を破棄する。中央の行・列も処理する。部屋ID・敵・宝箱の現在位置は回転コピーしない。

両AIには接続済み。他のAIはこの1本を置き、`Refresh` と `TryGetNextStep` 等を呼べば利用できる。[組み込み例・API・前提](Assets/Participants/Common/README.md)。地形が推定できても宝を調べたことにはならないため、未訪問・未観測の探索は残す。

## 共通地形クラスと前版の確認範囲

- コンパイルと共通クラスの12項目を確認。4生成マップで一区画＋境界の441セルから残り1240セルを推定し、全地形の一致と、別入口への経路が実際の壁を横切らないことを確認した。
- 前版の1入口集中配置では、実際のExplorerAgent・物理衝突・視界・命令適用を使った1マップ×4開始方向で、3人が入口へ到着。押し戻し後も戻り、実座標が配置セル内で3人同時に12秒以上停止することを確認した。
- 敵・宝箱・戦闘・得点処理のない隔離条件。対戦比較・得点や勝率の測定はしていない。検証環境のUnityEditor.Searchの検索インデックス例外1件もログに保存した。詳細は [今回の記録と限界](AIExperiments/Ariadne/C4_HOLD_CHANGES.md)。

## 日別の保存版

| 開発日 | 保存版 | 内容と記録 |
|---|---|---|
| 2日目・2026-10-24 | [2日目の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-day2/SuperDungeonWars) | 回収・搬出の優先度調整。[2日目の記録](AIExperiments/Ariadne/DAY2_RESULTS.md) |
| 3日目・2026-10-25 | [3日目の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-day3/SuperDungeonWars) | 護衛判断・短い迎撃。[3日目の記録](AIExperiments/Ariadne/DAY3_RESULTS.md) |
| 4日目・2026-10-26 | [4日目の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-day4/SuperDungeonWars) | 全4位置での比較・近距離迎撃・正式番号への変更・提出準備。[4日目の記録](AIExperiments/Ariadne/DAY4_RESULTS.md) |
| 追加強化1・2026-10-26 | [追加実装の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-upgrade1/SuperDungeonWars) | 通常敵への反撃と運搬経路。[変更点・未測定事項](AIExperiments/Ariadne/UPGRADE1_CHANGES.md) |
| 追加強化2・2026-10-26 | [追加実装の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-upgrade2/SuperDungeonWars) | 近場回収の先行割当・長い回収の人数制限。[変更点・入口のルール](AIExperiments/Ariadne/UPGRADE2_CHANGES.md) |
| 追加強化3・2026-10-26 | [追加実装の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-upgrade3/SuperDungeonWars) | 中央の金宝箱へ専任1人、道中の通常宝箱を選別。[変更点・解除条件](AIExperiments/Ariadne/UPGRADE3_CHANGES.md) |
| 追加強化4・2026-10-27 | [追加実装の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-upgrade4/SuperDungeonWars) | 入口待ち伏せ3人と回収1人、空振り時の切り替えON/OFF。[設定と未測定事項](AIExperiments/Ariadne/UPGRADE4_CHANGES.md) |
| 独立待ち伏せ版・2026-10-27 | [分離後の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-ambush1/SuperDungeonWars) | 通常AIと分離、3人を1入口へ配置、待機を初期設定に変更。[確認範囲と変更点](AIExperiments/Ariadne/AMBUSH1_CHANGES.md) |
| 共通C4・待機修正・2026-10-28 | [固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-c4-hold/SuperDungeonWars) | 共通地形推定を両AIへ接続、入口の実座標維持。[変更点と確認範囲](AIExperiments/Ariadne/C4_HOLD_CHANGES.md) |
| 3入口分担・2026-10-28 | [固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-ambush-spread/SuperDungeonWars) | 入口を担当ごとに分離。[変更点](AIExperiments/Ariadne/AMBUSH_SPREAD_CHANGES.md) |

過去のタグと記録は固定のまま残す。2・3日目は当時の仮番号Party1111111、4日目以降は正式番号0004838。

## デバッグ用補助スクリプト

[AutoMatch](Tools/AutoMatch/README.md) は4チームの自動連戦と勝率集計。`AutoMatchTest.cs` 1本をAssets以下へ置き、Play中に `DontDestroyOnLoad > AutoMatchTest` のInspectorから開始する。現在の4チームを自動取得し、0番を測定対象として同じ迷路で全4位置を回せる。試合数・速度を指定でき、各試合の得点・順位・搬出数と勝率をプロジェクト直下のAutoMatchResultsへCSV/TXTで保存する。追加時点では自動連戦を実行していない。

[GameSpeed](Tools/GameSpeed/README.md) はゲーム速度、結果の宝箱開封待ち、カメラフォーカスを操作する1ファイルのデバッグツール。`GameSpeedDebugController.cs` をAssets以下へコピーすると、EditorのPlay開始時に1回だけ自動生成する。HierarchyのDontDestroyOnLoad内にあるGameSpeedDebugControllerを選ぶと、Inspectorに速度ボタンとSkip Result Waitsが出る。Camera Focusでは赤・青・黄・緑を選び、1〜4番の登録メンバーへ追従できる。Autoで自動観戦へ戻る。結果処理は元の実時間版とスケール時間へ変更済みの版をEditorで判別する。

同じPlay中のシーン再読み込みでは速度・結果設定・カメラ選択を保持する。デバッグ用オブジェクトはDontDestroyOnLoadに残り、MazeExploreの再読み込みでは再生成しない。旧版でシーンやPrefabに配置したものは外す。次の試合で生成された同じ色・番号のキャラクターへカメラをつなぎ直す。

[ResultFastForward](Tools/ResultFastForward/README.md) は結果画面専用の旧ツール。新しいGameSpeedを使う場合は、同じ時間設定を二重に短縮しないよう旧コンポーネントを外す。これらのデバッグ用ファイルは参加者AIの提出フォルダーには含めない。

## 提出用ZIPと公開範囲

以前の検査済みZIPは4日目固定版のまま。今回の版の提出ZIPとしては案内しない。共通クラスは通常の参加者フォルダー外にあるため、番号ごとのZIPへ含める準備と再コンパイル・提出前検査が必要。同じプロジェクトで型を重複コピーしない。独立待ち伏せ版は実験用型名で、正式提出用の検査も未実施。

公開するのはAI C#と直接付随するMarkdown、別依頼で許可されたToolsのC#と説明だけ。今回変更したローカルのシーン参照・Prefab設定、meta・Registry・確認用コード・DLL・生データ・ログ・ZIPは追加しない。

## 4日目までの実測結果（今回の修正・共通地形推定の成績ではない）

同じ2マップ×上下2位置の4試合でAriadneは1位2回、平均432.75点。比較対象TreasureReturnは1位1回、平均401.5点。全8試合の実行エラーは0件だった。

開始位置による差が残り、各条件1試合のため安定した優位性は確認できていない。詳しい条件と限界は [1日目の実測結果](AIExperiments/Ariadne/DAY1_RESULTS.md) を参照。

2日目は回収と探索の優先度を調整した。同じ4マップ・8開始条件で、前日版の平均334.625点・搬出3.000個に対し、採用版は平均502.750点・搬出4.625個。採用版を同じ8条件で再実行し、順位・得点・搬出数がすべて一致した。候補比較を含む計32試合で記録上の実行エラーは0件。

上辺開始など得点が下がった条件もある。全マップで4位置ずつを評価した結果ではなく、候補選定と再測定も同じ条件を使っている。比較条件と悪化した条件を含む詳細は [2日目の実測結果](AIExperiments/Ariadne/DAY2_RESULTS.md) を参照。

3日目は護衛判断と短い迎撃を比較した。初回の計6マップ・12開始条件で、2日目版の平均511.5点・1位6回に対し、採用版は533.5点・1位7回。得点が下がった条件は5条件あり、運搬中のKOも合計24→27回と増えている。護衛の安定した改善は確認できていない。候補比較・追加評価・再実行の計48試合で実行エラー0件。候補選定用8条件と追加評価用4条件を分けた詳細は [3日目の実測結果](AIExperiments/Ariadne/DAY3_RESULTS.md) を参照。

4日目は6マップ×全4位置の24条件を3版で比較した。採用版は1位13/24・平均511.292点、3日目版は1位12/24・平均521.042点。1位回数を優先する事前基準で選んだが、平均点は下がっている。選定後の未使用2マップ×全4位置では、3日目版が平均417.625点・1位4/8、最終版が平均450.500点・1位5/8だった。

正式番号に変更した最終版の再測定も含む計96試合で、記録上の実行エラーは0件。開始位置による差、得点の悪化、移動中の停滞が残る条件もあり、全条件での優位性は確認できていない。詳細は [4日目の実測結果](AIExperiments/Ariadne/DAY4_RESULTS.md) を参照。

4日計画の自動開発は終了済み。追加強化1・2・3・4は、その後のユーザーの依頼に基づく手動の追加実装であり、対戦での効果は未測定。2・3・4日目のタグと記録は固定のまま残している。
