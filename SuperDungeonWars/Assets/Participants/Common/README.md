# C4SymmetricMap — どの参加者AIからも使える地形推定

`C4SymmetricMap.cs` 1本で使う共通クラス。Unityと配布された公開参加者APIを使う。Ariadneの型、ゲーム内部のクラス、Reflection、生成シードには依存しない。GameObjectへアタッチする必要はない。

## 導入

通常は `Assets/Participants/Common/` へこのC#を1回だけ配置する。各Partyがそれぞれインスタンスを作り、自分の `SXG_GetKnownMap()` を渡す。同じプロジェクトに同じC#を複数コピーすると型が重複するため、通常版と待ち伏せ版はこの1本を共有する。インスタンスや観測結果は共有しない。

受付番号ごとのフォルダーだけをZIPにする提出形式では、Commonが自動で含まれるとは限らない。提出時はこのC#を提出先フォルダー内へ**移動**するか、そのフォルダーだけの別プロジェクトへ1本コピーし、依存ファイル込みでコンパイル・提出前検査を行う。共通元を残した同一プロジェクトへ同じ型を重複配置しない。今回、提出ZIPは更新していない。

## 既存のPartyへの組み込み例

既存の `ComPartyBase` 継承クラスへ、次のフィールドとメソッドを加える。自分の判断処理で決めた目標セルを `MoveUsingSymmetry` へ渡す。

```csharp
// ファイル先頭へ追加する。
using SuperDungeonWars.AI;

// 以下は既存のComPartyBase継承クラスの中へ置く。
private C4SymmetricMap terrain;

private void MoveUsingSymmetry(int memberIndex, Vector2Int goal)
{
    KnownMapView observed = SXG_GetKnownMap();
    if (observed == null) return;
    if (terrain == null)
        terrain = new C4SymmetricMap(observed.Width, observed.Height);
    terrain.Refresh(observed);

    if (!SXG_TryGetMemberInfo(memberIndex, out PartyMemberInfo member)
        || member.IsKnockedOut || member.IsActionLocked) return;

    // 最初の1セルだけを命令し、次の判断で観測・現在位置を更新する。
    if (terrain.TryGetNextStep(member.CurrentCell, goal, out Vector2Int next)
        && next != member.CurrentCell)
        SXG_SetMemberMoveTargetCell(memberIndex, next);
    else
        SXG_StopMember(memberIndex);
}
```

複数人を動かす場合は、Refreshを部隊の思考で1回だけ行い、各人の経路を順に求めてもよい。探索・回収・迎撃の目標を決める機能は含まないため、それぞれのAIの戦略をそのまま使える。

## 主なAPI

| API | 内容 |
|---|---|
| `Refresh(KnownMapView)` | 新しい公開観測を取り込む。変化があればtrue |
| `TryGetCell(cell, out SymmetricCell)` | 実測・推定・未知の区別、開口、部屋属性、推定元を取得 |
| `IsObserved / IsKnown / IsWalkable` | 実測のみ／実測と推定／通行可能を区別して判定 |
| `CanMove(from, to)` | 隣接セルの両側の開口を確認。推定地形も使う |
| `TryGetOpenNeighbor(from, direction, out neighbor)` | 元セルの開口から隣を取得。隣が完全未知でも返せる |
| `TryGetNextStep(start, goal, out next)` | 実測・推定地形による最短経路の最初の1歩。到着済みならstartを返す |
| `TryGetNextStep(..., allowUnknownGoal: true)` | 完全未知の目標を最後の1セルにだけ許可 |
| `SetInferenceEnabled(false)` | 次のRefreshから観測情報だけを使う |
| `InferenceActive / HasSymmetryConflict` | 推定が有効か、実測との矛盾が発生したか |
| `ObservedCellCount / InferredCellCount / Revision` | 状態表示や再計画に使える件数・更新番号 |
| `RotateClockwise / RotateDirectionsClockwise` | 座標と開口方向を時計回りに回す静的関数 |

`SymmetricCell.Knowledge` は `Unknown / Observed / Inferred`。`SourceCell` は推定元の実測セル。推定先の `RoomId` は必ず-1で、離れた部屋へ同じIDを捏造しない。宝箱・敵・味方のデータは扱わない。元のKnownMapViewのIsObservedも変更しない。

## 推定の前提

奇数サイズの正方形で、90度回転対称の地形を想定する。41×41なら時計回り90度は `(x,y) → (y,40-x)`、開口は北→東→南→西。中央の行・列も同じ回転処理へ含める。四分割から中央の行・列を除いた区画だけでは、その行・列の未知情報までは復元できない。

新しい観測が開口・部屋属性・中央ホール属性と食い違ったら、推定を全部消し、その試合のインスタンスでは観測情報だけへ切り替える。食い違いを観測する前の推定が正しいことを保証する機能ではない。対称でないステージでは最初から推定をOFFにする。

地形が試合中に不変である配布版を対象とし、観測済み地形はキャッシュする。新しい試合や動的に地形が変わる環境ではインスタンスを作り直す。各インスタンス内の探索用配列を再利用するため、同じインスタンスを並列で呼び出さない。

## 確認範囲

Unity 6000.3.27f1で12項目の確認を実施。方向と座標の回転、実測との区別、元マップの不変性、中央の行・列、矛盾時の撤回、OFF/ON、長方形、未知終点・壁・境界の経路処理、両Ariadneの接続を確認した。事前審査済み4マップでは、中央の行・列を含む21×21セルの観測から全41×41セルの地形が一致し、別入口への経路が実際の壁を横切らないことを確認した。得点・勝率の測定ではない。
