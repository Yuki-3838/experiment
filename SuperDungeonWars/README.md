# SuperDungeonWars — 回収AIと独立待ち伏せAI

正式受付番号 **0004838** の回収AIと、別の実験用待ち伏せAIを保存する。2026年10月23〜26日の4日計画は完了済み。10月27日の依頼で待ち伏せ版を通常AIから分離した。

## どちらを使うか

| AI | フォルダー | 内容 |
|---|---|---|
| 通常のAriadne | [Party0004838](Assets/Participants/Party0004838) | C#4本。追加強化3の回収型へ戻した。中央の金宝箱へ1人を優先し、ほかは近場回収・探索等 |
| 独立した待ち伏せ版 | [AriadneEntranceAmbush](Assets/Participants/AriadneEntranceAmbush/README.md) | C#5本。1人が回収、3人が同じ敵入口を守る。切り替えは初期OFF |

通常AIへ待ち伏せ用の設定を混ぜない。別のParty・Character用Prefabとして登録して選ぶ。補助クラスはアタッチ不要。

## 通常AIの導入

配布済みのUnityプロジェクトへ追加するAIスクリプトであり、ゲーム本体・Prefab・外部アセットはこのGitには含めない。この環境の確認用Unityは6000.3.27f1。

1. `Assets/Participants/Party0004838/` のPrefabがなければ、配布版の **プロバト → 挑戦者作成** から番号0004838で生成する。
2. 同フォルダーの `Party0004838Party.cs`、`Party0004838Character.cs`、`AriadneMap.cs`、`AriadneTactics.cs` の4本を置く。既存のmetaは維持する。通常AIに旧版のAriadneAmbush.csは不要。
3. Party用PrefabのMember Prefabsを、このCharacterのPrefabへ設定する。BattlePartyRegistryへParty用Prefabを登録する。
4. `Assets/Dungeon/Scenes/MazeExplore.unity` を開いてPlayし、チーム紹介からEnterまたはSpaceで進める。

ローカルの既存Registryは通常の回収型を使う。待ち伏せ版の導入・設定は [別AIのREADME](Assets/Participants/AriadneEntranceAmbush/README.md) を参照。ローカルに用意した `AriadneEntranceAmbushRegistry.asset` をControllerのBattle Party Registryへ指定すると待ち伏せ版になる。

## 入口で待っていなかった件

前回の追加強化4は実行確認をしていなかった。旧版は入口への経路が未知なら探索を続け、到着後も初期設定では8秒で一時回収へ出る作りだった。今回、旧タイマーの8秒切り替えを限定確認したが、ユーザーの実際の場面を再現して原因を確定したわけではない。

独立版では**待機継続を初期設定**とし、同じ敵入口の1〜3マス内側に3人を配置する。`敵が来ない時に近場回収へ切り替える` をONにした時だけ8秒待機→8秒近場回収・探索→配置へ帰還する。OFFでも迎撃・奪取・搬出では配置を離れる。

実行中のPartyの `現在の担当` に経路探索中・移動中・待機中等を表示する。未観測の経路は開拓が必要で、120秒以内の配置完了は未確認。

## 今回の確認範囲

隔離した最小Unityプロジェクトでコンパイルと12項目の命令・タイマー確認を通過。全4方向から既知経路で3人を配置し、到着後100判断の停止命令を確認した。**確認用の移動実装を使っており、実際の物理挙動・衝突・戦闘を含む対戦比較は行っていない。強さの改善は未測定。** 詳しくは [変更点と確認の限界](AIExperiments/Ariadne/AMBUSH1_CHANGES.md)。

## 四分の一からの地形推定

ユーザーの指摘どおり、配布版の生成処理は90度回転の対称構造を持つ。生成した4マップの全セルで開口・部屋・中央ホールの回転一致を確認した。中央の縦横の列を除いた一区画だけでは、その行・列の情報は含まれない。敵や宝箱の現在位置も地形とは別。

今回のAIには回転推定を追加していない。参加者AIは引き続き公開SXG APIと観測を使用し、非公開の生成マップ・シードは取得しない。

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

過去のタグと記録は固定のまま残す。2・3日目は当時の仮番号Party1111111、4日目以降は正式番号0004838。

## デバッグ用補助スクリプト

[GameSpeed](Tools/GameSpeed/README.md) はゲーム速度、結果の宝箱開封待ち、カメラフォーカスを操作する1ファイルのデバッグツール。`GameSpeedDebugController.cs` をAssets以下へコピーすると、EditorのPlay開始時に1回だけ自動生成する。HierarchyのDontDestroyOnLoad内にあるGameSpeedDebugControllerを選ぶと、Inspectorに速度ボタンとSkip Result Waitsが出る。Camera Focusでは赤・青・黄・緑を選び、1〜4番の登録メンバーへ追従できる。Autoで自動観戦へ戻る。結果処理は元の実時間版とスケール時間へ変更済みの版をEditorで判別する。

同じPlay中のシーン再読み込みでは速度・結果設定・カメラ選択を保持する。デバッグ用オブジェクトはDontDestroyOnLoadに残り、MazeExploreの再読み込みでは再生成しない。旧版でシーンやPrefabに配置したものは外す。次の試合で生成された同じ色・番号のキャラクターへカメラをつなぎ直す。

[ResultFastForward](Tools/ResultFastForward/README.md) は結果画面専用の旧ツール。新しいGameSpeedを使う場合は、同じ時間設定を二重に短縮しないよう旧コンポーネントを外す。これらのデバッグ用ファイルは参加者AIの提出フォルダーには含めない。

## 提出用ZIPと公開範囲

以前の提出前検査を通過したZIPは4日目固定版のまま。今回の回収型・独立待ち伏せ版の提出ZIPとして案内しない。独立待ち伏せ版は実験用の型名で、正式提出用の検査は未実施。

公開するのはAIのC#と直接付随するMarkdown、別依頼で許可されたToolsのC#と説明だけ。Prefab・meta・Registry・ゲーム設定・評価用Editorコード・生データ・ログ・ZIP・外部アセットは追加しない。

## 4日目までの実測結果（今回の待ち伏せ版の成績ではない）

同じ2マップ×上下2位置の4試合でAriadneは1位2回、平均432.75点。比較対象TreasureReturnは1位1回、平均401.5点。全8試合の実行エラーは0件だった。

開始位置による差が残り、各条件1試合のため安定した優位性は確認できていない。詳しい条件と限界は [1日目の実測結果](AIExperiments/Ariadne/DAY1_RESULTS.md) を参照。

2日目は回収と探索の優先度を調整した。同じ4マップ・8開始条件で、前日版の平均334.625点・搬出3.000個に対し、採用版は平均502.750点・搬出4.625個。採用版を同じ8条件で再実行し、順位・得点・搬出数がすべて一致した。候補比較を含む計32試合で記録上の実行エラーは0件。

上辺開始など得点が下がった条件もある。全マップで4位置ずつを評価した結果ではなく、候補選定と再測定も同じ条件を使っている。比較条件と悪化した条件を含む詳細は [2日目の実測結果](AIExperiments/Ariadne/DAY2_RESULTS.md) を参照。

3日目は護衛判断と短い迎撃を比較した。初回の計6マップ・12開始条件で、2日目版の平均511.5点・1位6回に対し、採用版は533.5点・1位7回。得点が下がった条件は5条件あり、運搬中のKOも合計24→27回と増えている。護衛の安定した改善は確認できていない。候補比較・追加評価・再実行の計48試合で実行エラー0件。候補選定用8条件と追加評価用4条件を分けた詳細は [3日目の実測結果](AIExperiments/Ariadne/DAY3_RESULTS.md) を参照。

4日目は6マップ×全4位置の24条件を3版で比較した。採用版は1位13/24・平均511.292点、3日目版は1位12/24・平均521.042点。1位回数を優先する事前基準で選んだが、平均点は下がっている。選定後の未使用2マップ×全4位置では、3日目版が平均417.625点・1位4/8、最終版が平均450.500点・1位5/8だった。

正式番号に変更した最終版の再測定も含む計96試合で、記録上の実行エラーは0件。開始位置による差、得点の悪化、移動中の停滞が残る条件もあり、全条件での優位性は確認できていない。詳細は [4日目の実測結果](AIExperiments/Ariadne/DAY4_RESULTS.md) を参照。

4日計画の自動開発は終了済み。追加強化1・2・3・4は、その後のユーザーの依頼に基づく手動の追加実装であり、対戦での効果は未測定。2・3・4日目のタグと記録は固定のまま残している。
