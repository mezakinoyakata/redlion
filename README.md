# EDCBViewer

録画機（[mezakinoyakata/EDCB](https://github.com/mezakinoyakata/EDCB)）が MySQL に書き出した EPG データを参照し、録画済みファイルの閲覧・再生と番組の全文検索を行う Windows WPF アプリです。

## 動作環境

- Windows 10 / 11
- .NET 9 Desktop Runtime
- MySQL サーバー（録画機側で稼働）
- 録画機との SMB 共有（ファイル再生に使用）
- MPC-BE（外部プレイヤー）

## 機能

### ディレクトリブラウザ

- 設定したフォルダ以下の `.ts` / `.m2ts` / `.mp4` を一覧表示
- サブフォルダへの移動、アドレスバーへの直接入力
- ファイル選択時にファイル名を解析してタイトル・放送局・放送日時を即時表示
- 放送局と開始時刻で MySQL `events` テーブルを検索し番組情報（short_text + ext_text）を右ペインに表示
- ダブルクリックまたは Enter キーでファイルを MPC-BE で再生
- 列ヘッダークリックでソート（ファイル名・放送局・放送日時）、ページング対応

### 絞込とEPG全文検索

| 操作 | 動作 |
|------|------|
| 絞込ボックスに入力 | ファイル名（タイトル・放送局）をリアルタイムフィルタ |
| 絞込ボックスで **Enter** | キーワードで EPG 全文検索を実行（下記参照） |
| 絞込ボックスをクリア | ファイルブラウズモードに戻る |

**EPG 全文検索の仕様**

- MySQL FULLTEXT（ngram パーサー、2文字以上）で `event_name` / `short_text` / `ext_text` を検索
- 対象: `program_guide` ビュー（`events JOIN services`）
- 結果を放送日時降順で最大 200 件表示
- 結果行を選択すると右ペインに番組情報を表示

## セットアップ

1. `dotnet build -c Release` でビルド
2. `bin\Release\net9.0-windows\EDCBViewer.exe` を起動
3. メニュー「設定 → パス設定...」で以下を入力して保存

| 設定項目 | 内容 |
|----------|------|
| 表示件数 | 1 ページあたりの最大表示件数（デフォルト 500） |
| プレイヤー | MPC-BE の実行ファイルパス（例: `C:\ap\MPC-BE\mpc-be64.exe`） |
| フォルダ | ディレクトリブラウザの起点フォルダ（例: `\\5600x\d\encoded`） |
| MySQL 接続文字列 | 例: `Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=xxx` |

設定ファイル: `%LOCALAPPDATA%\EDCBViewer\settings.json`

## キーボードショートカット

| キー | 動作 |
|------|------|
| F5 | フォルダ再読込 |
| Enter（リスト選択中） | ファイル再生 / フォルダ移動 |
| Enter（絞込ボックス） | EPG 全文検索実行 |
| ↑ / ↓ | リスト選択移動 |
| PageDown / PageUp | 次 / 前ページ |
| Home / End | 先頭 / 末尾ページ |
| 印字可能文字（リスト選択中） | 絞込ボックスにフォーカスして入力 |

## MySQL スキーマ（EPG DB）

EpgTimerSrv が EPG ロード完了時に自動書き出しする。EDCBViewer は読み取り専用。

主なテーブル・ビュー:

| 名前 | 種別 | 内容 |
|------|------|------|
| `events` | テーブル | 番組情報（event_name, short_text, ext_text 等）|
| `services` | テーブル | サービス情報（service_name, remote_control_key 等）|
| `program_guide` | ビュー | `events JOIN services`（全文検索の対象）|
| `upcoming` | ビュー | `program_guide` のうち放送開始が未来の番組 |

FULLTEXT インデックス: `ft_event (event_name, short_text, ext_text) WITH PARSER ngram`

## ビルド

```
dotnet build -c Release
```
