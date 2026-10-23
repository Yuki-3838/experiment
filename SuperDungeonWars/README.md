# SuperDungeonWars — Ariadne

2026年10月23日から4日間で強化する、4人協調型の財宝回収AI。

- [導入と動作方針](AIExperiments/Ariadne/README.md)
- [1日目の実測結果](AIExperiments/Ariadne/DAY1_RESULTS.md)
- [開発の引き継ぎと2〜4日目の計画](AIExperiments/Ariadne/DEVELOPMENT.md)
- [参加者AIとPrefab](Assets/Participants/Party1111111)
- [Editor専用の対戦評価コード](Assets/Editor/AriadneEvaluation.cs)

配布済みゲーム本体へ同じパスの `Assets` をコピーして使用する。ゲーム本体、外部アセット、Libraryは含めない。Unity 6000.3.24f1で確認。

1日目のAriadneは同じ2マップ×上下2位置の4試合で1位2回、平均432.75点。比較対象TreasureReturnは1位1回、平均401.5点。8試合とも実行エラー0件。開始位置による差が残り、少数の試行なので安定した優位性はまだ確認できていない。
