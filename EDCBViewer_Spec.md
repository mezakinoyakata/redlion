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
| 依存パッケージ | MySqlConnector 2.4.0（録画インデックス・EPG DB 共用）、Microsoft.Data.Sqlite 9.0.0（csproj 残存・未使用、削除候補） |
| ビルド | `dotnet build -c Release`（`dotnet publish` は使わない、`-c Release` 必須） |

---

## ファイル構成

```
EDCBViewer/
├── MainWindow.xaml / .cs      メインウィンドウ（録画・予約・ディレクトリタブ）
├── SettingsWindow.xaml / .cs  設定ダイアログ
├── AppSettings.cs             設定管理（JSON 永続化）
├── App.xaml / .cs             アプリエントリポイント
├── GlobalUsings.cs
├── Models/
│   ├── EpgEvent.cs            EPG イベントモデル
│   ├── RecFileInfo.cs         録画済み情報モデル
│   ├── ReserveData.cs         予約情報モデル
│   ├── MediaFile.cs           ディレクトリタブ用メディアファイル
│   └── RecordingIndexEntry.cs 録画インデックスエントリ
├── Parsers/
│   ├── EmwuiClient.cs         EMWUI HTTP API クライアント
│   └── EncodingDetector.cs    文字コード検出
└── Services/
    ├── EpgDbReader.cs         MySQL EPG DB 読み取り
    └── RecordingIndex.cs      MySQL 録画インデックス DB 読み取り
```

---

## 設定項目（AppSettings）

設定ファイルパス: `%LOCALAPPDATA%\EDCBViewer\settings.json`

### 設定 UI に表示される項目

| プロパティ | 説明 | デフォルト |
|---|---|---|
| EmwuiBaseUrl | EpgTimerSrv の EMWUI URL（予約一覧・番組情報フォールバック用）例: `http://5600x:5510` | `""` |
| MaxRecItems | 録画済み・予約・ディレクトリ各タブの1ページ表示件数 | `500` |
| RecordingFolder | 録画フォルダ（追っかけ再生時のファイル探索用）例: `\\5600x\d\PT2` | `\\5600x\d\PT2` |
| PlayerPath | 動画プレイヤー実行ファイルのパス | MPC-BE のパス |
| EncodedFolder | ディレクトリタブの起点フォルダ | `""` |
| DbConnectionString | MySQL 接続文字列（録画インデックス・EPG DB 共用）例: `Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=xxx` | `""` |

### AppSettings.cs に存在するが UI に出ない項目

| プロパティ | 状態 |
|---|---|
| RefreshIntervalSeconds | 自動更新無効化のため未使用 |
| EpgDataFolder | `*_epg.dat` 用として残存・未使用 |

### ToUncPath()

`EmwuiBaseUrl` のホスト名を使い、サーバーローカルパス（例: `D:\PT2\foo.ts`）を UNC パス（`\\5600x\d\PT2\foo.ts`）に変換する。すでに UNC パスの場合はそのまま返す。録画ファイル再生時に使用。

---

## データ取得フロー

### 録画済み一覧

```
RecordingIndex.LoadAll()
  → MySQL recordings テーブル全件取得（ORDER BY start_time DESC）
  → _allRecList に保持（検索・ソート用）
  → 先頭 MaxRecItems 件をページ表示

DbConnectionString 未設定 → 録画一覧は空
```

### 予約一覧

```
EmwuiClient.GetReserveInfoAsync()
  → GET /api/EnumReserveInfo?count=500&index=M（バッチ取得）
  → XML 解析 → List<ReserveData>

EmwuiBaseUrl 未設定 → 予約一覧は空
```

### 番組情報（録画済み選択時）

```
① _programInfoCache（ConcurrentDictionary<uint, string> メモリキャッシュ）ヒット → 即表示
② info.ProgramInfo（MySQL recordings.program_info カラム）非空 → 表示
③ EpgDbReader.GetEventInfoText(onid, tsid, sid, event_id)
     → MySQL events テーブル検索 → ヒット → キャッシュに追加して表示
④ すべて空 → 番組情報なしで終了（EMWUI フォールバックなし）
```

### 番組情報（予約選択時）

```
data.ProgramInfo が null（未取得）の場合のみ取得:
① EpgDbReader.GetEventInfoText(onid, tsid, sid, event_id)
     → MySQL events テーブル検索
② ① が空 かつ EmwuiBaseUrl 設定済み
     → EmwuiClient.GetEventInfoTextAsync()
       GET /api/EnumEventInfo?basic=0&id=ONID-TSID-SID-EID
取得結果を data.ProgramInfo に保存（再選択時は再取得しない）
```

### proginfo_cache.json

- パス: `%LOCALAPPDATA%\EDCBViewer\proginfo_cache.json`
- 起動時（クラス静的初期化）にディスクから読み込み
- アプリ終了時（`Window_Closing`）にディスクへ保存
- `recordings.program_info` および MySQL events テーブルから取得した番組情報を蓄積

---

## 検索機能

### 録画済みタブ

| モード | 動作 |
|---|---|
| 通常 | 半角スペースで AND 分割。全角スペースはフィールド側のみ除去。タイトル・放送局・番組情報（キャッシュ）・コメントを検索 |
| フレーズ優先 | スペース区切りトークンの連結形がデータ内に存在する場合、フレーズまたは元クエリいずれかに一致するものを優先 |
| 正規表現 | RegexCheck チェック時。同フィールドに対して正規表現マッチ |
| 日付フィルタ | `<=2026/05/01` 等の演算子付き日付文字列で StartTime の日付を比較（`<=` `>=` `<` `>` `=` 対応） |

- 検索実行: 検索ボックスで Enter またはボタン押下
- 検索クリア: 検索ボックスを空にすると自動でページビューに戻る
- 検索中はソートが有効なら全件ページモード維持

### 予約タブ

- タイトル・放送局・番組情報に対して部分一致（大小文字無視）
- 日付フィルタ同様に対応

### ディレクトリタブ

- ParsedTitle・ParsedStation に対して部分一致
- フォルダは常時表示（フィルタ対象外）

---

## ソート機能

列ヘッダークリックで昇順ソート。同じ列を再クリックで降順トグル。ヘッダーに ▲/▼ を表示。

| タブ | ソート可能列 |
|---|---|
| 録画済み | タイトル・放送局・開始日時・時間・状態 |
| 予約録画 | タイトル・放送局・開始日時・時間・状態 |
| ディレクトリ | ファイル名・放送局・放送日時 |

---

## ページング

全タブ共通。First / Prev / Next / Last ボタンおよびキーボードで操作。1ページあたりの件数は MaxRecItems 設定値。

| タブ | ページ数の基準 |
|---|---|
| 録画済み | MySQL recordings 全件数 |
| 予約録画 | EMWUI から取得した全予約数 |
| ディレクトリ | カレントディレクトリのファイル数 |

---

## キーボードショートカット

| キー | 動作 |
|---|---|
| PageDown / PageUp | アクティブタブの次/前ページ |
| Home / End | アクティブタブの先頭/末尾ページ |
| Ctrl+1 / 2 / 3 | 録画済み / 予約録画 / ディレクトリタブに切替 |
| F5 | 更新（ディレクトリタブはカレントフォルダ再読込） |
| Enter（リスト選択中） | ファイル再生 / フォルダ移動 / 予約のダブルクリック動作 |
| ↑ / ↓ | リスト選択移動 |
| 印字可能文字（リスト選択中） | 検索ボックスにフォーカスを移して入力 |

---

## ディレクトリタブ

起点フォルダ（`EncodedFolder`）を起点に、フォルダを移動しながら `.ts` / `.m2ts` / `.mp4` ファイルを閲覧・再生する。

### アドレスバー

- **パスボックス**（編集可能）: 現在のフォルダパスを表示。Enter で移動。存在しないパスは拒否して元に戻す
- **… ボタン**: `OpenFolderDialog` で任意フォルダへ移動。起点外を選択した場合は新しい起点として設定

### ファイル一覧

- カレントディレクトリのサブフォルダ + `.ts`/`.m2ts`/`.mp4` ファイルのみ表示（再帰列挙なし）
- サブフォルダは `📁 フォルダ名` 形式でリスト上部に名前順表示
- ファイルは ParsedStartTime 降順（解析できない場合は LastModified 降順）
- ダブルクリックまたは Enter でサブフォルダに移動 / ファイルを再生

### ファイル選択時の情報表示

MySQL `recordings` テーブルを `file_name`（拡張子なしファイル名）で検索し、一致するエントリがあれば右ペインにタイトル・放送局・日時・ドロップ数・番組情報を表示。一致なしの場合はファイル名から ParsedTitle / ParsedStation / ParsedStartTime を表示。

---

## 追っかけ再生（予約ダブルクリック）

1. `recordings` の Title + StartTime が一致する録画ファイルを探して再生
2. なければ同 Title の最新録画を再生
3. `IsRecording == true` の場合は `RecordingFolder` を検索し、予約 StartTime ±5分以内に作成された `.ts`/`.m2ts` ファイルを再生（`skipExistCheck: true`）

---

## MySQL データベース（録画インデックス）

> **EDCBViewer は MySQL に書き込まない。** `recordings` テーブルへの書き込みは EpgTimerSrv（EDCB）が行う。EDCBViewer は SELECT のみ。

- 接続: `AppSettings.DbConnectionString`（EPG DB と同一の MySQL サーバー・同一接続文字列）
- 管理クラス: `Services/RecordingIndex.cs`
- テーブル: `recordings`
- DbConnectionString 未設定の場合は全 SELECT をスキップ

### recordings テーブルの主なカラム

| カラム | 型 | 内容 |
|---|---|---|
| `file_name` | VARCHAR (PRIMARY KEY) | `Path.GetFileNameWithoutExtension(RecFilePath)` |
| `full_title` | VARCHAR | 録画タイトル（元のまま） |
| `series_title` | VARCHAR | `ParseTitle()` で抽出したシリーズ名 |
| `episode_number` | INT NULL | 話数（取得できない場合は NULL） |
| `start_time` | DATETIME NULL | 録画開始日時 |
| `start_time_epg` | DATETIME NULL | EPG 上の開始日時 |
| `duration_second` | BIGINT | 録画秒数 |
| `service_name` | VARCHAR | 放送局名 |
| `rec_id` | BIGINT | EDCB の録画 ID |
| `onid` / `tsid` / `sid` / `event_id` | BIGINT | EPG 識別子 |
| `program_info` | TEXT | 番組情報テキスト |
| `comment` / `err_info` | VARCHAR | コメント・エラー情報 |
| `drops` / `scrambles` | BIGINT | ドロップ・スクランブル数 |
| `rec_status` / `protect_flag` | BIGINT | 録画状態・保護フラグ |
| `original_file_path` | VARCHAR | フルパス |
| `saved_at` | DATETIME | インデックス登録日時 |

### 主なメソッド（SELECT のみ）

- `LoadAll()`: 全件を `List<RecFileInfo>` として返す（START TIME DESC）
- `Find(fileName)`: `file_name` で検索して `RecordingIndexEntry?` を返す
- `FindPathByRecId(recId)`: `rec_id` から `original_file_path` を返す
- `ParseTitle(title)`: タイトルからシリーズ名と話数を抽出（`#N`・`＃N`・`第N話`・`第N回`・`（N）`・`(N)`・末尾スペース+数字に対応）

> `AddOrUpdate()` メソッドはコード上に残存しているが呼び出し元なし。削除予定。

---

## MySQL EPG DB 連携

> **EDCBViewer は events テーブルに書き込まない。** 書き込みは EpgTimerSrv が行う。

- クラス: `Services/EpgDbReader.cs`
- 接続文字列: `AppSettings.DbConnectionString`（録画インデックス DB と共用）
- DbConnectionString 未設定（`IsConfigured == false`）の場合は null を返す

### GetEventInfoText

`(onid, tsid, sid, event_id)` で `events` テーブルを検索し `short_text + "\n" + ext_text` を返す。両方空なら null。

> `SyncCacheToDbAsync()` メソッドはコード上に残存しているが呼び出し元なし。削除予定。

### 書き込み側（EpgTimerSrv）

EpgTimerSrv（C++）が EPG ロード完了後に MySQL へ書き出す。  
詳細は `C:\work\CC\EDCB\EpgSqliteExporter_Spec.md` を参照。

---

## 他プロジェクトとの連携

### EpgTimerSrv（C++）
- **予約一覧**: EMWUI HTTP API（`/api/EnumReserveInfo`）経由
- **録画一覧**: MySQL `recordings` テーブルから直接取得（EMWUI 不使用）
- **EPG DB**: EpgTimerSrv が書き出す MySQL `events` テーブルを参照

### EDCBEpgImporter（C#）
- `C:\work\CC\EDCBEpgImporter`
- `*_epg.dat` を読んで同スキーマの DB に書き出すスタンドアロンツール
