# EDCBViewer

別マシン（視聴PC）から録画済みファイルを閲覧・再生し、EPG を全文検索するための WPF アプリです。

## 動作環境

- .NET 9 Desktop Runtime（Windows）
- [mezakinoyakata/EDCB](https://github.com/mezakinoyakata/EDCB)（[xtne6f/EDCB](https://github.com/xtne6f/EDCB) fork）が稼働している録画機
- MySQL サーバー（EPG DB）
- 録画機との SMB 共有（UNC パスでアクセス）
- MPC-BE（外部プレイヤー）

## 機能

- **ディレクトリタブ**
  - 指定フォルダ内の `.ts` / `.m2ts` / `.mp4` を閲覧・再生
  - ファイル選択時に MySQL `events` テーブルから番組情報を取得して右ペインに表示
  - 列クリックでソート、ページング対応
- **EPG 全文検索**
  - 絞込ボックスにキーワードを入力して **Enter** で MySQL FULLTEXT 検索（ngram パーサー）
  - `program_guide` ビュー（events JOIN services）を対象に `event_name` / `short_text` / `ext_text` を検索
  - 結果を番組名・放送局・放送日時で一覧表示、選択すると番組詳細を右ペインに表示
  - 絞込ボックスをクリアするとファイルブラウズモードに戻る

## セットアップ

1. `dotnet build -c Release` でビルドし `bin\Release\net9.0-windows\` を配置
2. `EDCBViewer.exe` を起動
3. メニューの「設定」で以下を設定

| 項目 | 説明 |
|------|------|
| MySQL 接続文字列 | 例: `Server=recserver;Database=edcbviewer;Uid=edcb;Pwd=xxx` |
| 録画フォルダ | 例: `\\recserver\d\PT2`（追っかけ再生用） |
| MPC-BE パス | 例: `C:\app\MPC-BE\mpc-be64.exe` |
| エンコード済みフォルダ | ディレクトリタブの起点フォルダ |

設定は `%LOCALAPPDATA%\EDCBViewer\settings.json` に保存されます。

## ビルド

```
dotnet build -c Release
```

## キーボードショートカット

| キー | 動作 |
|------|------|
| F5 | フォルダ再読込 |
| PageDown / PageUp | 次/前ページ |
| Home / End | 先頭/末尾ページ |
| Enter（リスト選択中） | ファイル再生 / フォルダ移動 |
| Enter（絞込ボックス） | EPG 全文検索実行 |
| ↑ / ↓ | 選択移動 |
