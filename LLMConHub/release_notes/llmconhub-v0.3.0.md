LLMCon Hub v0.3.0 では、手元のコントローラを挿すだけで使えるようにしました。

## 新しくできること

- **コントローラの割り当てが自動で決まります。** SDL のコントローラの対応表（SDL_GameControllerDB、Linux の約750機種）を同梱し、挿したコントローラのボタンと軸の割り当てを自動で引きます。GP2040-CE の基板のアーケードコントローラなども、挿すだけで正しく読めます。
- **対応表に無い機種も、ボタンの位置で決められます。** MCP の道具 `pad_setup_start`、`pad_setup_next`、`pad_setup_finish` を足しました。大規模言語モデルがプレイヤに「上の段のいちばん左を押してください」のように位置で頼み、押されたボタンから割り当てを決めて保存します。天板に印字の無いアーケードコントローラでも使えます。
- **2台の形で、1P と 2P のコントローラを選べます。** `/etc/llmconhub.conf` に `PAD1=auto:2`、`PAD2=auto:1` のように書くと、USB の Pro コントローラ（1P）と Bluetooth の Pro コントローラ（2P）が読むコントローラを入れ替えられます。
- **割り当ての出どころが見えます。** MCP の `get_pad_raw` が、いまの割り当てがプロファイル、SDL の対応表、既定のどれから来たかを表示します。

コントローラの割り当ての決まる順は、`profile.json`、SDL の対応表、既定です。`profile.json` に書いた機種は、これまでどおりその割り当てを使います。

## 確かめたこと（2026年11月4日）

- アーケードコントローラ（GP2040-CE の PS4 の形）が、SDL の対応表で正しく読めた。レバー、8つのボタン、スタートとセレクトが、意図した位置の名前で届いた。
- 1台の Hub に、アーケードコントローラとスーパーファミコン型のパッドをつなぎ、Steam から起動したストリートファイター6 で、2人がそれぞれのキャラクターを操作できた。
- `pad_setup` で、仮想のアーケードコントローラの14の手順がすべて正しく記録され、保存した割り当てが当たった。

## インストール

これまでと同じく、Raspberry Pi OS Lite（64-bit、Trixie 以降）の上で次の1行を実行し、終わったら再起動します。すでに入れている場合も、同じ1行で新しい版に入れ替わります。すでにある `profile.json`（`pad_setup` で保存した割り当てなど）は残し、新しい版のものは `profile.json.dist` として置きます。

```bash
curl -fsSL https://raw.githubusercontent.com/qurihara/LLMCon/main/LLMConHub/install.sh | sudo bash
```

## 第三者の部品

SDL_GameControllerDB の `gamecontrollerdb.txt` を、手を加えずに同梱しています（zlib License）。ライセンスの表示は `third_party/SDL_GameControllerDB_LICENSE.txt` にあります。

詳しくは [LLMConHub/README.md](https://github.com/qurihara/LLMCon/blob/main/LLMConHub/README.md) を読んでください。
