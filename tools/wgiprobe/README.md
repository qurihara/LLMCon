# wgiprobe

Windows.Gaming.Input が、窓を持つ処理と持たない処理とで振る舞いを変えるかを測る道具。
物理パッドの素性調べ（VID/PID、軸とボタンの数、対応表）にも使う。

同じ実行ファイルを `--window` の有無で走らせて比べるので、比較の対象がずれない。

## なぜ対照経路を持つのか

「値が来ない」には理由が三つあり、素朴に測ると取り違える。

1. WGI が本当に届けていない
2. **誰も触っていない**（素の HID の機体は、最初の入力報告が来るまで時刻印が 0 のまま）
3. 機体が再列挙された（モード切替、再起動、抜き差し）。握っていた WGI のオブジェクトが古くなる

そこでこの道具は、WGI と同時に次を記録する。

- **XInput**（`XInputGetState`）— 窓も前面も要らない。XInput の機体に対する対照
- **winmm**（`joyGetPosEx`）— 窓も前面も要らない。素の HID の機体に対する対照
- **着脱**（`RawGameControllerAdded/Removed`、XInput スロットの接続断）
- **前面ウィンドウの変化**（タイトル付き）

対照経路が入力を見ているのに WGI が見ていなければ、WGI が落としている。
どの経路も入力を見ていなければ、単に押されていない（判定は `Inconclusive` になる）。

## 使い方

```
dotnet build tools/wgiprobe -c Release

wgiprobe                        窓なしで既定 8 秒
wgiprobe --window               窓あり（TopMost、メッセージを回す）
wgiprobe --map                  入力を一つずつ記録する（対応表を作るとき）
wgiprobe --armed                最初の入力を検知してから数え始める
wgiprobe --seconds=40           記録する長さ
```

**`--armed` を使うこと。** 人に「いま押してください」と合図を合わせるのは、まず失敗する。
`--armed` なら最初の入力まで最大 15 分待ち、そこから `--seconds` を数える。

対応表を作るときは `--map --armed --seconds=60` で、一つずつ間を空けて押してもらう。

## 読み方

```
VALUES ARRIVING: YES/NO       WGI が非ゼロの時刻印を返したか
VERDICT: ...                  対照経路と突き合わせた判定
device arrive/remove events   0 でなければ、値が死んだのは古いオブジェクトのせいかもしれない
foreground changes            0 でなければ、前面喪失が効いたかもしれない
```

`kind=XInput (has Gamepad)` か `kind=plain HID (no Gamepad)` かは、
`Gamepad.Gamepads` に対応があるかで自動判定する。**素の HID の機体は
`Gamepad.Gamepads` に現れない**ので、そちらだけを見ていると存在しないことになる。

## 分かっていること

`experiments/wgi-window-rule-2026-08/` を見よ。
