# Astraeus Helios

SuperTankWars用の独自AIと機体構成です。砲台3基・回転ジョイント1基・装甲67枚、実測コスト319、初期エネルギー1,000で3回出撃します。

## ファイル

| ファイル | 内容 |
|---|---|
| [Astraeus.cs](Astraeus.cs) | 完成版AIスクリプト |
| [Astraeus.prefab](Astraeus.prefab) | 砲台・ジョイント・装甲の配置とAI参照を保存したPrefab |
| [配置数値.md](配置数値.md) | UnityのInspectorに直接入力できる位置・回転・大きさ |
| [検証結果.md](検証結果.md) | 全20組み合わせの勝率 |
| [results/acceptance.json](results/acceptance.json) | 検証した機体のハッシュ・席別成績・合否 |
| [results/helios-v6-validation480.jsonl](results/helios-v6-validation480.jsonl) | 480試合の記録 |
| [stability-fixes.patch](stability-fixes.patch) | 検証環境に適用した共通処理2か所の安定化修正 |

`.meta` ファイルも含めています。Prefab内のスクリプト参照を保つため、スクリプト・Prefabと一緒にコピーしてください。

## Unityへの配置

Unity 6000.3.24f1の元のSuperTankWarsプロジェクトで使用します。

1. このフォルダーの `Astraeus.cs`、`Astraeus.cs.meta`、`Astraeus.prefab`、`Astraeus.prefab.meta` を `Assets/Participant/Astraeus/` にコピーします。
2. リポジトリ直下の `Astraeus.meta` を `Assets/Participant/Astraeus.meta` にコピーします。
3. ゲームの参加者設定に `Astraeus.prefab` を登録します。

元のプロジェクトの `TurretPart`、`RotJointPart`、共有マテリアルとゲームスクリプトを使用します。手動で作る場合は [配置数値.md](配置数値.md) を参照してください。

```text
Astraeus                         ← Astraeus.Astraeus を付ける
├─ AstraeusFireControlRing        ← RotJointPart
│  ├─ AstraeusGun_0               ← TurretPart
│  ├─ AstraeusGun_1               ← TurretPart
│  └─ AstraeusGun_2               ← TurretPart
└─ 装甲Cube × 67
```

AIの `m_turrets` には Gun_0、Gun_1、Gun_2 の順に3基、`m_rotJoints` には FireControlRing を1基登録します。保存済みPrefabには登録済みです。

## AIの動作

- 観測した敵の速度と旋回を推定し、各砲口からの弾道で偏差射撃します。
- 露出した急所と射線を考慮して標的を選び、短時間の標的固定で照準を安定させます。
- 敵の予測位置へ回転ジョイントを向け、車体の旋回中も砲口の射線を確保します。
- 障害物の探索と停滞時の脱出、接近弾と近くの敵を考慮した回避を行います。
- 相手の型名や検証用シードへの分岐はありません。

## 検証条件

SXG_PlayerTanksの6機から相手3機を選ぶ通常4人戦で、全20組み合わせを各24試合実行しました。自機の4席と相手の6通りの並びをすべて使用し、得点の単独1位を勝利として数えています。検証前に機体・AI・480個の未使用シードを固定しました。

**480戦468勝（97.5%）。全20組み合わせで70%以上、最低は21/24勝（87.5%）。中断・エラーは0件です。** この有限の検証試合での実測値です。

全参加機に共通する物理処理の安定化も2点適用しています。

1. 厚さ0の装甲が原因でPhysXがクラッシュする問題に対し、凸形状を作る前に各軸の大きさを最小0.001に補正します。コストの計測後に適用します。
2. 同じ物理ステップ中の追加入力で、撃破済み戦車の装甲を二重に分離しないようにします。

元のUnityプロジェクト直下から `git apply --check /path/to/stability-fixes.patch` で適用可否を確認し、未適用なら `git apply /path/to/stability-fixes.patch` で適用できます。相手AIと相手Prefabのファイルは変更していません。

## 対戦検証時点のファイルとSHA-256

AIスクリプトには、検証後に各処理の意図・数値の単位・判断条件の日本語コメントを追加しています。実行処理は同じですが、コメントの追加で現在の `.cs` のファイルハッシュは変わっています。

以下は対戦に使った原本のハッシュです。原本は [検証時点のコミット](https://github.com/Yuki-3838/experiment/tree/9cd39c103530f5e3bc056842e9c3bf62e4304248/Astraeus) に保存しています。`results/acceptance.json` も検証時点の記録です。Prefabの内容は同じです。

```text
Astraeus.cs
C1B7BCFEECE89E5030BD84136D9573C489881E6574878E9E45E35892FE90C249

Astraeus.prefab
6D1C943BEAF87861BA8DE9F99C6108B49BA8028454697FDC5F756D6EA3723BE5
```
