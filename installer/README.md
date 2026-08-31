# LLMCon の配布とインストール

LLMCon は、MCP で制御できる仮想ゲームコントローラです。外付けの USB ゲームパッドの入力を受け取り、
ルールで改変して仮想 Xbox 360 コントローラとして出力できます。MCP サーバは HTTP で公開されます。

配布は、ひとつのインストーラにまとめられます。ただし、本体の実行ファイルとは別に、ViGEmBus という
カーネルモードのドライバが必要です。ドライバは実行ファイルの中に含められないため、インストーラが
別途導入します。

## 1. 単一ファイルの実行ファイルを作る

リポジトリのルートで、次のコマンドを実行します。`.NET 8 SDK` が必要です。

```powershell
dotnet publish CntlLevelConnection\CntlLevelConnection.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

これで `publish\CntlLevelConnection.exe` がひとつだけ作られます。.NET と ASP.NET Core と WPF の
ランタイムを同梱しているので、.NET が入っていないパソコンでも動きます（サイズはおよそ 200 MB です）。

## 2. インストールする（2つのやり方）

### やり方 A: PowerShell スクリプト（手軽）

`installer\install-llmcon.ps1` を実行します。次の3つを行います。

1. ViGEmBus ドライバが無ければ winget で導入する（ドライバ導入時に管理者の確認が出ます）
2. 実行ファイルを `%LOCALAPPDATA%\LLMCon\LLMCon.exe` へコピーする
3. スタートメニューにショートカットを作る

### やり方 B: Inno Setup でひとつのインストーラを作る（配布向け）

`installer\LLMCon.iss` を Inno Setup（https://jrsoftware.org/isinfo.php ）の `ISCC.exe` で
コンパイルすると、`LLMCon-Setup.exe` ができます。これがひとつのインストーラで、ViGEmBus ドライバの
確認と導入、本体のインストール、ショートカットの作成までをまとめて行います。

### 固定運用の製品を作る（プロファイル同梱のインストーラ）

`installer\KuriCon.iss` は、同じ実行ファイルにプロファイル（profiles/kuricon.json）を
同梱し、ショートカットに `--profile` を付けて起動する「格闘ゲーム用の固定コントローラ」だけの
単体インストーラ（KuriCon-Setup.exe）を作る例です。プロファイルがデザインを固定し、
マイクのしきい値ボタンを既定で有効にします。別の固定製品を作るときは、この定義とプロファイルを
複製して差し替えます。

KuriCon 用の実行ファイルは、別に書き出します。製品の名前と説明を実行ファイルに埋めるためです
（通知の見出しなどに、内部の名前が出ないようにする。Issue #20）。リポジトリのルートで実行します。

```powershell
dotnet publish CntlLevelConnection\CntlLevelConnection.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:Product=KuriCon -p:AssemblyTitle=KuriCon `
  -o publish-kuricon
```

`AssemblyTitle` は、実行ファイルの「説明」になります。Windows の通知の見出しは、製品名ではなく
この説明を読みます。指定を忘れると、利用者の画面に内部の名前が出ます。

## 3. 使い方

1. スタートメニューの LLMCon から起動します。
2. 起動すると、MCP サーバが `http://127.0.0.1:8777/` で待ち受けます。
3. お使いの大規模言語モデルのクライアント（たとえば Claude Code）に、この MCP サーバを登録します。

   ```
   claude mcp add --transport http llmcon http://127.0.0.1:8777/
   ```

4. 2台目を動かすときは、コマンドラインでポートと名前を分けて起動します。

   ```
   LLMCon.exe --port 8778 --name 2P
   ```

   そのうえで、2台目を別の名前で登録すると、ひとつの大規模言語モデルから複数のコントローラを
   またいで制御できます。

## 4. 物理パッドをゲームから隠す（HidHide）

LLMCon が物理パッドを読んで改変し、仮想コントローラとして出しても、ゲームからは物理パッドと
仮想コントローラの両方が見えています。ゲームが両方を読むと、生の入力が改変を打ち消します。
反転のように「押していないときに On」にする改変は、この影響を最も強く受けます。

これを防ぐには HidHide ドライバを入れます。ViGEmBus と同じ作者による、特定の機器を特定の処理
からだけ見えるようにするドライバです。

```powershell
winget install --id Nefarius.HidHide
```

導入だけが管理者権限を要します。そのあとの設定の変更（隠す、戻す）は LLMCon が自分で行うので、
利用者に管理者の確認は出ません。KuriCon のインストーラは、これを自動で導入します。

導入したら、次の3つに注意してください。

1. **コントローラを挿し直してください。** HidHide は機器が認識されるときに階層へ組み込まれるので、
   導入より前から挿さっていた機器には、挿し直すか再起動するまで効きません。
2. **LLMCon を先に起動し、それからゲームを起動してください。** 隠す仕組みは「新しく開く」のを
   止めるものなので、すでに起動しているゲームやブラウザには効きません。順序が逆だったときは、
   ゲームを開き直してください。
3. **隠しているあいだ、そのパッドは他のすべてのアプリから見えません。** Steam や Windows の設定
   画面にも出なくなります。タスクトレイの「パッドをゲームから隠す」から解除できます。LLMCon を
   終了すれば必ず戻り、異常終了した場合も次の起動で戻ります。

隠すかどうかは、MCP のツール `set_pad_hidden`、タスクトレイ、プロファイルの `hidePads`、
起動引数の `--hide-pads` のいずれからでも切り替えられます。いま何を隠しているかは `get_info` と
`get_state` に出ます。
