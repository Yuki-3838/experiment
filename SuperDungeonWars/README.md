# SuperDungeonWars — Ariadne 3日目（2026-10-25）

4人で探索・宝箱回収・搬出を分担する参加者AI。2026年10月23日から26日までの4日間で段階的に改善する。

## 日別の保存版

**現在の開発版: 3日目（2026年10月25日）。護衛判断と敵運搬者への短い迎撃を追加。**

| 開発日 | 保存版 | 内容と記録 |
|---|---|---|
| 2日目・2026-10-24 | [2日目の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-day2/SuperDungeonWars) | 回収・搬出の優先度調整。[2日目の記録](AIExperiments/Ariadne/DAY2_RESULTS.md) |
| 3日目・2026-10-25 | [3日目の固定保存版](https://github.com/Yuki-3838/experiment/tree/superdungeonwars-ariadne-day3/SuperDungeonWars) | 護衛判断・短い迎撃。[3日目の記録](AIExperiments/Ariadne/DAY3_RESULTS.md) |

日別のGitタグを固定しているため、開発が進んだ後も各日のコードと説明を取り出せる。

## ゲーム本体のリザルト早送り実験

[ResultFastForward.patch](ResultFastForward.patch) は、配布プロジェクトの試合終了画面とリザルト演出を自動で5倍速にする実験パッチ。早送りのキー入力は不要で、該当画面中だけ有効になる。

取り付けるコンポーネントは [BattleResultFastForwardController.cs](Assets/Dungeon/Program/Game/Debug/BattleResultFastForwardController.cs)。時間制御の補助スクリプト [BattlePresentationTime.cs](Assets/Dungeon/Program/Game/Debug/BattlePresentationTime.cs) と各metaファイルも保存している。パッチを適用するとMazeExploreシーンのGameObject「BattleResultPresentation」に自動で追加される。手動で設定する場合も、このGameObjectに `BattleResultFastForwardController` を追加する。`BattleResultPresentation` は同じGameObjectから取得するため、Inspectorで参照を割り当てる必要はない。`BattlePresentationTime` はstatic補助クラスなのでGameObjectには追加しない。

適用するときは配布元リポジトリのルートで確認してから適用する。

```powershell
git apply --check /path/to/ResultFastForward.patch
git apply /path/to/ResultFastForward.patch
```

## 公開するファイル

参加者AI関連では、AI本体のC#スクリプト4本と、この説明書・実測結果の説明を保存する。

| スクリプト | 役割 |
|---|---|
| [Party1111111Party.cs](Assets/Participants/Party1111111/Party1111111Party.cs) | 部隊全体の担当割当、探索、宝の記憶、回収と帰還の判断 |
| [AriadneMap.cs](Assets/Participants/Party1111111/AriadneMap.cs) | 観測済み通路の管理、幅優先探索、搬出口までの距離の計算 |
| [Party1111111Character.cs](Assets/Participants/Party1111111/Party1111111Character.cs) | 攻撃対象と宝箱取得の判断 |
| [AriadneTactics.cs](Assets/Participants/Party1111111/AriadneTactics.cs) | 自部隊の観測から、味方運搬者に近い敵を個人AIへ通知する |

各スクリプトに、処理の目的、判断の理由、調整値の単位を日本語でコメントしてある。

## 導入

Unity **6000.3.24f1** の配布済みSuperDungeonWarsプロジェクトを用意し、4本の `.cs` を `Assets/Participants/Party1111111/` に置く。2日目から更新する場合は `AriadneTactics.cs` も追加する。既存の同名スクリプトがあれば中身を置き換え、Unity側の `.meta` は維持する。

配布プロジェクトに既存のParty1111111のPrefabがある場合は、そのPartyとCharacterのコンポーネントで使用できる。別途構成する場合は、次のように設定する。

1. Character用Prefabへ `Party1111111Character` を付ける。
2. Party用Prefabへ `Party1111111Party` を付け、メンバー用Prefabへ先ほどのCharacterを設定する。
3. `BattlePartyRegistry` の先頭4件の中へParty用Prefabを登録する。
4. `Assets/Dungeon/Scenes/MazeExplore.unity` を開いてPlayし、チーム紹介からEnterまたはSpaceで進める。

`AriadneMap` と `AriadneTactics` はPartyが内部で生成する補助クラスなので、GameObjectへ取り付ける必要はない。チーム表示名AriadneはローカルのParty用Prefabで設定する。`1111111` は仮の受付番号で、正式提出時には主催者指定の番号に合わせる。

Prefab、`.meta`、ゲーム設定、評価用Editorコード、対戦生データはローカルのUnityプロジェクトで管理する。

## 判断の流れ

1. 公開SXG APIから、部隊のメンバー・観測済みマップ・現在見えている宝箱を読む。
2. 宝を持っている仲間は、既知経路で最も近い搬出口へ向かわせる。
3. 手の空いた仲間について、宝箱回収と未知領域の探索の組合せを評価する。
4. 評価が高い「仲間と仕事」の組を順に確定し、同じ宝へ担当が集中するのを防ぐ。
5. 次の隣接セルへ移動を指示し、0.12秒後の思考で観測と担当を更新する。

未観測セルは、観測済みセルの開口から入れる隣の1マスとしてだけ扱う。未知のセルを通り抜ける経路は推測しない。搬出口はゲーム規則にある4辺の中央で、帰還には観測済みで到達可能な入口だけを使う。

宝箱は見失っても位置を記憶するが、近くへ来ても見えない場合、または最後の観測から45秒経った場合に候補から外す。同じセルまで近づいた宝には、観測したワールド座標への移動を指示して接触を狙う。

個人の攻撃は、攻撃可能な候補の中で味方運搬者に近づいた敵を優先し、次に敵の宝箱運搬者を狙う。運搬者への脅威は公開観測から3ワールド単位以内・既知の接続2歩以内で判定する。自分が宝を運んでいる間は搬出を優先する。

移動を伴う近場の護衛・迎撃へは手の空いた仲間を最大1人だけ回す。護衛3歩・敵運搬者への迎撃4歩以内で、追跡期限は2.4秒（0.12秒周期で終了を判定）。終了後2.4秒は次の追跡を始めない。4マス以内で回収し時間内に搬出できる宝がある仲間は、任意の追跡より回収を優先する。敵を見失った場合も回収・探索へ戻る。

## 主な調整値

| 値 | 意味 |
|---|---|
| 思考間隔0.12秒 | 全員の経路探索を毎フレーム繰り返す負荷を抑える |
| 通常宝1、中央宝2.7 | 平均100点・270点から見積もる相対価値 |
| 回収評価の倍率60 | 距離と相対価値で見積もる回収の評価を探索と比較する。2日目に12から調整 |
| 往路0.72秒/マス、復路1.05秒/マス、余裕3秒 | 制限時間内に回収と搬出ができるかを概算する |
| 回収中の前回目標を1.22倍、探索中は1.18倍 | 小さな評価差で目標が頻繁に入れ替わるのを抑える |
| 他の担当目標から3マス以内は探索評価0.25倍 | 探索先を分散させる |

時間の概算は、120秒制、セル間隔2、通常速度3、運搬速度2を前提とする。衝突や敵の妨害を正確に予測するものではなく、各係数は今後調整する。

## 実測結果と今後

同じ2マップ×上下2位置の4試合でAriadneは1位2回、平均432.75点。比較対象TreasureReturnは1位1回、平均401.5点。全8試合の実行エラーは0件だった。

開始位置による差が残り、各条件1試合のため安定した優位性は確認できていない。詳しい条件と限界は [1日目の実測結果](AIExperiments/Ariadne/DAY1_RESULTS.md) を参照。

2日目は回収と探索の優先度を調整した。同じ4マップ・8開始条件で、前日版の平均334.625点・搬出3.000個に対し、採用版は平均502.750点・搬出4.625個。採用版を同じ8条件で再実行し、順位・得点・搬出数がすべて一致した。候補比較を含む計32試合で記録上の実行エラーは0件。

上辺開始など得点が下がった条件もある。全マップで4位置ずつを評価した結果ではなく、候補選定と再測定も同じ条件を使っている。比較条件と悪化した条件を含む詳細は [2日目の実測結果](AIExperiments/Ariadne/DAY2_RESULTS.md) を参照。

3日目は護衛判断と短い迎撃を比較した。初回の計6マップ・12開始条件で、2日目版の平均511.5点・1位6回に対し、採用版は533.5点・1位7回。得点が下がった条件は5条件あり、運搬中のKOも合計24→27回と増えている。護衛の安定した改善は確認できていない。候補比較・追加評価・再実行の計48試合で実行エラー0件。候補選定用8条件と追加評価用4条件を分けた詳細は [3日目の実測結果](AIExperiments/Ariadne/DAY3_RESULTS.md) を参照。

4日目は開始位置と複数マップでの評価・調整・提出準備を進める。
