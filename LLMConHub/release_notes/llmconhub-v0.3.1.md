LLMCon Hub v0.3.1 では、.NET の実行環境を .NET 10 に上げました。

## 変わったこと

- **.NET 10 で動きます。** .NET 8 の保守は 2026年11月10日に終わります。配布物には .NET 10 の実行環境を同梱しているので、Raspberry Pi に .NET を入れる必要はありません。仮想のパッドで100回ずつ測ると、入力から出力までの時間は .NET 8 のときとほぼ同じでした（USB の経路で中央値0.80ミリ秒、Bluetooth の経路で中央値1.31ミリ秒）。
- **1台の形でも、読むコントローラを選べます。** `/etc/llmconhub.conf` に `PAD=auto:2` のように書くと、2つ目に挿したコントローラを読みます。
- **PC には Bluetooth でつなぐことを勧めます。** Windows の PC で Steam が動いているとき、USB の Pro コントローラがつながった直後に、Xbox Game Bar などが勝手に開くことがありました（毎回ではありません）。LLMCon Hub はそのあいだ何も押しておらず、Bluetooth では起きませんでした。Nintendo Switch には、USB でも問題なくつながります。
- **調べるための設定を足しました。** USB の記述子の形（`PROCON_DESC=vendor`）、報告の充電グリップの印（`LLMCONHUB_PROCON_GRIP=0`）、USB のやりとりの記録（`LLMCONHUB_USB_TRACE`）を選べます。詳しくは README にあります。

## インストール

これまでと同じく、Raspberry Pi OS Lite（64-bit、Trixie 以降）の上で次の1行を実行し、終わったら再起動します。すでに入れている場合も、同じ1行で新しい版に入れ替わります。すでにある `profile.json` は残ります。

```bash
curl -fsSL https://raw.githubusercontent.com/qurihara/LLMCon/main/LLMConHub/install.sh | sudo bash
```

詳しくは [LLMConHub/README.md](https://github.com/qurihara/LLMCon/blob/main/LLMConHub/README.md) を読んでください。
