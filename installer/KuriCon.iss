; KuriCon インストーラ定義（Inno Setup 用）
;
; LLMCon を「格闘ゲーム用の固定コントローラ」だけの単体製品として配るためのインストーラを作る。
; 中身は LLMCon と同じ実行ファイルだが、プロファイルを profile.json として実行ファイルの隣に置く。
; アプリは起動時に実行ファイルの隣の profile.json を自動で読むので、ショートカットに引数はいらない
; （絶対パスの引数に依存しないので、どこにインストールされても壊れない）。
;
; コンパイルの前提:
;   1. 先に publish-kuricon フォルダを作っておくこと（installer/README.md を参照）。
;      製品の名前を実行ファイルに埋めるため、KuriCon 用は別に書き出す。
;   2. Inno Setup の ISCC.exe でこのファイルをコンパイルすると KuriCon-Setup-<版>.exe ができる。
;
; 名前について。この製品は 2026/8/7 まで FightingCon という呼び名であった。正式には KuriCon である。
; AppId も変えたので、古い FightingCon が入っている機械では、先に古いほうを消す必要がある。

#define MyAppName "KuriCon"
#define MyAppVersion "0.4.6"
#define MyAppExe "KuriCon.exe"

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
SetupIconFile=KuriCon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=.
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
; インストールの最後に、コントローラを挿し直す必要があることと、起動の順序を伝える。
; どちらも守らないと「隠したのに効かない」という形で現れ、原因に辿り着けない。
InfoAfterFile=KuriCon-after.txt

[InstallDelete]
; 0.1 版が置いていた kuricon.json（今は profile.json を使う）を、アップグレード時に消す
Type: files; Name: "{app}\fighting-mic.json"

[Files]
; 単一ファイルの実行ファイルを KuriCon.exe として置き、プロファイルを profile.json、アイコンを同梱する
; 製品の名前を埋めた実行ファイルを使う。通知の見出しなどに、内部の名前が出ないようにするため（Issue #20）。
Source: "..\publish-kuricon\CntlLevelConnection.exe"; DestDir: "{app}"; DestName: "{#MyAppExe}"; Flags: ignoreversion
Source: "..\profiles\kuricon.json"; DestDir: "{app}"; DestName: "profile.json"; Flags: ignoreversion
Source: "KuriCon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; 引数なし。アプリが実行ファイルの隣の profile.json を自動で読む。
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\KuriCon.ico"

; 消すための入口を、スタートメニューにも置く。
; Windows 11 の「設定」の「インストールされているアプリ」に、この製品が出てこないことがある
; （利用者ごとの場所へ入れる形だと出ないことがある。2026/8/7 に実機で確認した）。
; 設定から消せないと、利用者は消す方法を失う。ここに置けば必ず辿り着ける。
Name: "{autoprograms}\{#MyAppName} をアンインストール"; Filename: "{uninstallexe}"

[Languages]
; 画面の文言を日本語にする。Japanese.isl は Inno Setup に同梱されている。
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Run]
; ViGEmBus ドライバが無ければ winget で導入する（導入済みの環境には触れない）
;
; 波括弧の閉じを重ねてはならない。Inno は開き波括弧を定数の始まりとみなすので {{ と書いて逃がすが、
; 閉じ波括弧にその決まりは無い。}} と書くと、そのまま2つ渡って PowerShell が構文の誤りで止まり、
; 何も起きないまま先へ進む。2026/8/7 に、この書き方のせいでドライバの導入が一度も走っていなかった
; ことが分かった（この機械には両方とも別の経路で入っていたので、長く気づかれなかった）。
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""if (-not (Get-Service ViGEmBus -ErrorAction SilentlyContinue)) {{ Start-Process winget -Verb RunAs -Wait -ArgumentList 'install','--id','ViGEm.ViGEmBus','--exact','--silent','--accept-package-agreements','--accept-source-agreements' }"""; \
  StatusMsg: "コントローラを使うための部品を確認しています..."; Flags: runhidden

; HidHide ドライバが無ければ winget で導入する（導入済みの環境には触れない）。
; これが無いと、ゲームには物理パッドと仮想コントローラの両方が見え、生の入力が改変を打ち消す。
; 導入だけが管理者権限を要し、そのあとの設定の変更は KuriCon が自分で行える。
;
; 場所の判定に ProgramW6432 を使う理由。このインストーラは 32bit のモードで走るので、
; ここから起動する powershell.exe も 32bit 側になり、その $env:ProgramFiles は
; C:\Program Files (x86) を指す。HidHide が入るのは 64bit 側なので、ProgramFiles で
; 判定すると、入っている環境でも「無い」と誤判定して毎回 UAC を出すことになる。
; ProgramW6432 は 32bit の処理からでも 64bit 側を指す（2026/8/7 に nucbox で実測）。
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""if (-not (Test-Path (Join-Path $env:ProgramW6432 'Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe'))) {{ Start-Process winget -Verb RunAs -Wait -ArgumentList 'install','--id','Nefarius.HidHide','--exact','--silent','--accept-package-agreements','--accept-source-agreements' }"""; \
  StatusMsg: "コントローラを使うための部品を追加しています..."; Flags: runhidden

; インストール後に起動するかどうかを選べるようにする
Filename: "{app}\{#MyAppExe}"; Description: "KuriCon を起動する"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; アンインストールのときに、HidHide の隠蔽を必ず止める。
;
; 隠蔽の設定はドライバ側に残り、KuriCon の有無とは無関係に効き続ける。利用者が
; 取りうる最も自然な行動、すなわち「コントローラが認識されないのでアンインストールする」が、
; そのまま回復不能につながる。隠蔽は残り、それを戻す道具だけが消えるためである。
;
; --cloak-off は全体の停止スイッチで、管理者権限が要らない。個々の機器の登録は残るが、
; 隠蔽そのものが止まるのでコントローラは見えるようになる。
;
; 上と同じ理由で、場所は ProgramW6432 で指す。存在の確認は cmd の if exist で行う
; （Inno の skipifdoesntexist は Filename に書いたパスを見るので、32bit 側の
; Program Files を見に行き、黙って飛ばされてしまう）。
Filename: "{cmd}"; \
  Parameters: "/c if exist ""%ProgramW6432%\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"" ""%ProgramW6432%\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe"" --cloak-off"; \
  Flags: runhidden; RunOnceId: "HidHideCloakOff"









