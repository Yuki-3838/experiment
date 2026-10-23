# Ariadne — SuperDungeonWars AI

4人の探索と財宝の搬出を協調させる参加者AI。2026年10月23日から26日までの4日間で段階的に改善する。

## 導入

このフォルダーのGit保存先は `Yuki-3838/experiment/SuperDungeonWars`。配布済みのSuperDungeonWarsプロジェクトへ、保存された `Assets` 以下を同じ場所にコピーする。ゲーム本体や市販アセットは含めない。

- Unity: **6000.3.24f1**。この環境はURP/Core 17.3.0を使用。
- シーン: `Assets/Dungeon/Scenes/MazeExplore.unity`。
- 参加チームの表示名: **Ariadne**。
- `Assets/Participants/Settings/BattlePartyRegistry.asset` の先頭がAriadne、残りはAggressive、Strategic、Convoy。Playで通常の対戦を開始する。
- `Party1111111` は元からある仮の受付番号。正式な提出時には主催者指定の実際の番号への置換が必要。

### Windowsの起動問題

長い展開先ではPackageCache内の `.urtshader` がWindowsのパス長制限に当たり、`Host type is not matching any asset type` が出ていた。この環境では短い `P:\` 経由で再インポートして解消した。Pドライブはログインし直すと消えるので、その場合は短いフォルダーへ置くか、次のように作り直す。

```powershell
subst P: 'C:\Users\yu04k\Downloads\SuperDungeonWars-ProgrammingBattle-main\SuperDungeonWars-ProgrammingBattle-main\SuperDungeonWars'
& 'C:\Program Files\Unity\Hub\Editor\6000.3.24f1\Editor\Unity.exe' -projectPath P:\
```

Pドライブが別の用途で使われている場合は、その割り当てを上書きしない。通常の作業用の短い参照先は、外側フォルダーの `UnityProject` ジャンクション。

ピンク表示については、Graphics SettingsのDefault Render Pipelineを配布済みの `Assets/Settings/PC_RPAsset.asset` に設定済み。

さらに展開漏れのあったMixamoのアニメーション、関連モデル、無料素材などを公式配布元からローカルへ復元し、TagManagerのMazeWallとBattlePreviewStage0〜3も復元した。搬出は `Y Bot@Throw In.fbx` 内の `OnDeliveryThrowRelease` イベントで確定するため、このアニメーションが欠けると移動できても得点にならない。これら外部素材はこのGitリポジトリへの再配布対象に含めない。

## 1日目の方針

- 公開 `SXG_` APIで取得した既知マップ、メンバー、可視宝箱のみを使用。
- 既知セルの接続をBFSで探索。未観測セルは経路の終点にだけ使う。
- メンバーと仕事の組合せを評価し、宝箱の担当の重複を防ぐ。探索先は近いものへ集中しすぎないよう分散。
- 宝箱の価値の目安、到達距離、搬出距離、残り時間で回収を判断。
- 宝を持つと、既知経路で到達できる4辺中央の入口のうち最短の入口へ帰る。
- 見失った宝箱の位置を最大45秒記憶し、近くを再確認して無ければ破棄。
- 同じセル内の宝にはワールド座標で近づく。移動の指示は隣接セルごと。
- 攻撃は運搬中の敵に対する近接の機会だけを使う。

宝の目安の価値はSmallChestを1、CentralChestを2.7とする。設定の平均100点・270点に対応する。制限時間120秒、セル間隔2、通常速度3、運搬速度2を前提に時間を概算する。これらが変更された大会では調整が必要。

## 対戦記録の再生成

`Assets/Editor/AriadneEvaluation.cs` はEditor専用で、提出対象から除く。Unityメニュー `Ariadne > Run day 1 comparison (8 matches)` から実行する。現在のシーンは保存してから実行する。

同じマップシードと出現位置で、AriadneとTreasureReturnを、それぞれAggressive・Strategic・Convoyと比較する。2シード×2位置×2方式=8試合。ゲーム側の120秒判定、物理演算、宝の取得・搬出・採点処理をそのまま使う。測定中だけカメラの描画を止め、時間を1フレーム0.02秒、物理も0.02秒に固定する。

配布版のMazeGeneratorは設定の `m_seed` を使わず、事前承認済みテーブルから毎回ランダムに選ぶ。このため評価用Editorコードが測定中だけそのテーブルを承認済みの1シードに固定し、終了時に元へ戻す。ソースは変更しない。使用する2シードは `1041505397` と `1013712272`。生成ログの実際のシードも照合し、各JSONの `actualSeed` に記録する。参加者AIにはこの情報を渡さない。

`AIExperiments/Ariadne/results` に各試合の生データ、途中経過、完了印を出す。既存の `day1-00.json`〜`day1-07.json` は同名で更新される。履歴が必要なら実行前に別のフォルダーへ保存する。

時間の固定はUnityの [Time.captureDeltaTime](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Time-captureDeltaTime.html) を使用。CPU・物理演算による差まで含めた完全な決定性は保証しない。少数のサンプル対戦だけで最強・大会優勝を保証できるものではない。

開発状況と次の作業は [DEVELOPMENT.md](DEVELOPMENT.md) に記録する。
