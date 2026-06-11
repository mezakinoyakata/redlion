# EDCBViewer 仕様書

## 概要

EDCB（EpgTimerSrv）と連携する Windows WPF アプリケーション。  
録画済みファイルの閲覧・再生、EPG 全文検索、番組表表示を提供する。

---

## プロジェクト情報

| 項目 | 内容 |
|---|---|
| 場所 | `C:\work\CC\EDCBViewer` |
| フレームワーク | .NET 9.0 / WPF / Windows |
| 依存パッケージ | MySqlConnector 2.4.0（EPG DB 接続用）、Microsoft.Data.Sqlite 9.0.0（未使用・削除候補） |
| ビルド | `dotnet build -c Release`（`dotnet publish` は使わない、`-c Release` 必須） |
| テスト | `dotnet test Tests\EDCBViewer.Tests.csproj -c Release`（xUnit） |

---

## ファイル構成

```
EDCBViewer/
├── MainWindow.xaml / .cs      メインウィンドウ（ディレクトリタブ）
├── EpgGuideWindow.xaml / .cs  番組表ウィンドウ
├── SettingsWindow.xaml / .cs  設定ダイアログ
├── DarkTitleBar.cs            タイトルバーのダークモード化（DWM API）
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
├── Services/
│   ├── EpgDbReader.cs         MySQL EPG DB 読み取り・全文検索・番組表クエリ
│   └── RecordingIndex.cs      未使用・削除候補
└── Tests/
    ├── EDCBViewer.Tests.csproj  xUnit テストプロジェクト
    ├── MediaFileTests.cs        ファイル名パースのテスト
    └── EpgTrigramTests.cs       トリグラム判定のテスト
```

---

## 設定項目（AppSettings）

設定ファイルパス: `%LOCALAPPDATA%\EDCBViewer\settings.json`

### 設定 UI に表示される項目

| プロパティ | 説明 | デフォルト |
|---|---|---|
| MaxRecItems | ディレクトリタブの1ページ表示件数 | `500` |
| PlayerPath | 動画プレイヤー実行ファイルのパス | MPC-BE のパス |
| EncodedFolders | 起点フォルダのリスト（複数可、追加/削除 UI あり） | `[]` |
| DbConnectionString | MySQL 接続文字列（EPG DB 接続用）例: `Server=5600x;Database=edcbviewer;Uid=edcb;Pwd=xxx` | `""` |

### AppSettings.cs に存在するが UI に出ない項目

| プロパティ | 状態 |
|---|---|
| EncodedFolder | 旧設定（単一起点）。初回ロード時に EncodedFolders へ移行 |
| RecordingFolder | 未使用（追っかけ再生機能の名残） |
| RefreshIntervalSeconds | 自動更新無効化のため未使用 |
| EpgDataFolder | 未使用 |

---

## ディレクトリタブ

起点フォルダ（`EncodedFolders`、複数可）を起点に、フォルダを移動しながら `.ts` / `.m2ts` / `.mp4` ファイルを閲覧・再生する。

### 複数起点のマージ表示

- ルート表示（パス未指定）時は全起点フォルダの直下フォルダ・ファイルをマージして表示
- 列挙は起点 ×拡張子パターンごとに try/catch で隔離（`TryList`、eager評価）。
  一部の SMB 共有が落ちていても他の起点は表示される
- 初回ロードで 0 件だった起点がある場合、5 秒後に自動で再読込
  （SMB コールドスタートのタイムアウト対策）

### アドレスバー

- **パスボックス**（編集可能）: 現在のフォルダパスを表示。Enter で移動。存在しないパスは拒否して元に戻す
- **… ボタン**: `OpenFolderDialog` で任意フォルダへ移動

### ファイル一覧

- カレントディレクトリのサブフォルダ + `.ts`/`.m2ts`/`.mp4` ファイルのみ表示（再帰列挙なし）
- サブフォルダは `📁 フォルダ名` 形式でリスト上部に名前順表示
- ファイルは ParsedStartTime 降順（解析できない場合は LastModified 降順）
- ダブルクリックまたは Enter でサブフォルダに移動 / ファイルを再生

### ファイル名パース（MediaFile）

EDCB RecName_Macro.DLL フォーマット
`{Title} ({ServiceName} {YYYY}-{MM}-{DD}-{HHMM}-{曜日})` を正規表現で解析し、
`ParsedTitle` / `ParsedStation` / `ParsedStartTime` を得る。

- EDCB の継続録画サフィックス `-(N)`（例: `〜)-(1).ts`）に対応。
  タイトル・局・日時は本編と同じ値になり、リスト表示名に `(続きN)` を付加して区別する
- パターン不一致のファイルはファイル名をそのままタイトルとして扱う

### ファイル選択時の情報表示

ファイル名パース結果を即時表示した後、非同期で MySQL を検索して番組情報を補完する。

```
① ファイル名パース結果（ParsedTitle / ParsedStation / ParsedStartTime）を右ペインに即表示
② ParsedStation と ParsedStartTime が取得できた場合:
     EpgDbReader.GetEventInfoTextByStationAndTime(station, startTime, preferTitle: ParsedTitle)
       → events JOIN services WHERE service_name=@svc AND start_time BETWEEN ±2分（最大10件）
       → 複数ヒット時は event_name と preferTitle の双方向包含でベスト候補を選択
       → preferTitle と event_name にトリグラム（3文字部分列）の共通がなければ
          別番組とみなして非表示（同局名マルチサービスの誤マッチ対策）
       → short_text + ext_text を番組情報として表示
③ ②がヒットしない、または ParsedStation/ParsedStartTime 未取得 → 番組情報欄は非表示
```

> イベント名と説明文の照合は行わない。説明文がタイトル文字列を含まない番組が
> 普通に存在するため（過剰防御で正常データを抑制した実績あり）。

---

## 番組表（EpgGuideWindow）

メニュー「番組表」から開く（単一インスタンス）。MySQL の events テーブルを直接参照するため、
**EpgTimerSrv の過去番組表（EnumPgArc）と異なり、テーブルに残っている限り過去のどの日付でも表示できる。**
過去データの蓄積は EpgSqliteExporter の稼働開始時点から。

### レイアウト

- 04:00 起点の 24 時間グリッド（縦=時間 3px/分、横=サービス 170px/列）
- 列順: 地デジ（onid≥30848）→ BS（onid=4）→ CS（onid=6,7）→ その他、リモコンキー順
- 対象サービス: `service_type=1 AND partial_reception=0`（ワンセグ・データ放送等を除外）
- ジャンル大分類（event_genres.nibble_l1、seq=0）でブロックを色分け
- 表示日が今日の場合は現在時刻に赤ラインを表示し、その位置へ自動スクロール
- サービス名ヘッダー（横）・時刻軸（縦）はメイングリッドとスクロール同期

### ナビゲーション

- 前日 / 今日 / 翌日 ボタン、DatePicker による日付ジャンプ
- 水平ホイール（WM_MOUSEHWHEEL、MX Master のサムホイール等）で横スクロール

### 詳細表示

- 番組ブロックをクリックすると右ペインにタイトル・放送局・放送日時を表示
- 本文（short_text + ext_text）は一覧クエリに含めず、クリック時に
  `GetEventInfoText(onid, tsid, sid, event_id)` で遅延取得する

---

## 絞込・全文検索

### ファイル絞込（通常モード）

絞込ボックスに文字を入力するとリアルタイムでファイル名（ParsedTitle・ParsedStation）を部分一致フィルタする。
スペース区切りは AND 条件（全語一致）。フォルダは常時表示（フィルタ対象外）。

### EPG 全文検索（Enter）

絞込ボックスにキーワードを入力して **Enter** を押すと EPG 全文検索モードに切り替わる。

```
EpgDbReader.SearchEvents(keyword)
  ① フレーズ検索: 語を [\s　]* で連結した REGEXP パターンで
     event_name / short_text / ext_text を検索（隣接一致のみ。
     キャスト列で別人名が並ぶ場合の誤ヒットを防ぐ）
  ② ①が 0 件なら同一フィールド内 AND LIKE 検索にフォールバック
  → 最大 200 件を start_time DESC で返す
```

- 結果は番組名・放送局・放送日時でリストに表示
- 番組を選択すると右ペインに short_text + ext_text を表示
- 絞込ボックスをクリアするとファイルブラウズモードに戻る
- 全文検索中はページングボタンは無効

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

## UI 共通

- 全ウィンドウのタイトルバーは `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` でダークモード描画（`DarkTitleBar.Apply`）

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
| `GetEventInfoTextByStationAndTime(station, startTime, preferTitle)` | 放送局名 + 開始時刻 ±2分で検索。preferTitle とのトリグラム照合で誤マッチを除外 |
| `GetGuideEvents(rangeStart, rangeEnd)` | 番組表用。時間範囲の全TVサービスのイベント（ジャンル付き、本文なし）を表示順で返す |
| `SearchEvents(keyword, limit=200)` | 全文検索。REGEXP フレーズ → AND LIKE フォールバック |
| `HasCommonTrigram(a, b)` | internal。3文字部分列の共通有無（テストから参照） |

### DB ビュー

| ビュー | 定義 |
|---|---|
| `program_guide` | `SELECT e.*, s.service_name, s.network_name, s.remote_control_key FROM events e JOIN services s USING (onid, tsid, sid)` |
| `upcoming` | `SELECT * FROM program_guide WHERE start_time > NOW()` |

### 書き込み側（EpgTimerSrv）

EpgTimerSrv（C++）が EPG ロード完了後に MySQL へ書き出す。  
詳細は `C:\work\CC\EDCB\EpgSqliteExporter_Spec.md` を参照。

---

## テスト（Tests/）

デグレ防止用の xUnit テスト。`dotnet test Tests\EDCBViewer.Tests.csproj -c Release` で実行。

| テストファイル | 対象 |
|---|---|
| MediaFileTests.cs | ファイル名パース（通常 / 継続 `-(N)` / 不一致 / フォルダ表示名 / 日時テキスト） |
| EpgTrigramTests.cs | `HasCommonTrigram` の基本動作と DB 破損シナリオの再現 |

テストプロジェクトは `InternalsVisibleTo("EDCBViewer.Tests")` で internal メンバーを参照する。
本体 csproj は `<Compile Remove="Tests\**" />` で Tests 配下を除外している。
