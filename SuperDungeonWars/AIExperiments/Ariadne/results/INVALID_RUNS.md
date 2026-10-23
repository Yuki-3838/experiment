# 修復前の試行

`invalid-missing-animation/` と `invalid-missing-clips/` は不足素材が残っていた状態での動作調査。全チームの点数が0だった。

- 最初はAnimatorControllerとアニメーションそのものが欠落していた。ログにはAnimator警告と、BattlePreviewStageレイヤー不足のエラーが出ていた。
- Controllerだけを復元しても搬出のイベントを持つFBXクリップが無いため、搬出点は確定しなかった。
- その後、公式配布のMixamo一式とTagManagerを復元し、評価をやり直した。

これらの試行をAIの勝率・点数比較に使ってはいけない。大量の重複警告を含むUnityログはGitへ含めない。

## マップを対にできていなかった試行

`unmatched-random-maps/` は素材修復後の正常に遊べる8試合だが、配布版MazeGeneratorが設定シードを無視していた。各試合の `seed:12345/24680` は設定へ渡した値であり、実際に生成されたマップのシードではない。Ariadne対TreasureReturnの同じ条件での比較には使わない。主結果は `actualSeed` を照合してから改めて記録する。
