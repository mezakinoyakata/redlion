# EDCBViewer

別マシン（視聴PC）から EDCB の録画済みファイルと予約一覧を閲覧し、MPC-BE で再生するための WPF アプリです。

## 動作環境

- .NET 9 Desktop Runtime（Windows）
- 録画機との SMB 共有（UNC パスでアクセス）
- MPC-BE（外部プレイヤー）

## 機能

- **録画済み一覧**（RecInfo.txt を読み込み）
  - 番組名・放送局・日時・録画時間・ドロップ数・ステータスを表示
  - 行をダブルクリック、または選択して再生ボタンで MPC-BE 起動
- **予約一覧**（Reserve.txt を読み込み）
  - 番組名・放送局・日時・ステータスを表示
  - ダブルクリックで対応する録画ファイルを MPC-BE で再生
  - 録画中の番組は追っかけ再生を試みる
- 定期自動更新（デフォルト 60 秒間隔）
- 設定ダイアログでパスを変更可能

## セットアップ

1. リリースの zip を展開して `EDCBViewer.exe` を起動
2. ツールバーの「設定」ボタンで以下を設定
   | 項目 | 説明 |
   |------|------|
   | RecInfo.txt パス | 例: `\\録画機\c\ap\edcb\Setting\RecInfo.txt` |
   | Reserve.txt パス | 例: `\\録画機\c\ap\edcb\Setting\Reserve.txt` |
   | 録画フォルダ | 例: `\\録画機\d\PT2`（追っかけ再生用） |
   | MPC-BE パス | 例: `C:\ap\MPC-BE.1.8.1.x64\mpc-be64.exe` |

設定は `settings.json`（exe と同じフォルダ）に保存されます。

## 注意

- Reserve.txt には**絶対に書き込みません**（読み取り専用）
- ファイルは最小限の時間だけ開き、EpgTimerSrv の書き込みを妨げないよう設計しています

## ビルド

```
dotnet publish -c Release -r win-x64 --self-contained false
```

出力先: `publish\`（プロジェクトルート直下、csproj の `<PublishDir>` で固定）
