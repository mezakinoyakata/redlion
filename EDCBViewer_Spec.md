# EDCBViewer 仕様書

## 概要

EDCB（EpgTimerSrv）と連携する Windows WPF アプリケーション。  
録画済み一覧・予約一覧の閲覧、番組情報表示、録画ファイル再生を提供する。

---

## プロジェクト情報

| 項目 | 内容 |
|---|---|
| 場所 | `C:\work\CC\EDCBViewer` |
| フレームワーク | .NET 9.0 / WPF / Windows |
| 依存パッケージ | MySqlConnector 2.4.0 |
| ビルド | `dotnet build -c Release`（`dotnet publish` は使わない、`-c Release` 必須） |

---

## ファイル構成

```
EDCBViewer/
├── MainWindow.xaml / .cs      メインウィンドウ（録画・予約タブ、ディレクトリタブ）
├── SettingsWindow.xaml / .cs  設定ダイアログ
├── AppSettings.cs             設定管理（JSON永続化）
├── Models/
│   ├── EpgEvent.cs            EPGイベントモデル
│   ├── RecFileInfo.cs         録画済み情報モデル
│   ├── ReserveData.cs         予約情報モデル
│   ├── MediaFile.cs           ディレクトリタブ用メディアファイル（IsDirectory / DisplayName を含む）
│   └── RecordingIndexEntry.cs 録画インデックスエントリ
├── Parsers/
│   ├── EmwuiClient.cs         EMWUI HTTP API クライアント
│   ├── EpgBinaryParser.cs     *_epg.dat バイナリパーサー（現在は使用停止予定）
│   ├── EncodingDetector.cs    文字コード検出
│   ├── RecInfoParser.cs       RecInfo テキストパーサー
│   └── ReserveParser.cs       Reserve テキストパーサー
└── Services/
    ├── RecordingIndex.cs      録画インデックスDB管理（SQLite）
    └── PlayServer.cs          ローカル再生HTTPサーバー
```

---

## 設定項目（AppSettings）

| プロパティ | 説明 | デフォルト |
|---|---|---|
| EmwuiBaseUrl | EpgTimerSrv の EMWUI URL | `""` |
| MaxRecItems | 録画済み一覧の取得上限件数 | 500 |
| RecordingFolder | 録画フォルダ（UNCパス等） | `\\5600x\d\PT2` |
| PlayerPath | 動画プレイヤーのパス | MPC-BE のパス |
| RefreshIntervalSeconds | 自動更新間隔（現在は手動更新のみ） | 60 |
| PlayServerPort | 再生サーバーポート | 5580 |
| EncodedFolder | エンコード済みフォルダ（ディレクトリタブ） | `""` |
| DbConnectionString | PostgreSQL 接続文字列（録画サーバー上のDB） | `""` |

設定ファイルパス: `%LOCALAPPDATA%\EDCBViewer\settings.json`

---

## データ取得フロー（現在）

### 録画済み一覧
```
EmwuiClient.GetRecFileInfoAsync()
  → GET /api/EnumRecInfo?count=N&index=M
  → XML解析 → List<RecFileInfo>
```

### 予約一覧
```
EmwuiClient.GetReserveInfoAsync()
  → GET /api/EnumReserveInfo?count=500&index=M（バッチ取得）
  → XML解析 → List<ReserveData>
```

### 番組情報（録画済み選択時）
```
EmwuiClient.GetProgramInfoTextAsync(id)
  → GET /api/EnumRecInfo?id=N
  → programInfo フィールド（プレーンテキスト）を抽出
  → メモリキャッシュ（_programInfoCache）に保存
```

### 番組情報（予約選択時）← **DB移行予定**
```
【現在】
EmwuiClient.GetEventInfoTextAsync(onid, tsid, sid, eid)
  → GET /api/EnumEventInfo?basic=0&id=ONID-TSID-SID-EID
  → event_text + event_ext_text を返す

【予定】
EpgData.db の events テーブルを ONID/TSID/SID/EventID で検索
  → short_text + ext_text を返す
  → EMWUI 接続不要・オフライン参照可
```

---

## SQLiteデータベース

### PostgreSQL DB（録画サーバー上）
- 接続: `DbConnectionString` 設定（例: `Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=pass`）
- **recordings テーブル** — 管理クラス: `Services/RecordingIndex.cs`、EDCBViewer が read/write
- **events テーブル** — 管理クラス: `Services/EpgDbReader.cs`、EDCBViewer は read-only
  - EpgTimerSrv 側（EpgSqliteExporter）が PostgreSQL に書き出す必要あり（別途対応）

#### recordings テーブル主要カラム
| カラム | 型 | 説明 |
|---|---|---|
| file_name | TEXT PK | ファイル名（拡張子なし） |
| full_title / series_title / episode_number | TEXT / INTEGER | タイトル情報 |
| start_time / start_time_epg / duration_second | TEXT / BIGINT | 放送日時・時間 |
| onid / tsid / sid / event_id | BIGINT | EPG識別子 |
| program_info / drops / scrambles | TEXT / BIGINT | 番組情報・録画品質 |

---

## ディレクトリタブ

エンコード済みフォルダ（`EncodedFolder` 設定）を起点にフォルダを移動しながら `.mp4` ファイルを閲覧・再生する。

### アドレスバー
- **パスボックス**（編集可能）: 現在のフォルダパスを表示。直接入力して Enter で移動。存在しないパスは拒否して元に戻す
- **… ボタン**: `OpenFolderDialog` を表示して任意フォルダへ移動。`EncodedFolder` 外を選択した場合は新しいルートとして設定

### フォルダナビゲーション
- カレントディレクトリのサブフォルダと `.mp4` ファイルのみ表示（再帰列挙なし）
- サブフォルダは `📁 フォルダ名` で常にリスト上部に表示
- ダブルクリックまたは Enter キーでサブフォルダに移動、ファイルは再生
- 検索ボックスはカレントディレクトリのファイルのみフィルタ（フォルダは常時表示）

### MediaFile モデル（`Models/MediaFile.cs`）
| プロパティ | 内容 |
|---|---|
| `IsDirectory` | フォルダエントリかどうか |
| `DisplayName` | フォルダ: `📁 フォルダ名`、ファイル: `ParsedTitle` |

---

## 他プロジェクトとの連携

### EpgTimerSrv（C++）
- EMWUI HTTP API を通じて録画済み・予約情報を取得
- **EPG DB（EpgData.db）**: EpgTimerSrv が書き出す SQLite を EDCBViewer が参照する（移行予定）

### EDCBEpgImporter（C#）
- `C:\work\CC\EDCBEpgImporter`
- *_epg.dat を読んで同一スキーマの SQLite に書き出すスタンドアロンツール
- EpgTimerSrv 組み込み版（EpgSqliteExporter）と同じスキーマ

---

## 移行予定: EMWUI EPG → DB参照

### 対象
`ReserveList_SelectionChanged` 内の `GetEventInfoTextAsync` 呼び出し

### 変更内容
- `EmwuiClient.GetEventInfoTextAsync()` を廃止
- `EpgData.db` の `events` テーブルを (onid, tsid, sid, event_id) で検索
- `short_text` + `ext_text` を連結して番組説明として表示

### メリット
- EMWUI への HTTP 接続が不要（オフライン・高速）
- EpgTimerSrv が蓄積した過去の EPG も参照可能

### 必要な追加設定
- `AppSettings` に `EpgDbPath`（EpgData.db のパス）を追加予定
  - または `EpgDataFolder` を転用して同フォルダの `EpgData.db` を探す
