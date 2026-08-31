; LLMCon インストーラ定義（Inno Setup 用）
;
; このファイルは Inno Setup の ISCC.exe でコンパイルすると、ひとつの LLMCon-Setup-0.3.exe を作ります。
; その LLMCon-Setup は、ViGEmBus ドライバの確認と導入、本体のインストール、ショートカットの作成を行います。
;
; 物理パッドをゲームから隠す機能（set_pad_hidden）には HidHide ドライバも要りますが、こちらは
; 導入しません。汎用の LLMCon では隠蔽は既定で切ってあり、研究用の機械に余分なドライバを入れない
; ほうがよいためです。使うときは winget install --id Nefarius.HidHide を手で実行してください。
; 格闘ゲーム用の製品（KuriCon）のインストーラは、隠蔽が中心の機能なので HidHide も導入します。
;
; コンパイルの前提:
;   1. 先に publish フォルダを作っておくこと（installer/README.md を参照）。
;   2. Inno Setup（https://jrsoftware.org/isinfo.php）を入れて、ISCC.exe でこのファイルをコンパイルすること。

#define MyAppName "LLMCon"
#define MyAppVersion "0.3.4"
#define MyAppExe "LLMCon.exe"

[Setup]
; AppId はアップグレード時の同一性の識別子。変えると別アプリ扱いになるので、今後も変えないこと。
AppId={#MyAppName}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppPublisher=Kazutaka Kurihara
DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; 新しい版のインストーラを実行すると、同じ場所へ上書きしてアップグレードになる。
; 実行中なら、先にアプリを閉じるよう促す。
CloseApplications=yes
SetupIconFile=LLMCon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=.
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes

[Files]
; publish フォルダの単一ファイル実行ファイルを取り込み、LLMCon.exe という名前で置く
Source: "..\publish\CntlLevelConnection.exe"; DestDir: "{app}"; DestName: "{#MyAppExe}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"

; 消すための入口を、スタートメニューにも置く。
; Windows 11 の「設定」の「インストールされているアプリ」に、この製品が出てこないことがある
; （利用者ごとの場所へ入れる形だと出ないことがある。2026/8/7 に実機で確認した）。
; 設定から消せないと、利用者は消す方法を失う。ここに置けば必ず辿り着ける。
; KuriCon 側には最初から置いてあった。こちらにも同じものを置く（Issue #24）。
Name: "{autoprograms}\{#MyAppName} をアンインストール"; Filename: "{uninstallexe}"

[Languages]
; 画面の文言を日本語にする。Japanese.isl は Inno Setup に同梱されている。
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Run]
; ViGEmBus ドライバが無ければ winget で導入する（ドライバ導入には管理者権限が必要なので、ここで昇格の確認が出る）
;
; 閉じ波括弧を重ねてはならない。{{ は Inno の逃がし方だが、}} にその決まりは無く、そのまま2つ渡って
; PowerShell が構文の誤りで止まる。2026/8/7 に、この書き方のせいでドライバの導入が一度も
; 走っていなかったことが分かった。
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""if (-not (Get-Service ViGEmBus -ErrorAction SilentlyContinue)) {{ Start-Process winget -Verb RunAs -Wait -ArgumentList 'install','--id','ViGEm.ViGEmBus','--exact','--silent','--accept-package-agreements','--accept-source-agreements' }"""; \
  StatusMsg: "ViGEmBus ドライバを確認・導入しています..."; Flags: runhidden

; インストール後に起動するかどうかを選べるようにする
Filename: "{app}\{#MyAppExe}"; Description: "LLMCon を起動する"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; LLMCon は HidHide を導入しませんが、利用者が自分で導入して set_pad_hidden を使うことはできます。
; その状態でアンインストールすると、隠蔽だけが残り、戻す道具が消えます。KuriCon と同じ備えを置きます。
; 場所を ProgramW6432 で指すのは、インストーラが 32bit のモードで走るためです。
Filename: "{cmd}"; \
  Parameters: "/c if exist ""%ProgramW6432%\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"" ""%ProgramW6432%\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"" --cloak-off"; \
  Flags: runhidden; RunOnceId: "HidHideCloakOff"



