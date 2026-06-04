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
| 依存パッケージ | MySqlConnector 2.4.0（EPG DB・録画インデックス DB 両用）、Microsoft.Data.Sqlite 9.0.0（csproj 残存・未使用、削除候補） |
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
│   ├── EmwuiClient.cs         EMWUI HTTP API クライアント（予約一覧・番組情報取得）
│   ├── EpgBinaryParser.cs     *_epg.dat バイナリパーサー（未使用・削除候補）
│   ├── EncodingDetector.cs    文字コード検出
│   ├── RecInfoParser.cs       RecInfo.txt パーサー（未使用・削除候補）
│   └── ReserveParser.cs       Reserve.txt パーサー（未使用・削除候補）
└── Services/
    ├── EpgDbReader.cs         MySQL EPG DB 読み取り（MySqlConnector）
    └── RecordingIndex.cs      MySQL 録画インデックス DB 管理（MySqlConnector）
```

---

## 設定項目（AppSettings）

### 設定 UI に表示される項目

| プロパティ | 説明 | デフォルト |
|---|---|---|
| EmwuiBaseUrl | EpgTimerSrv の EMWUI URL（予約一覧・番組情報フォールバック用） | `""` |
| MaxRecItems | 録画済み一覧のページサイズ（ディレクトリタブのページサイズも兼用） | 500 |
| RecordingFolder | 録画フォルダ（追っかけ再生時のファイル探索用） | `\\5600x\d\PT2` |
| PlayerPath | 動画プレイヤーのパス | MPC-BE のパス |
| EncodedFolder | ディレクトリタブの起点フォルダ | `""` |
| DbConnectionString | MySQL 接続文字列（録画インデックス・EPG DB 共用） | `""` |

### AppSettings.cs に存在するが UI に出ない項目

| プロパティ | 状態 |
|---|---|
| RefreshIntervalSeconds | 自動更新無効化のため未使用 |
| EpgDataFolder | *_epg.dat 用として残存・未使用 |

設定ファイルパス: `%LOCALAPPDATA%\EDCBViewer\settings.json`

### DbConnectionString の形式

```
Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=（パスワード）
```

未設定の場合、MySQL 録画インデックスおよび EPG DB 参照をスキップする。

### ToUncPath()

`EmwuiBaseUrl` のホスト名を使って、サーバーローカルパス（例: `D:\PT2\foo.ts`）を UNC パス（`\\5600x\d\PT2\foo.ts`）に変換する。録画ファイル再生時に使用。

---

## データ取得フロー

### 録画済み一覧

```
RecordingIndex.LoadAll()
  → MySQL recordings テーブル全件取得
  → ORDER BY start_time DESC
  → ページスライスして表示

DbConnectionString 未設定の場合 → 録画一覧は空
```

### 予約一覧

```
EmwuiClient.GetReserveInfoAsync()
  → GET /api/EnumReserveInfo?count=500&index=M（バッチ取得）
  → XML解析 → List<ReserveData>

EmwuiBaseUrl 未設定の場合 → 予約一覧は空
```

### 番組情報（録画済み選択時）

```
① _programInfoCache（メモリキャッシュ）→ ヒットなら即表示
② info.ProgramInfo（MySQL recordings.program_info）→ あれば表示
③ EpgDbReader.GetEventInfoText(onid, tsid, sid, event_id)
     → MySQL events テーブル検索 → あれば表示
④ ③ も空なら番組情報なしで終了（EMWUI フォールバックなし）
```

### 番組情報（予約選択時）

```
① EpgDbReader.GetEventInfoText(onid, tsid, sid, event_id)
     → MySQL events テーブル検索
② ① が空なら EMWUI フォールバック
     → GET /api/EnumEventInfo?basic=0&id=ONID-TSID-SID-EID
```

### proginfo_cache.json

- パス: `%LOCALAPPDATA%\EDCBViewer\proginfo_cache.json`
- 起動時に読み込み、終了時に保存
- MySQL `recordings.program_info` の内容をメモリキャッシュとして保持

---

## MySQLデータベース（録画インデックス）

- 接続: `AppSettings.DbConnectionString`（EPG DB と同一の MySQL サーバー・同一接続文字列）
- 管理クラス: `Services/RecordingIndex.cs`
- テーブル: `recordings`
- 未設定（`DbConnectionString` が空）の場合は全操作をスキップ

### recordings テーブルの主なカラム

| カラム | 型 | 内容 |
|---|---|---|
| `file_name` | VARCHAR (PRIMARY KEY) | `Path.GetFileNameWithoutExtension(RecFilePath)` |
| `full_title` | VARCHAR | 録画タイトル（元のまま） |
| `series_title` | VARCHAR | `ParseTitle()` で抽出したシリーズ名 |
| `episode_number` | INT NULL | 話数（取得できない場合は NULL） |
| `start_time` | DATETIME NULL | 録画開始日時（NULL 可） |
| `start_time_epg` | DATETIME NULL | EPG 上の開始日時（NULL 可） |
| `duration_second` | BIGINT | 録画秒数 |
| `service_name` | VARCHAR | 放送局名 |
| `rec_id` | BIGINT | EDCB の録画 ID |
| `onid/tsid/sid/event_id` | BIGINT | EPG 識別子 |
| `program_info` | TEXT | 番組情報テキスト |
| `comment/err_info` | VARCHAR | コメント・エラー情報 |
| `drops/scrambles` | BIGINT | ドロップ・スクランブル数 |
| `rec_status/protect_flag` | BIGINT | 録画状態・保護フラグ |
| `original_file_path` | VARCHAR | フルパス（`RecFilePath`） |
| `saved_at` | DATETIME | インデックス登録日時 |

### 主なメソッド

- `LoadAll()`: `recordings` テーブル全件を `List<RecFileInfo>` として返す（録画一覧表示用）
- `Find(fileName)`: `file_name` でレコードを検索し `RecordingIndexEntry` を返す（ディレクトリタブで使用）
- `FindPathByRecId(recId)`: `rec_id` から `original_file_path` を返す
- `ParseTitle(title)`: タイトルからシリーズ名と話数を抽出（`#N`・`第N話`・`第N回`・`（N）`・末尾数字に対応）
- `AddOrUpdate(rec, programInfo)`: `file_name` をキーに UPSERT（現在は呼び出し元なし・削除候補）

---

## MySQL EPG DB 連携

### EpgDbReader

- クラス: `Services/EpgDbReader.cs`
- 使用ライブラリ: `MySqlConnector`
- 接続文字列: `AppSettings.DbConnectionString`
- `IsConfigured` が false（DbConnectionString 未設定）の場合は null を返す

#### GetEventInfoText
`(onid, tsid, sid, event_id)` で `events` テーブルを PK 検索し `short_text + ext_text` を返す。

#### SyncCacheToDbAsync（現在呼び出し元なし・削除候補）
`events` テーブルへの番組情報書き込み。INSERT 時 `short_text=''`・`ext_text=番組情報全文`。  
ON DUPLICATE KEY UPDATE では既存テキストを上書きしない。

### 書き込み側（EpgTimerSrv）

EpgTimerSrv（C++）が EPG ロード完了後に MySQL へ REPLACE INTO する。  
詳細は `C:\work\CC\EDCB\EpgSqliteExporter_Spec.md` を参照。

---

## ディレクトリタブ

設定した起点フォルダ（`EncodedFolder`）を起点にフォルダを移動しながら `.ts` / `.m2ts` / `.mp4` ファイルを閲覧・再生する。録画フォルダを指定して録画済み TS ファイルの確認にも使用可能。

### アドレスバー
- **パスボックス**（編集可能）: 現在のフォルダパスを表示。直接入力して Enter で移動。存在しないパスは拒否して元に戻す
- **… ボタン**: `OpenFolderDialog` を表示して任意フォルダへ移動。`EncodedFolder` 外を選択した場合は新しいルートとして設定

### フォルダナビゲーション
- カレントディレクトリのサブフォルダと `.ts` / `.m2ts` / `.mp4` ファイルを表示（再帰列挙なし）
- サブフォルダは `📁 フォルダ名` で常にリスト上部に表示
- ダブルクリックまたは Enter キーでサブフォルダに移動、ファイルは再生
- 検索ボックスはカレントディレクトリのファイルのみフィルタ（フォルダは常時表示）
- ファイル選択時、MySQL `recordings` テーブルに一致するエントリがあれば番組情報・ドロップ数を右ペインに表示

### MediaFile モデル（`Models/MediaFile.cs`）
| プロパティ | 内容 |
|---|---|
| `IsDirectory` | フォルダエントリかどうか |
| `DisplayName` | フォルダ: `📁 フォルダ名`、ファイル: `ParsedTitle` |

---

## 他プロジェクトとの連携

### EpgTimerSrv（C++）
- **予約一覧**: EMWUI HTTP API（`/api/EnumReserveInfo`）を通じて取得
- **録画一覧**: MySQL `recordings` テーブルから直接取得（EMWUI 不使用）
- **EPG DB（MySQL）**: EpgTimerSrv が書き出す `events` テーブルを EDCBViewer が参照

### EDCBEpgImporter（C#）
- `C:\work\CC\EDCBEpgImporter`
- `*_epg.dat` を読んで SQLite に書き出すスタンドアロンツール（開発・検証用）
