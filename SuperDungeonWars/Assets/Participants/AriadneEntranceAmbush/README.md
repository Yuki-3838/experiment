# Ariadne Entrance Ambush（独立した待ち伏せAI）

通常の `Party0004838` とは別に選ぶ実験用AI。**1人が宝箱を回収し、残り3人は自陣以外の3入口を1人ずつ守る。**

## 必要なスクリプト

このフォルダーのC#5本と、[Common/C4SymmetricMap.cs](../Common/README.md)の共通クラス1本、計6本を使用する。共通クラスは同じプロジェクトへ1回だけ置く。

- `AriadneEntranceAmbushParty.cs`：担当割当、入口への開拓・移動・待機・迎撃、回収担当の判断。
- `AriadneEntranceAmbushCharacter.cs`：攻撃候補・宝箱取得・搬出イベントの判断。
- `AriadneAmbush.cs`：入口・配置場所・切り替え時間。
- `AriadneMap.cs`：公開観測からの経路探索。
- `AriadneTactics.cs`：このAIのPartyとCharacterだけの連絡帳。

補助クラス3本と共通クラスはアタッチ不要。PartyとCharacterを別のGameObjectのPrefabへアタッチし、Partyの `Member Prefabs` へこのCharacterのPrefabを指定する。配布プロジェクトのキャラクター用Prefabを複製し、AIのコンポーネントだけを交換すれば、必要な見た目の参照を保持できる。同じPrefabへ通常版と待ち伏せ版のParty/Characterを同時に付けない。

`BattlePartyRegistry` へ待ち伏せ版PartyのPrefabを登録する。ローカル環境ではPrefabと別Registryを作成済み：`Assets/Participants/Settings/AriadneEntranceAmbushRegistry.asset`。シーンのBattlePrototypeGameControllerの `Battle Party Registry` をこちらへ切り替えると先頭チームが待ち伏せ版になる。通常のRegistryは回収版のまま。

## 設定と状態表示

- **敵が来ない時に近場回収へ切り替える：初期OFF**。入口到着後の待機を続ける。
- ONなら、入口で8ゲーム内秒待って敵運搬者が来なければ、近場回収・探索へ8秒切り替えて戻る。秒数は変更可能。
- OFFでも、可視運搬者への迎撃、敵が落とした宝の回収、持っている宝の搬出は行う。
- 実行中のPartyの `現在の担当` で、入口への経路探索中・移動中・待機中などを確認できる。
- 担当入口はメンバーごとに固定。自陣から時計回りに1・2・3つ先を割り当て、それぞれの入口から1マス内側へ配置する。迎撃・一時回収の範囲も各自の入口が中心。

経路は公開観測と共通C4クラスの回転推定から計画する。完全未知の部分は到着前の開拓が必要。敵や宝箱の現在位置は推定しない。実測と矛盾すれば推定を撤回する。

2026-10-28、ローカルのシーンはこの別Registryを選択済み、待ち伏せPrefabの一時回収はOFFへ変更済み。Play中なら一度停止してから再生する。PrefabやシーンはGitへ含めない。迎撃は入口そのものから2マス以内。押されたら、公開観測から決めた入口の待機位置へ戻る。

## 確認範囲

現在の3入口分担配置は、Unity 6000.3.27f1の隔離プロジェクトでコンパイルのみ確認。再生・物理待機・対戦比較は未実施。[分担配置の変更点](../../../AIExperiments/Ariadne/AMBUSH_SPREAD_CHANGES.md)。

以前の1入口集中版では、共通地形クラスの12項目と、実際の移動・視界・衝突を使う1マップ×4開始方向の押し戻し後12秒待機を確認していた。これは今回の分担配置の動作確認ではない。以前の条件・Editor検索インデックス例外は [前版の記録](../../../AIExperiments/Ariadne/C4_HOLD_CHANGES.md) を参照。正式提出用の検査・ZIP更新は未実施。

Gitへ含めるのはC#と説明Markdownのみ。Prefab・meta・Registryはローカル専用。
