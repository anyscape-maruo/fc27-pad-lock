# 開発・検証

## ビルド

Windows x64の.NET Framework 4.xコンパイラーを使用します。追加のNuGetパッケージはありません。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

生成物は `build/FC27PadLock.exe`、`build/FC27PadLockControl.exe`、`build/FC27PadLockOverlay.exe`、`build/FC27PadLockMonitor.exe`、`build/FC27PadLockUpdater.exe` です。srcにC#コード、AssemblyInfo.csに公開版のメタデータがあります。専用アイコンはassetsに含まれ、5つのEXEに埋め込みます。通常の利用者はControlだけを開き、補助プロセスは自動管理されます。

実行中のEXEは上書きできません。アプリを終了するか、`build/build.ps1 -OutputDirectory <別の出力先>` と `scripts/verify.ps1 -OutputDirectory <同じ出力先>` で隔離したビルドを使います。配布時は `scripts/package.ps1 -BuildDirectory <同じ出力先>` を指定できます。

ControlはDesktopUi.csで共通デザインと独自トレイパネル、TextCatalog.csで翻訳、AppSettings.csで設定保存を扱います。`--preview-ui <絶対PNGパス> --language ja|en` はサンプル画面を描画し、入力フック・保護IPC・設定保存を行いません。`--detach` はUIだけ終了して稼働中のガード・補助プロセスを残す更新用操作です。

オーバーレイはガードへの操作通知を送らず、状態を読み取る別プロセスです。通常はControlから自動起動します。`--standalone` では表示だけの通知領域メニューを持ち、稼働中のガードを変更せず追加できます。通常のアプリ終了ではガードとオーバーレイを終了しますが、単独オーバーレイの終了は保護に作用しません。

## 自己テストと実動作を区別する

verify.ps1は押下・解放、フォーカス変更、退避キー、解除、注入入力の扱いと、操作パネルの状態処理を自己テストします。`--self-test` はガードを起動せず、フックも入力送信も行いません。通常のアプリ起動、--stop、--pause、--resume、--quitは同じWindowsユーザーの稼働版に作用するため、実行中の版と同じセッションで無断検証しないでください。

Windowsの低レベルフックは専用メッセージループのスレッドに設置します。状態保存・プロセス検出は別スレッド、解除通知・試験期限は独立Timerで処理します。コールバックにはディスク操作やUI処理を入れません。

MonitorはFC27が前面のときだけRaw Inputの種別・件数とフックチェーンの結果を観測します。ゲーム内の選択機器や、ゲーム内部で受け取った入力を読み取って検証する機能はありません。status.jsonの `actualMatchVerified` と `rawInputGuaranteed` はfalseであり、フック登録とゲーム内効果を混同しません。新しいGuardを生成した際は、Monitorを後から再登録する順序を保ちます。

## 配布物

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

artifactsにwin-x64 ZIPとSHA256SUMS.txtを作ります。許可リストにあるEXE、起動用ファイル、任意インストール・解除スクリプト、説明書、ライセンスだけを含めます。個人パスを含む実機観察やインストール記録は配布しません。EXEは現時点では署名なしです。

コンパイラーがビルドごとに生成する情報があるため、別環境の再ビルドと配布EXEがバイト単位で一致することは保証しません。配布版のハッシュとソースのコミットをReleaseに記録します。

`scripts/build-setup.ps1 -PayloadZip <配布ZIP> -OutputPath <Setup.exe>` でZIPを埋め込んだ統合インストーラーを生成します。Setupの `--self-test <絶対JSONパス>` は埋め込みZIPの許可リスト・必須ファイル・パストラバーサル対策を検証し、インストールしません。利用者のゲーム・設定・入力を変更せずにCIで実行できます。

## 実機確認の手順

メニューで30秒試験→コントローラー保持・キーボード・クリック・退避・期限終了を確認→期限なし保護でゲーム開始時からコントローラーを選択→オフライン試合やClubsの実動作を確認、の順で記録します。試合中の再起動や自動入力送信を検証の前提にしません。

登録／解除の検証は、テスト用フォルダーのDesktopDirectory・StartupDirectoryを指定して実在のデスクトップを避けてください。終了通知を含む実動作確認は隔離したWindowsセッションやVMで行ってください。

## 利用者向けHTMLマニュアル

配布する利用者向け文書はHTMLです。リポジトリのMarkdownは編集用の原稿として保持しています。原稿を変更したら `python scripts/build-docs.py`（markdown-it-pyが必要）で再生成し、`Manual.html` と `docs/*.html` を更新してください。HTMLはCSSを内包し、オフラインで読めます。利用者にMarkdownを開かせる導線はありません。
