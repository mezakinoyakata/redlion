# EDCBViewer 仕様書

## 概要

EDCB（EpgTimerSrv）と連携する Windows WPF アプリケーション。  
録画済みファイルの閲覧・再生・検索、番組表表示を提供する。

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
├── HorizontalWheel.cs         水平ホイール → 横スクロール変換（WM_MOUSEHWHEEL フック）
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
- **最速放送マーク**（2026-07-19、しょぼいカレンダー連携）: そのファイルが
  **アニメの当該話数の最速TV放送の録画**なら、ファイル名の左端に赤アクセントバーと
  最速列に「★」を表示（通常ブラウズ・検索結果の両方）。詳細は「しょぼいカレンダー連携」の章を参照
  - **最速列**: ヘッダークリックでソート可能。初回クリック（▲）で最速が先頭、
    同順位は放送日時の新しい順
  - **最速のみチェックボックス**（絞込バー右端）: ON にすると最速ファイルだけを表示
    （フォルダ非表示、通常ブラウズ・検索結果の両方に適用）

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
     EpgDbReader.GetEventInfoByStationAndTime(station, startTime, preferTitle: ParsedTitle)
       → events JOIN services WHERE service_name=@svc AND start_time BETWEEN ±2分（最大10件）
       → 複数ヒット時は event_name と preferTitle の双方向包含でベスト候補を選択
       → preferTitle と event_name にトリグラム（3文字部分列）の共通がなければ
          別番組とみなして非表示（同局名マルチサービスの誤マッチ対策）
       → タイトル表示を EPG 側の正式タイトル（event_name）に差し替える。
          ファイル名では Title2 マクロで除去されている [4K][HDR][字] 等のタグが見える
          （2026-07-16 導入。一覧のファイル名列はファイル名のまま）
       → short_text + ext_text を番組情報として表示（両方空なら番組情報欄のみ非表示）
③ ②がヒットしない、または ParsedStation/ParsedStartTime 未取得
   → タイトルはファイル名パース結果のまま、番組情報欄は非表示
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

## ファイル検索（絞込）

**検索結果に表示するのはファイルのみ**（EPG イベントは表示しない）。

絞込ボックスにキーワードを入力して **Enter** を押すと、**現在のスコープ以下を再帰的に**検索する。

- スコープ: ルート表示中なら全起点フォルダ以下、フォルダ内ならそのフォルダ以下（サブフォルダ含む）
- スペース区切りは AND 条件（全語一致）
- マッチ判定は次の **2 系統の OR**（2026-07-16 導入）:
  1. **ファイル名一致**: ParsedTitle・ParsedStation の部分一致（パース不能なファイル名は
     全体が ParsedTitle になる）。両辺を NFKC 正規化するため全角半角・大文字小文字は無視
     （「4K」で「４Ｋ」がヒット）
  2. **EPG 照合**: EPG DB の番組名＋説明文（event_name / short_text / ext_text）に全語一致した
     イベントの (service_name, start_time) と、ファイルの (ParsedStation, ParsedStartTime) が
     分精度で一致すれば結果に含める（`EpgDbReader.GetMatchingEventKeys`）。
     **EDCB の Title2 マクロは `[4K]` `[HDR]` `[字]` 等のタグをファイル名から除去するため、
     これらのタグはファイル名検索では原理的にヒットしない**（「HDR」0 件バグの原因）。
     DB 照合順序 utf8mb4_0900_ai_ci により DB 側も全角半角・大文字小文字を無視する。
     DbConnectionString 未設定・DB 接続不可の場合はファイル名一致のみで動作
- 検索結果は**ファイルのみ表示**（フォルダは表示しない）。ソート・ページング有効
- 一致ゼロの場合は「一致するファイルがありません」を表示（EPG イベントへのフォールバック表示はしない。
  以前は 0 件時に EPG 全文検索の**イベント**を表示していたが、2026-07-11 に撤去。
  同日、検索対象を「表示中フォルダ直下のみ」から「スコープ以下の再帰」に変更）
- 絞込ボックスをクリアするとファイルブラウズモードに戻る

※ `EpgDbReader.SearchEvents`（REGEXP フレーズ検索 → AND LIKE フォールバック）は実装として残っているが、UI からは呼ばれていない。

### 起点フォルダの自動再読込

起動直後にネットワーク未接続などで読み込めなかった起点フォルダがある場合、5 秒後に自動再読込する。
**リトライは最大 3 回**（空フォルダと未接続を区別できないため、無制限だと空の起点フォルダ 1 つで
5 秒ごとの再読込が永久に続く）。手動リフレッシュ（F5・更新ボタン・設定変更・ナビゲート）で回数はリセット。
検索モード中（絞込適用中）は自動再読込しない。

---

## ソート機能

列ヘッダークリックで昇順ソート。同じ列を再クリックで降順トグル。ヘッダーに ▲/▼ を表示。

ソート実行時に項目を選択中だった場合、ソート後もその項目の選択を保持し、
項目が含まれるページへ移動してスクロール表示する（2026-07-16。以前は
1ページ目・無選択にリセットされていた）。未選択時は1ページ目を表示。

| タブ | ソート可能列 |
|---|---|
| ディレクトリ | ファイル名・放送局・放送日時・最速 |

---

## ページング

ディレクトリタブのみ。First / Prev / Next / Last ボタンおよびキーボードで操作。1ページあたりの件数は MaxRecItems 設定値。


---

## キーボードショートカット

| キー | 動作 |
|---|---|
| PageDown / PageUp | 次/前ページ |
| Home / End | 先頭/末尾ページ |
| F5 | フォルダ再読込 |
| Enter（リスト選択中） | ファイル再生 / フォルダ移動 |
| Enter（絞込ボックス） | ファイル絞込実行 |
| ↑ / ↓ | リスト選択移動 |
| 印字可能文字（リスト選択中） | 絞込ボックスにフォーカスを移して入力 |

---

## UI 共通

- 全ウィンドウのタイトルバーは `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)` でダークモード描画（`DarkTitleBar.Apply`）
- 水平ホイール（WM_MOUSEHWHEEL、MX Master のサムホイール等）対応（`HorizontalWheel.Attach`）。
  WPF は水平ホイール未対応のため WndProc フックで変換する
  - メインウィンドウ: マウスカーソル直下の横スクロール可能な ScrollViewer をスクロール
  - 番組表: 常にメイングリッドをスクロール（ヘッダー・詳細ペイン上でも効く）

---

## MySQL EPG DB 連携

> **EDCBViewer は events テーブルに書き込まない。** 書き込みは EpgTimerSrv が行う。
> 最速放送マーク（しょぼいカレンダー連携）用に `syobocal_*` という別テーブル群を
> 追加するが、これらは EDCBViewer 専用の新規テーブルであり events には触れない。
> 理由: events には全文検索用 FULLTEXT インデックス（`ft_event`）があり、MySQL/InnoDB は
> FULLTEXT 保持テーブルへの列追加を高速な `ALGORITHM=INSTANT`/`INPLACE` で行えない。
> 唯一可能な `ALGORITHM=COPY`（テーブル全体再構築）を実測したところ3分以上かかっても
> 完了せず、実行中は events への読み書きをブロックする。events への列追加は
> EpgTimerSrv・EpgTimer 側の動作を阻害するリスクがあるため採用しない
> （2026-07-20、`events.fastest` 列を追加する初期実装を検証中にこの問題が判明し撤回）。

- クラス: `Services/EpgDbReader.cs`
- 接続文字列: `AppSettings.DbConnectionString`
- DbConnectionString 未設定（`IsConfigured == false`）の場合は null / 空リストを返す

### メソッド

| メソッド | 用途 |
|---|---|
| `GetEventInfoText(onid, tsid, sid, eventId)` | PK で events を検索し short_text + ext_text を返す |
| `GetEventInfoByStationAndTime(station, startTime, preferTitle)` | 放送局名 + 開始時刻 ±2分で検索し (event_name, 説明文) を返す。preferTitle とのトリグラム照合で誤マッチを除外 |
| `GetGuideEvents(rangeStart, rangeEnd)` | 番組表用。時間範囲の全TVサービスのイベント（ジャンル付き、本文なし）を表示順で返す |
| `GetMatchingEventKeys(terms)` | 絞込検索用。全語（AND）が番組名・説明文のいずれかに LIKE 一致するイベントの (service_name, start_time) を返す |

---

## しょぼいカレンダー連携（SyobocalService）

最速放送マークの判定に https://cal.syoboi.jp/ の API（db.php）を利用する。

### データの置き場: syobocal_* 専用テーブル（DB共有、events は不変更）

しょぼカルの生データ（放送レコード・チャンネル対応・作品情報・同期進捗）は
**`syobocal_*` という EDCBViewer 専用の新規テーブル群**に持つ。events には一切書かない。
最速判定は**表示のたびに events との JOIN で計算**する（`GetFastestKeysViaJoin`）。
どのマシンから起動しても syobocal_* を共有するため同じ判定結果が見える。

| テーブル | 内容 |
|---|---|
| `syobocal_airings` | 放送レコード。`pid`(PK) `tid` `cnt`(話数、映画等はNULL) `chid` `st_time`。**TV放送のみ格納**（ABEMA等の配信・ラジオは取得時点で除外） |
| `syobocal_service_map` | EDCBサービス名 → しょぼカルChID（複数可）の対応表 |
| `syobocal_titles` | `tid`(PK) `first_ym`（作品の放送開始年月、yyyy*100+MM。不明時0） |
| `syobocal_meta` | key-value。`covered_from_ym` / `covered_to_ym`（連続カバー済み月範囲） / `last_recent_refresh` |

いずれも FULLTEXT インデックスを持たないため、`CREATE TABLE IF NOT EXISTS` も
書き込みも高速（events の ALTER で判明した COPY アルゴリズム問題を回避できる）。

### 判定方法（EpgDbReader.GetFastestKeysViaJoin）

`syobocal_airings` を `syobocal_service_map` → `services` → `events` と JOIN し、
`events.start_time` を鍵とする (サービス名, 開始時刻) の最速集合を1クエリで得る:

```sql
SELECT DISTINCT s.service_name, e.start_time
FROM syobocal_airings a
JOIN syobocal_service_map sm ON sm.chid = a.chid
JOIN services s ON s.service_name = sm.service_name
JOIN events e ON e.onid=s.onid AND e.tsid=s.tsid AND e.sid=s.sid
    AND e.start_time BETWEEN DATE_SUB(a.st_time, INTERVAL 5 MINUTE)
                          AND DATE_ADD(a.st_time, INTERVAL 5 MINUTE)
LEFT JOIN syobocal_titles t ON t.tid = a.tid
WHERE a.cnt IS NOT NULL
  AND a.st_time >= @lo AND a.st_time <= @hi
  AND (t.first_ym IS NULL OR t.first_ym = 0 OR t.first_ym >= @coveredFromYm)
  AND NOT EXISTS (
      SELECT 1 FROM syobocal_airings a2
      WHERE a2.tid = a.tid AND a2.cnt = a.cnt AND a2.st_time < a.st_time
  )
```

- タイトル文字列は一切使わない（局によるタイトル表記の違いに影響されない）。
  `syobocal_airings` には TV 放送のみ格納済みのため、`NOT EXISTS` の比較は自動的に
  TV 放送同士の比較になる（ABEMA 等の先行配信の混入なし）
- `events` との JOIN は ±5分の許容窓。しょぼカル記載時刻と EPG 実測時刻の
  わずかなズレを吸収しつつ、**events に対応する行が無い放送は結果から除外**される
  （録画DBに存在しない＝実際に確認できていない放送は最速扱いしない）
- 話数なし（映画・特番等）は対象外
- 作品の放送開始年月（`syobocal_titles.first_ym`）がカバー範囲（`covered_from_ym`）
  より前の場合は、それ以前の放送を知らないため対象外
- 表示側は結果セットとファイルの (ParsedStation, ParsedStartTime[分精度]) を照合する

### 同期（SyobocalService.SyncToDbAsync）・API 負荷配慮

1. `ChLookup` でチャンネル一覧を取得し EDCB サービス名と対応付ける
   （NFKC 正規化 + 空白・中点・ハイフン除去 + 大文字化 → 完全一致 → 前方一致 → 包含。
   `NHKBSP4K`「テレ東」「日テレ」等の自動照合不能分は手動エイリアス表。候補は複数保持可）
   → `syobocal_service_map` へ反映
2. `syobocal_meta` のカバー済み月範囲（`covered_from_ym`/`covered_to_ym`）を読み、
   ファイル群の放送時期（events 蓄積開始以降にクランプ）を覆うために必要な差分だけを
   **2か月チャンク**で `ProgLookup` 取得し、`syobocal_airings` へチャンクごとに反映
   （既にカバー済みなら通信なし。他マシンが先に同期済みでもここで恩恵を受ける）
   - 1リクエスト5,000件上限に達したら期間を半分にして再帰取得
     （切り捨て検出は Deleted 行込みの生件数 ≥4,900 で判定。有効行だけで判定すると
     末尾欠けを見逃す — 7/17 以降が欠落した実績あり）
   - 途中で失敗したら残りを中断し次回同期時に再開
     （途中の月を飛ばすとカバー範囲の連続性が壊れるため中断が正しい）。
     カバー範囲はチャンクごとに `syobocal_meta` へ保存（中断しても進捗が残る）
   - 直近2か月は改編対応のため再取得する（最短1時間間隔）
3. 今回取得範囲に登場した作品IDのうち `syobocal_titles` に無いものを `TitleLookup` で補充
- **リクエスト間 2秒**（700ms では十数リクエストで 429 になることを実測）。
  429 (Too Many Requests) は 15→30→60秒バックオフで最大3回再試行
- User-Agent `EDCBViewer/1.0`
- 同期の各段階・失敗は `%LOCALAPPDATA%\EDCBViewer\syobocal.log` に記録。
  失敗時はステータスバーに「しょぼカル同期失敗（syobocal.log 参照）」を表示
- オフライン・API 障害時は例外を握りつぶし、DB の既存データの範囲で判定を続行
- 同期は `MainWindow.LoadSyobocal` がファイル一覧読み込み後にバックグラウンド実行。
  まず DB の現在値で即マーク反映（他マシン同期済みならここで出る）→ 同期 →
  更新があれば読み直して再反映（選択位置は保持）。進捗はステータスバーに表示
- **制限: events の蓄積期間（2026-04-27〜）より前の録画は判定対象外（マークなし）**
| `SearchEvents(keyword, limit=200)` | 全文検索。REGEXP フレーズ → AND LIKE フォールバック（現在 UI 未使用） |
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
