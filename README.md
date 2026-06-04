# EDCBViewer

別マシン（視聴PC）から EDCB の録画済みファイルと予約一覧を閲覧し、MPC-BE で再生するための WPF アプリです。

## 動作環境

- .NET 9 Desktop Runtime（Windows）
- [mezakinoyakata/EDCB](https://github.com/mezakinoyakata/EDCB)（[xtne6f/EDCB](https://github.com/xtne6f/EDCB) fork）が稼働している録画機
- MySQL サーバー（録画インデックス・EPG DB 共用）
- 録画機との SMB 共有（UNC パスでアクセス）
- MPC-BE（外部プレイヤー）

## 機能

- **録画済み一覧**（MySQL `recordings` テーブルから取得）
  - 番組名・放送局・日時・録画時間・ドロップ数・ステータスを表示
  - 番組情報をキャッシュ・EPG DB から取得して右ペインに表示
  - ダブルクリックまたは再生ボタンで MPC-BE 起動
  - タイトル・放送局・番組情報・日付によるフィルタ検索（正規表現対応）
  - 列クリックでソート、ページング対応
- **予約一覧**（EMWUI API から取得）
  - 番組名・放送局・日時・ステータスを表示
  - ダブルクリックで対応する録画ファイルを再生
  - 録画中の番組は追っかけ再生を試みる
- **ディレクトリタブ**
  - 指定フォルダ内の `.ts` / `.m2ts` / `.mp4` を閲覧・再生
  - MySQL `recordings` テーブルと照合して番組情報・ドロップ数を表示

## セットアップ

1. `dotnet build -c Release` でビルドし `bin\Release\net9.0-windows\` を配置
2. `EDCBViewer.exe` を起動
3. メニューの「設定」で以下を設定

| 項目 | 説明 |
|------|------|
| EMWUI URL | 例: `http://recserver:5510` |
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
| F5 | 更新 |
| Ctrl+1 / 2 / 3 | 録画済み / 予約録画 / ディレクトリ タブ切替 |
| PageDown / PageUp | 次/前ページ |
| Home / End | 先頭/末尾ページ |
| Enter | ファイル再生 / フォルダ移動 |
| ↑ / ↓ | 選択移動 |
