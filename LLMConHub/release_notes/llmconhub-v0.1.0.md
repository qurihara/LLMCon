LLMCon Hub の最初の版です。Raspberry Pi 4 の上で LLMCon を動かし、Raspberry Pi を Nintendo Switch の Pro コントローラとして、USB か Bluetooth でゲーム機や PC につなぎます。

## インストール

Raspberry Pi OS Lite（64-bit、Trixie 以降）の上で、次の1行を実行し、終わったら再起動します。

```bash
curl -fsSL https://raw.githubusercontent.com/qurihara/LLMCon/main/LLMConHub/install.sh | sudo bash
```

再起動のあとは、電源を入れるだけで立ち上がります。USB でつながっていれば USB の Pro コントローラとして、そうでなければ Bluetooth の Pro コントローラとして動きます。

## 確かめたこと（2026年11月3日）

- Nintendo Switch 2：USB と Bluetooth の両方で、MCP から送った入力（ボタン、十字キー、両スティック、ZL、ZR）と、Raspberry Pi につないだ物理のゲームパッドの入力が届いた。
- Windows の PC（Steam）：USB と Bluetooth の両方で、「Nintendo Switch Pro Controller」として認識され、同じ入力が届いた。
- 遅延：パッドの入力の事象から、USB の報告を書き終えるまで中央値 0.73 ミリ秒、Bluetooth の送り手へ渡し終えるまで中央値 1.42 ミリ秒（各100回）。

詳しくは [LLMConHub/README.md](https://github.com/qurihara/LLMCon/blob/main/LLMConHub/README.md) を読んでください。
