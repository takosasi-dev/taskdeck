# Changelog

主な変更点です。形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/)、版の付け方は [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [0.1.1] - 2026-09-25

プレリリース。中身は 0.1.0 と同じで、配布の形だけを変えた。

### Changed

- 配布の exe を圧縮しない形にした。起動が約 0.5 秒速くなる（1万件のデータで 1.9→1.4 秒。起動画面は 0.8→0.4 秒で出る）。そのかわり exe は約 110MB→285MB になる

## [0.1.0] - 2026-09-25

最初の公開版（プレリリース）。

### Added

- クイック入力（`Ctrl+Shift+Space`）: 1行から期限・時刻・優先度・プロジェクト・タグ・繰り返しを読み取る
- メイン画面: 今日・予定・すべて・完了済み・ゴミ箱、プロジェクト・タグ・保存した絞り込み、検索、コマンドパレット（`Ctrl+K`）
- サブタスク（3段まで）、繰り返し、リマインド（Windows の通知）、テンプレート、使い捨てリスト
- カレンダー（月・週。祝日と天気）、振り返り、フォーカスモード
- JSON・CSV・Markdown の入出力、起動時のバックアップ（5世代）
- タスクトレイへの常駐、グローバルホットキー、ログオン時の自動起動、初回起動の案内
- 更新の確認（GitHub Releases の正式版を1日1回。設定の「TaskDeck について」に出すだけ）

[0.1.1]: https://github.com/takosasi-dev/taskdeck/releases/tag/v0.1.1
[0.1.0]: https://github.com/takosasi-dev/taskdeck/releases/tag/v0.1.0
