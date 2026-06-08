# EDCBViewer 仕様書

## 概要

EDCB（EpgTimerSrv）と連携する Windows WPF アプリケーション。  
録画済みファイルの閲覧・再生、EPG 全文検索を提供する。

---

## プロジェクト情報

| 項目 | 内容 |
|---|---|
| 場所 | `C:\work\CC\EDCBViewer` |
| フレームワーク | .NET 9.0 / WPF / Windows |
| 依存パッケージ | MySqlConnector 2.4.0（EPG DB 接続用）、Microsoft.Data.Sqlite 9.0.0（未使用・削除候補） |
| ビルド | `dotnet build -c Release`（`dotnet publish` は使わない、`-c Release` 必須） |

---

## ファイル構成

```
EDCBViewer/
├── MainWindow.xaml / .cs      メインウィンドウ（ディレクトリタブ）
├── SettingsWindow.xaml / .cs  設定ダイアログ
├── AppSettings.cs             設定管理（JSON 永続化）
├── App.xaml / .cs             アプリエントリポイント
├── GlobalUsings.cs
├── Models/
│   ├── EpgEvent.cs            EPG イベントモデル
│   ├── MediaFile.cs           ディレクトリタブ用メディアファイル
│   ├── RecFileInfo.cs         未使用・削除候補
│   ├── ReserveData.cs         未使用・削除候補
│   └── RecordingIndexEntry.cs 未使用・削除候補
├── Parsers/
│   ├── EmwuiClient.cs         未使用・削除候補
│   └── EncodingDetector.cs    文字コード検出
└── Services/
    ├── EpgDbReader.cs         MySQL EPG DB 読み取り・全文検索
    └── RecordingIndex.cs      未使用・削除候補
```

---

## 設定項目（AppSettings）

設定ファイルパス: `%LOCALAPPDATA%\EDCBViewer\settings.json`

### 設定 UI に表示される項目

| プロパティ | 説明 | デフォルト |
|---|---|---|
| MaxRecItems | ディレクトリタブの1ページ表示件数 | `500` |
| RecordingFolder | 録画フォルダ（追っかけ再生時のファイル探索用）例: `\\5600x\d\PT2` | `\\5600x\d\PT2` |
| PlayerPath | 動画プレイヤー実行ファイルのパス | MPC-BE のパス |
| EncodedFolder | ディレクトリタブの起点フォルダ | `""` |
| DbConnectionString | MySQL 接続文字列（EPG DB 接続用）例: `Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=xxx` | `""` |

### AppSettings.cs に存在するが UI に出ない項目

| プロパティ | 状態 |
|---|---|
| RefreshIntervalSeconds | 自動更新無効化のため未使用 |
| EpgDataFolder | 未使用 |

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

ファイル名から `ParsedTitle` / `ParsedStation` / `ParsedStartTime` を即時表示した後、非同期で MySQL を検索して番組情報を補完する。

```
① ファイル名パース結果（ParsedTitle / ParsedStation / ParsedStartTime）を右ペインに即表示
② ParsedStation と ParsedStartTime が取得できた場合:
     EpgDbReader.GetEventInfoTextByStationAndTime(station, startTime)
       → events JOIN services WHERE service_name=@svc AND start_time BETWEEN ±2分
       → ヒット → short_text + ext_text を番組情報として表示
③ ②がヒットしない、または ParsedStation/ParsedStartTime 未取得 → 番組情報欄は非表示
```

---

## 絞込・全文検索

### ファイル絞込（通常モード）

絞込ボックスに文字を入力するとリアルタイムでファイル名（ParsedTitle・ParsedStation）を部分一致フィルタする。
フォルダは常時表示（フィルタ対象外）。

### EPG 全文検索（Enter）

絞込ボックスにキーワードを入力して **Enter** を押すと EPG 全文検索モードに切り替わる。

```
EpgDbReader.SearchEvents(keyword)
  → MySQL FULLTEXT: MATCH(event_name, short_text, ext_text) AGAINST (@kw IN BOOLEAN MODE)
  → program_guide ビュー（events JOIN services）を対象
  → 最大 200 件を start_time DESC で返す
```

- 結果は番組名・放送局・放送日時でリストに表示
- 番組を選択すると右ペインに short_text + ext_text を表示
- 絞込ボックスをクリアするとファイルブラウズモードに戻る
- 全文検索中はページングボタンは無効

### MySQL FULLTEXT インデックス

```sql
FULLTEXT KEY ft_event (event_name, short_text, ext_text) WITH PARSER ngram
```

- ngram パーサー使用（日本語対応）
- `ngram_token_size` デフォルト 2（2文字以上のキーワードで検索可能）

---

## ソート機能

列ヘッダークリックで昇順ソート。同じ列を再クリックで降順トグル。ヘッダーに ▲/▼ を表示。

| タブ | ソート可能列 |
|---|---|
| ディレクトリ | ファイル名・放送局・放送日時 |

---

## ページング

ディレクトリタブのみ。First / Prev / Next / Last ボタンおよびキーボードで操作。1ページあたりの件数は MaxRecItems 設定値。

EPG 全文検索モード中はページング無効（最大 200 件を一括表示）。

---

## キーボードショートカット

| キー | 動作 |
|---|---|
| PageDown / PageUp | 次/前ページ |
| Home / End | 先頭/末尾ページ |
| F5 | フォルダ再読込 |
| Enter（リスト選択中） | ファイル再生 / フォルダ移動 |
| Enter（絞込ボックス） | EPG 全文検索実行 |
| ↑ / ↓ | リスト選択移動 |
| 印字可能文字（リスト選択中） | 絞込ボックスにフォーカスを移して入力 |

---

## MySQL EPG DB 連携

> **EDCBViewer は events テーブルに書き込まない。** 書き込みは EpgTimerSrv が行う。

- クラス: `Services/EpgDbReader.cs`
- 接続文字列: `AppSettings.DbConnectionString`
- DbConnectionString 未設定（`IsConfigured == false`）の場合は null / 空リストを返す

### メソッド

| メソッド | 用途 |
|---|---|
| `GetEventInfoText(onid, tsid, sid, eventId)` | PK で events を検索し short_text + ext_text を返す |
| `GetEventInfoTextByStationAndTime(station, startTime)` | 放送局名 + 開始時刻 ±2分で events JOIN services を検索 |
| `SearchEvents(keyword, limit=200)` | FULLTEXT 全文検索。program_guide ビューを対象に MATCH AGAINST |

### DB ビュー

| ビュー | 定義 |
|---|---|
| `program_guide` | `SELECT e.*, s.service_name, s.network_name, s.remote_control_key FROM events e JOIN services s USING (onid, tsid, sid)` |
| `upcoming` | `SELECT * FROM program_guide WHERE start_time > NOW()` |

### 書き込み側（EpgTimerSrv）

EpgTimerSrv（C++）が EPG ロード完了後に MySQL へ書き出す。  
詳細は `C:\work\CC\EDCB\EpgSqliteExporter_Spec.md` を参照。
