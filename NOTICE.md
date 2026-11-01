# 第三者の権利について

このリポジトリの中身は、断りがなければ [LICENSE](LICENSE) に従う。以下は例外と注意である。

## 論文の事例で用いたゲーム

論文の応用事例では、権利の扱いが明確な著名ゲームの再実装3つを対象にした。
**それらのコードはこのリポジトリに含めていない。** 配布元から各自で取得すること。

| ゲーム | 配布元 | ライセンス |
|---|---|---|
| スペースインベーダーの再実装 | https://github.com/dwmkerr/spaceinvaders | MIT |
| テトリスの再実装 | https://github.com/jakesgordon/javascript-tetris | MIT |
| アステロイドの再実装 | https://github.com/kevinroast/Asteroids-Reloaded | 配布元の記載に従うこと |

**いずれのゲームのコードにも手を加えていない。** LLMCon は外側からコントローラの入力を
渡すだけなので、対象のゲームを改変する必要はない。

`games/` に入っているのは、動作を確かめるために自分で書いた小さなゲームであり、
上記の再実装とは別のものである。

## 落ち物パズル異種格闘技戦の動画で用いたゲーム

活用事例の動画 `video/fallblock_versus_with_llmcon_v2.mp4` では、次の4つの再実装を用いた。
**それらのコードはこのリポジトリに含めていない。** いずれのゲームのコードにも手を加えていない。

| ゲーム | 配布元 | ライセンス |
|---|---|---|
| テトリスの再実装 | https://github.com/jakesgordon/javascript-tetris | MIT |
| ぷよぷよの再実装 | https://github.com/takekawa/canvas-puyo | README に記載の MIT |
| ドクターマリオ風のゲーム | https://github.com/you-sk/drmario-clone | MIT |
| コラムスの再実装 | https://github.com/jcfieldsdev/columns | MIT |

4つとも、描画はプログラムによる図形で、元の市販のゲームの図案は入っていない。
コラムスの再実装の背景の写真は Pexels のもので、配布物に撮影者の表記がある。

## 市販のゲーム

論文では市販の対戦格闘ゲームでの動作も報告している。市販のゲームのコードと画面写真は、
このリポジトリに含めていない。

ただし、ストリートファイター6での事例の紹介動画 `video/sf6_llmcon_intro.mp4` には、
事例の動作を示すために、ゲームの画面が映っている。この画面の権利は株式会社カプコンにある
（© CAPCOM）。ゲームのコードやデータには手を加えておらず、LLMCon はコントローラの入力を
書き換えているだけである。

また、Jump King の自動攻略の事例の動画 `video/jumpking_llmcon_autoplay_v2.mp4` にも、
事例の動作を示すために、ゲームの画面が映っている。この画面の権利は Nexile にある。
この事例で使った Steam ワークショップの MOD と、第三者の模擬環境 jumpking-rl は、このリポジトリに含めていない。
ゲームのコードやデータには手を加えておらず、LLMCon はコントローラの入力を送っているだけである。

LLMCon を市販のゲームに使えるかどうかと、その利用条件は、各ゲームの規約に従って各自で判断すること。
対戦の場で用いる場合の注意は README の「使ってよい場面」に書いた。

## 依存しているもの

実行に必要な外部の部品は次のとおりで、それぞれのライセンスに従う。

| 部品 | 用途 | ライセンス |
|---|---|---|
| ModelContextProtocol / ModelContextProtocol.AspNetCore | MCP サーバ | MIT |
| Nefarius.ViGEm.Client | 仮想の Xbox 360 コントローラを作る | MIT |
| NAudio | マイクの入力を取り込む | MIT |
| ViGEmBus（ドライバ） | 同上。利用者の機械へ別途導入する | 配布元の条件に従う |
| HidHide（任意） | 物理のコントローラを他のアプリから隠す | 配布元の条件に従う |

ViGEmBus と HidHide は、このリポジトリでは配布していない。導入の手順は
`installer/README.md` と `docs/manual.md` に書いた。

## 印刷や機材の設定

導電性の素材で作った物理コントローラなど、論文で触れた機材の造形データは含めていない。
機種と個体と材料に強く依存し、そのまま使うとかえって失敗するためである。
