# Ariadne Entrance Ambush（独立した待ち伏せAI）

通常の `Party0004838` とは別に選ぶ実験用AI。**1人が宝箱を回収し、3人は同じ敵入口を守る。**

## 必要なスクリプト

このフォルダーのC#5本をまとめて使用する。

- `AriadneEntranceAmbushParty.cs`：担当割当、入口への開拓・移動・待機・迎撃、回収担当の判断。
- `AriadneEntranceAmbushCharacter.cs`：攻撃候補・宝箱取得・搬出イベントの判断。
- `AriadneAmbush.cs`：入口・配置場所・切り替え時間。
- `AriadneMap.cs`：公開観測からの経路探索。
- `AriadneTactics.cs`：このAIのPartyとCharacterだけの連絡帳。

補助クラス3本はアタッチ不要。PartyとCharacterを別のGameObjectのPrefabへアタッチし、Partyの `Member Prefabs` へこのCharacterのPrefabを指定する。配布プロジェクトのキャラクター用Prefabを複製し、AIのコンポーネントだけを交換すれば、必要な見た目の参照を保持できる。同じPrefabへ通常版と待ち伏せ版のParty/Characterを同時に付けない。

`BattlePartyRegistry` へ待ち伏せ版PartyのPrefabを登録する。ローカル環境ではPrefabと別Registryを作成済み：`Assets/Participants/Settings/AriadneEntranceAmbushRegistry.asset`。シーンのBattlePrototypeGameControllerの `Battle Party Registry` をこちらへ切り替えると先頭チームが待ち伏せ版になる。通常のRegistryは回収版のまま。

## 設定と状態表示

- **敵が来ない時に近場回収へ切り替える：初期OFF**。入口到着後の待機を続ける。
- ONなら、入口で8ゲーム内秒待って敵運搬者が来なければ、近場回収・探索へ8秒切り替えて戻る。秒数は変更可能。
- OFFでも、可視運搬者への迎撃、敵が落とした宝の回収、持っている宝の搬出は行う。
- 実行中のPartyの `現在の担当` で、入口への経路探索中・移動中・待機中などを確認できる。

開始時点で敵入口への経路は未知の場合があり、到着前には開拓が必要。入口へ瞬時に移動するAIではない。現在は観測した通路だけで経路を作り、回転対称による地形推定は未実装。

## 確認範囲

Unity 6000.3.27f1の別の最小プロジェクトでコンパイルと12項目の命令・タイマー確認を通過。既知経路の条件で全4方向から3人が配置へ進み、到着後100判断の停止命令を確認した。物理挙動・衝突・戦闘を含む対戦、120秒以内の到着や勝率は未確認。通常の受付番号0004838の提出AIとは別の実験名で、提出用検査は未実施。

Gitへ含めるのはC#と説明Markdownのみ。Prefab・meta・Registryはローカル専用。
