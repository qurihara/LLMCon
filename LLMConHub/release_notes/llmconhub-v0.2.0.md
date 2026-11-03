LLMCon Hub v0.2.0 では、1台の Raspberry Pi で LLMCon を2つ同時に動かし、2台の Pro コントローラとして使えるようにしました。

## 新しくできること

- `/etc/llmconhub.conf` に `MODE=dual` と書くと、2つのコントローラが同時に立ち上がります。
  - 1P：USB の Pro コントローラ。MCP は 8777番。1つ目の物理のゲームパッドを読む。
  - 2P：Bluetooth の Pro コントローラ。MCP は 8778番。2つ目の物理のゲームパッドを読む。
- 格闘ゲームのハンディキャップの対戦のように、2人の入力をそれぞれ LLM が改変する遊び方を、1台の Hub だけで受け持てます。
- 物理のゲームパッドは、挿した順に 1P と 2P に割り当たります。挿し直したときも、それぞれのコントローラが自分の順番のパッドを拾い直します。

`MODE` を書かなければ、これまでと同じく、USB でつながっていれば USB の、そうでなければ Bluetooth の Pro コントローラとして1台だけ動きます。

## 確かめたこと（2026年11月3日）

Windows の PC（Steam）に、USB と Bluetooth の2台の「Nintendo Switch Pro Controller」として認識されました。USB のほうには 1P に送った入力だけが、Bluetooth のほうには 2P に送った入力だけが届きました。

## インストール

これまでと同じく、Raspberry Pi OS Lite（64-bit、Trixie 以降）の上で次の1行を実行し、終わったら再起動します。すでに入れている場合も、同じ1行で新しい版に入れ替わります。

```bash
curl -fsSL https://raw.githubusercontent.com/qurihara/LLMCon/main/LLMConHub/install.sh | sudo bash
```

詳しくは [LLMConHub/README.md](https://github.com/qurihara/LLMCon/blob/main/LLMConHub/README.md) を読んでください。
