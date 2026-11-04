# LLMCon Hub

LLMCon Hub は、LLMCon を Raspberry Pi の上で動かし、Raspberry Pi そのものを Nintendo Switch の Pro コントローラとしてゲーム機や PC につなぐためのものである。
ゲームの側からは、ふつうの Pro コントローラに見える。物理のゲームパッド、ウェブ版コントローラ、大規模言語モデルからの入力を1か所に集め、改変してから、ゲーム機や PC へ出すので、Hub と名づけた（2026年11月2日に LLMConBT から改名した）。ゲームにも、ゲーム機や PC にも、手を加えない。

大規模言語モデルは、別の機械から MCP で Raspberry Pi の上の LLMCon に規則を伝える。LLMCon は、物理のゲームパッドやウェブ版コントローラや大規模言語モデルからの入力を規則で改変し、Bluetooth か USB ケーブルでゲーム機や PC へ届ける。

```
 大規模言語モデル（Claude など）──MCP（Tailscale など）──┐
 ウェブ版コントローラ（スマートフォンのブラウザ）──Wi-Fi──┤
 物理のゲームパッド ──USB-A──────────────────────────────┤
                                                         ▼
                                    Raspberry Pi 4（LLMCon Hub）
                                      LLMCon のコア（規則で入力を改変）
                                      MCP サーバとウェブ版コントローラ
                                      Pro コントローラとして名乗る部分
                                         │                    │
                                   Bluetooth（nxbt）    USB-C（USB gadget）
                                         ▼                    ▼
                              Nintendo Switch、PC の Steam などのゲーム
```

## 確かめたこと（2026年11月3日の時点）

- Raspberry Pi 4 Model B（Raspberry Pi OS Lite 64-bit、Trixie）の上で、LLMCon のコア、MCP サーバ、ウェブ版コントローラが動いた。
- Bluetooth で Windows の PC につなぎ、Steam が「Nintendo Switch Pro Controller」として認識した。Steam から起動したストリートファイター6 で、MCP から送ったボタンと十字キーが効いた。
- Bluetooth で Nintendo Switch 2 につなぎ、「持ちかた/順番を変える」の画面で Pro コントローラとして認識された。本体の設定のボタンの動作確認の画面で、MCP から送ったボタン、十字キー、左右のスティック、同時押し、連打、長押しがすべて反応した。
- USB-C で Windows の PC（ROG Ally）につなぎ、Steam が「Nintendo Switch Pro Controller（USB）」として認識した。Steam のテストの画面で、MCP から送ったボタン、十字キー、左右のスティックが反応した。このとき Raspberry Pi 4 は、PC の USB-C の口からの電気だけで動き、電圧の低下の記録は出なかった（物理のパッド1個をつないだ軽い使い方の場合）。
- Raspberry Pi の USB-A の口に物理のゲームパッド（BUFFALO BGC-FC801）をつなぎ、その入力が USB-C を通って PC の Steam に届いた。
- 同じ物理のパッドの入力が、Bluetooth でも PC の Steam に届いた。Bluetooth の Pro コントローラとして、Raspberry Pi の側から PC へつなぎ直せた（`--reconnect`）。
- 物理のパッドの入力は、USB でも Bluetooth でも、目で見て遅れなく反応した（2026-11-03、栗原さんが確かめた）。
- USB-C で Nintendo Switch 2 につなぎ（「Proコントローラーの有線通信」をオン）、ボタンの動作チェックの画面で、MCP から送った25の手順（A、B、X、Y、L、R、ZL、ZR、−、＋、両スティックの押し込み、十字キーの4方向、両スティックの4方向、同時押し）がすべて反応した。物理のパッドの入力も遅れなく届いた。Raspberry Pi 4 は Switch 2 の USB の口からの電気だけで動いたが、電圧の低下の記録（throttled=0x50005）が出続けた。
- Bluetooth で Switch 2 につなぎ直し、同じ25の手順と物理のパッドの入力が、遅れなく反応した。
- 手順を流す道具は `tools/button_check.py` にある。
- Raspberry Pi の側から Switch 2 へ、「持ちかた/順番を変える」の画面を開かずにつなぎ直せた（`RECONNECT=<Switch のアドレス> ./run.sh bt`）。
- 遅延（2026-11-03、Raspberry Pi 4 の中に作った仮想のゲームパッドで100回押して離し、Raspberry Pi の同じ時計で測った。測定は Raspberry Pi の中で完結する）。
  - USB の経路：パッドの入力の事象から、USB の報告を書き終えるまで、中央値 0.73 ミリ秒、95パーセンタイル 1.73 ミリ秒、最大 9.37 ミリ秒（200の変化のすべて）。
  - Bluetooth の経路：パッドの入力の事象から、Bluetooth の送り手（nxbt）へ渡し終えるまで、中央値 1.42 ミリ秒、95パーセンタイル 2.00 ミリ秒（200の変化のすべて）。
  - このあとの、USB の読み取りの間隔や Bluetooth の無線の区間で増える時間は、まだ測っていない。
- 初代の Nintendo Switch は扱わない（2026-11-03 に決めた）。
- 1台の Raspberry Pi 4 で LLMCon を2つ同時に動かし（`MODE=dual`）、USB の Pro コントローラ（1P、MCP は 8777番）と Bluetooth の Pro コントローラ（2P、MCP は 8778番）を同じ PC につないだ。Steam は2台の Nintendo Switch Pro Controller として認識し、テストの画面で、1P に送った入力は USB のほうだけに、2P に送った入力は Bluetooth のほうだけに出た。さらに、物理のパッドを2つ（スーパーファミコン型のパッドと、GP2040-CE のアーケードコントローラ）つなぎ、Steam から起動したストリートファイター6 で、2人がそれぞれのキャラクターを操作できた（2026-11-04、栗原さんが確かめた）。

## フォルダ

- `src/LLMConHub/`：部品。電文（PadFrame）、UDP とシリアルの出力先、Linux の evdev からの物理のゲームパッドの読み取り、USB の有線の Pro コントローラの手順（ProconProtocol、ProconUsbSink）。
- `src/LLMConHub.Host/`：Raspberry Pi の上で動くホスト。LLMCon の画面を持たないホストと同じ引数に、出力先（`--sink`）と物理のゲームパッドの指定（`--pad`）を足したもの。
- `tests/LLMConHub.Tests/`：試験。
- `bridge/llmconhub_bridge.py`：ホストから状態を受け取り、nxbt に渡して Bluetooth の Pro コントローラとして出す。
- `pi/`：Raspberry Pi の上で使う台本。`setup_pi.sh`（最初の設定）、`procon_gadget.sh`（USB の機器を作る）、`run.sh`（起動）。
- `tools/`：mac で使う道具。microSD への書き込み、最初の設定（cloud-init）の作成、Raspberry Pi 向けの組み立てと転送。

LLMCon のコア（`CntlLevelConnection.Core`）には手を加えていない。

## インストール（ワンコマンド）

Raspberry Pi 4（Raspberry Pi OS Lite 64-bit、Trixie 以降）の上で、次の1行を実行し、終わったら再起動する。

```bash
curl -fsSL https://raw.githubusercontent.com/qurihara/LLMCon/main/LLMConHub/install.sh | sudo bash
```

```bash
sudo reboot
```

この1行は、GitHub の Releases から組み立て済みの LLMCon Hub（.NET の実行環境を同梱、約41MB）を取ってきて `~/llmconhub` に広げ、USB の機器の口、Bluetooth の名乗り（Pro コントローラの番号）、nxbt を用意し、起動時に立ち上がるサービス（llmconhub）を入れる。再起動のあとは、電源を入れるだけで立ち上がる。

- 相手（Switch や PC）と USB-C でつながっていれば USB の Pro コントローラとして、つながっていなければ（AC アダプタで給電しているときなど）Bluetooth の Pro コントローラとして動く。
- Switch では、USB のときは「設定」→「コントローラーとセンサー」の「Proコントローラーの有線通信」をオンにする。Bluetooth のときは「持ちかた/順番を変える」の画面を開く。PC では「Bluetooth デバイスの追加」から「Pro Controller」を追加する。Steam から起動したゲームで使う。
- 物理のゲームパッドは USB-A の口につなぐ。起動のあとで挿したパッドも拾う。ボタンと軸の割り当ては、次の順に決まる。
  1. `~/llmconhub/profile.json` に、その機体（VID と PID）の項目があれば、それを使う。
  2. 無ければ、SDL のコントローラの対応表（`gamecontrollerdb.txt`、SDL_GameControllerDB、Linux の約750機種）を引く。手元のコントローラの多くは、挿すだけで正しく読める。
  3. どちらにも無ければ、MCP の道具 `pad_setup_start`、`pad_setup_next`、`pad_setup_finish` で決める。大規模言語モデルがプレイヤに「上の段のいちばん左を押してください」のように、ボタンの位置で順に頼み、押された番号から割り当てを決めて `profile.json` に保存する。天板に A や B の印字の無いアーケードコントローラでも決められる。
  - MCP の `get_pad_raw` は、いまの割り当てがどこから来たか（プロファイル、SDL の対応表、既定）を表示する。
  - 攻撃のボタンの好みは、ゲームの側の設定で変えればよい。LLMCon の名前（A、Start など）は位置で決まるので、改変の規則や大規模言語モデルの自動操作は、どのコントローラでも同じ名前で働く。
- MCP サーバは `http://<Raspberry Pi の番地>:8777/` で待ち受ける。既定では Tailscale の番地にだけ開く（Tailscale が無ければ 127.0.0.1）。離れた機械の大規模言語モデルから使うときは、Raspberry Pi に Tailscale を入れる。
- 形を決め打ちしたいとき、Bluetooth で前の相手へ Raspberry Pi の側からつなぎ直したいときは、`/etc/llmconhub.conf` に `MODE=bt`、`RECONNECT=AA:BB:CC:DD:EE:FF` のように書く。書ける項目は `pi/autostart.sh` の先頭にある。
- 1台で 1P と 2P の2つのコントローラを出すときは、`/etc/llmconhub.conf` に `MODE=dual` と書く。1P は USB の Pro コントローラ（MCP は 8777番、1つ目の物理のパッド）、2P は Bluetooth の Pro コントローラ（MCP は 8778番、2つ目の物理のパッド）になる。格闘ゲームのハンディキャップの対戦を、1台の Hub だけで受け持てる。
- 動きの記録は `journalctl -u llmconhub -f` で読める。サービスを外すときは `sudo ~/llmconhub/pi/install_service.sh remove`。

## 自分で組み立てるとき

1. microSD のカードに Raspberry Pi OS Lite（64-bit、Trixie 以降）を書き込み、最初の設定（機械の名前、利用者、SSH の鍵、Wi-Fi）を書く。mac では `tools/flash_sd_mac.sh` と `tools/make_cloudinit_mac.sh` が使える。Raspberry Pi Imager を使ってもよい。
2. mac か PC で `tools/publish_pi.sh 利用者名@機械の名前` を実行し、Raspberry Pi 向けに組み立てて写す（.NET の実行環境を同梱する）。Releases に置く配布物は `tools/make_release_pi.sh` で作る。
3. Raspberry Pi の上で `sudo ~/llmconhub/pi/setup_pi.sh` を実行し、再起動する。
4. 手で立ち上げるときは、Bluetooth なら `~/llmconhub/pi/run.sh bt`、USB なら `~/llmconhub/pi/run.sh usb` を実行する。起動時に立ち上げるときは `sudo ~/llmconhub/pi/install_service.sh` を一度だけ実行する。

## 実機で分かった注意

- 十字キーを2本の軸で報告する安いパッド（BUFFALO BGC-FC801 など）では、`set_pad_axes` に `lx=-1 ly=-1 dpadX=0 dpadY=1` を渡すと、十字キーとして扱う。ボタンの割り当ては `set_pad_buttons` で合わせる（BGC-FC801 では `0=B,1=A,2=Y,3=X,4=LB,5=RB,6=Back,7=Start`。LLMCon は位置で Switch に合わせるので、パッドの A（右）を LLMCon の B にする）。
- 安いパッドには、最初にボタンが押されるまで何も報告しないものがある。そのあいだ、スティックと十字キーの軸は中央として扱う。
- Windows の標準の仕組み（汎用の HID の読み方）は、USB の Pro コントローラの報告をボタンとして読まない。本物の Pro コントローラでも同じである。Steam は自分で読み直すので、Steam を通せば効く。
- Steam のテストの画面は、Switch の B の長押しと＋で閉じる。確かめるときは、この2つを押さない。
- 格闘ゲーム用のスティック（GP2040-CE の PS4 の形など）は、Linux の標準の読み取りでボタンの名前と位置が合わない。GP2040-CE の PS4 の形は SDL の対応表（Razer Panthera PS4）で正しく読める。対応表に無い機体は、`pad_setup` で決めるか、`profile.json` に `"asJoystick": true` と軸の割り当てを書く。
- ストリートファイター6 は、タイトル画面で最初にボタンを押したコントローラを1P にする。ほかのコントローラ（PC につないだアケコンなど）を使っていたときは、それを外してからタイトル画面で押す。
- nxbt は、Switch との接続が2回切れると、その Switch のペアリングの情報を BlueZ から消す（nxbt/controller/server.py）。消えたあとは、Raspberry Pi の側からつなぎ直せないので、「持ちかた/順番を変える」の画面でペアリングし直す。
- Switch 2 の USB の口からの電気だけでも Raspberry Pi 4 は動いたが、電圧の低下の記録が出続けた。長く使うなら、電源とデータを分ける部品を使う。
- Steam（SDL の Switch 用の処理）は、スティックの届く範囲を、最初は較正の値の 70% と仮定し、それを超える値が届くたびに広げる。そのため、まだ端まで倒していない向きに半分倒すと、約 0.71 と表示される。どの向きにも一度ずつ端まで倒せば、比例する値になる。Switch 本体では端まで届くように、LLMCon Hub は較正の値いっぱいの範囲を送る。

- Bluetooth の Device ID は、最初から Pro コントローラの番号（製造元 057E、製品 2009）にしておく。`setup_pi.sh` が `/etc/bluetooth/main.conf` に書く。既定の番号のまま一度 Windows につなぐと、Windows はその番号をレジストリ（`HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<アドレス>` の FingerprintString）に覚え、「デバイスの削除」や再起動のあとも使い続ける。直すには、管理者の権限でこのキーを消してから追加し直す。
- Bluetooth は従来の方式（BR/EDR）だけにする。本物の Pro コントローラは BLE で名乗らない。
- nxbt は、相手から副命令 0x48（振動）と 0x30（プレイヤーのランプ）を受け取ると「つながった」に進む。Switch と Steam は自分で送る。
- Switch 2 で、ペアリングのあと最初に画面を移したときに接続が切れたことが1回あった。ブリッジを起動し直してペアリングし直すと、画面を移しても切れなかった。切れたときの通信は記録できていない。再び切れたら、`NXBT_DEBUG=1` を付けてブリッジを起動し、`btmon -w` で通信を記録する。
- nxbt 0.1.4 は古い版の部品に固定していて、Python 3.13 では入らない。`setup_pi.sh` は、Debian の python3-dbus を使い、nxbt を版の固定なしで入れ、SIGTERM の受け口を直す。
- Raspberry Pi 4 の4つの USB-A の口は親（ホスト）の役割しか持たず、USB の機器としては USB-C の口だけが振る舞える。この USB-C の口は電源の入口も兼ねるので、USB ケーブルでつなぐときは、電源とデータを分ける部品が要る見込みである。
- 一度起動したあとで Wi-Fi のパスワードを直すときは、カードの network-config を直すだけでは足りない。Raspberry Pi の `/etc/netplan/90-NM-*.yaml` を消し、`sudo cloud-init clean` を実行して再起動する。

## 第三者の部品と資料

- [nxbt](https://github.com/Brikwerk/nxbt)（MIT）：Bluetooth の Pro コントローラの模倣に使う。`setup_pi.sh` が Raspberry Pi の上で取得して入れる。このリポジトリには含めない。
- USB の有線の Pro コントローラの手順は、[dekuNukem の解析資料](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) と、本物の Pro コントローラと Switch のあいだのやり取りの記録を読んで書き起こした。資料のコードは写していない。
- [SDL_GameControllerDB](https://github.com/mdqinc/SDL_GameControllerDB)（zlib License）：`gamecontrollerdb.txt` を手を加えずに同梱する。ライセンスの表示は `third_party/SDL_GameControllerDB_LICENSE.txt` にある。
- BlueZ、Raspberry Pi OS、.NET の実行環境は、それぞれの許諾に従う。.NET の実行環境は組み立てのときに同梱する。
