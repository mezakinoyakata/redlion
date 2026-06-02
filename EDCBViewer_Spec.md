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
| 依存パッケージ | MySqlConnector、Microsoft.Data.Sqlite 9.0.0（録画インデックスDB用） |
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
    ├── EpgDbReader.cs         MySQL EPG DB 読み取り（MySqlConnector）
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
| EpgDataFolder | EDCB の EpgData フォルダ（*_epg.dat の場所、現在未使用） | `""` |
| DbConnectionString | MySQL 接続文字列（EPG DB 参照用） | `""` |

設定ファイルパス: `%LOCALAPPDATA%\EDCBViewer\settings.json`

### DbConnectionString の形式

```
Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=（パスワード）
```

未設定の場合、EPG DB 参照をスキップして EMWUI フォールバックのみ使用する。

---

## データ取得フロー

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
① キャッシュ確認（_programInfoCache）→ ヒットなら即表示
② EpgDbReader.GetEventInfoText(onid, tsid, sid, event_id)
     → MySQL events テーブル検索
     → short_text + ext_text を返す
③ ② が空なら EMWUI フォールバック
     → GET /api/EnumRecInfo?id=N → programInfo フィールド
```

### 番組情報（予約選択時）
```
① EpgDbReader.GetEventInfoText(onid, tsid, sid, event_id)
     → MySQL events テーブル検索
② ① が空なら EMWUI フォールバック
     → GET /api/EnumEventInfo?basic=0&id=ONID-TSID-SID-EID
```

---

## SQLiteデータベース（録画インデックス）

- パス: `%LOCALAPPDATA%\EDCBViewer\recording_index.db`
- 管理クラス: `Services/RecordingIndex.cs`
- テーブル: `recordings`（録画済みファイルのメタデータを蓄積）
- `start_time_epg` など後付けカラムは空文字列になりうるため `ReadEntry` で `ParseDt()` により `default` にフォールバック

---

## MySQL EPG DB 連携

### EpgDbReader

- クラス: `Services/EpgDbReader.cs`
- 使用ライブラリ: `MySqlConnector`
- 接続文字列: `AppSettings.DbConnectionString`
- `IsConfigured` が false（DbConnectionString 未設定）の場合は null を返す

#### GetEventInfoText
`(onid, tsid, sid, event_id)` で events テーブルを PK 検索し `short_text + ext_text` を返す。

#### SyncCacheToDbAsync
起動時に `LoadAllRecInfoAsync` 完了後に自動実行。  
`proginfo_cache.json` に蓄積された番組情報を events テーブルへ書き込む。

- `INSERT INTO ... ON DUPLICATE KEY UPDATE` を使用
- EpgTimerSrv が書き出した既存行の EPG テキストは上書きしない
- `start_time` が NULL の行のみ更新（EpgTimerSrv 書き出し分を保護）
- `reserve_status` が 2 未満の行のみ 2 に更新
- `EventID=0` の録画は除外（PK が作れないため）

### 書き込み側（EpgTimerSrv）

EpgTimerSrv（C++）が EPG ロード完了後に MySQL へ REPLACE INTO する。  
詳細は `C:\work\CC\EDCB\EpgSqliteExporter_Spec.md` を参照。

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
- **EPG DB（MySQL）**: EpgTimerSrv が書き出す MySQL を EDCBViewer が参照する

### EDCBEpgImporter（C#）
- `C:\work\CC\EDCBEpgImporter`
- `*_epg.dat` を読んで SQLite に書き出すスタンドアロンツール（開発・検証用）
