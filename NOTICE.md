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

活用事例の動画 `video/fallblock_versus_llmcon_v2.mp4` では、次の4つの再実装を用いた。
**それらのコードはこのリポジトリに含めていない。** いずれのゲームのコードにも手を加えていない。

| ゲーム | 配布元 | ライセンス |
|---|---|---|
| テトリスの再実装 | https://github.com/jakesgordon/javascript-tetris | MIT |
| ぷよぷよの再実装 | https://github.com/takekawa/canvas-puyo | README に記載の MIT |
| ドクターマリオ風のゲーム | https://github.com/you-sk/drmario-clone | MIT |
| コラムスの再実装 | https://github.com/jcfieldsdev/columns | MIT |

4つとも、描画はプログラムによる図形で、元の市販のゲームの図案は入っていない。
コラムスの再実装の背景の写真は Pexels のもので、配布物に撮影者の表記がある。

## ゲーム接続の7つの事例の動画で用いたゲームと曲

活用事例の動画 `video/game_pairing_llmcon_v5.mp4` は、LLMCon で既存のゲームと別のゲームをつなぐ7つの事例をまとめたものである。
上の節の4つの落ち物パズルに加えて、次のゲームと曲を用いた。**それらのコードと音源はこのリポジトリに含めていない。** いずれのゲームのコードにも手を加えていない。

| ゲーム、曲 | 配布元 | ライセンス |
|---|---|---|
| スペースインベーダーの再実装 | https://github.com/dwmkerr/spaceinvaders | MIT |
| 倉庫番 | https://gist.github.com/straker/2fddb507d4bb6bec54ea2fdb022d020c | CC0 1.0 |
| 2048 | https://github.com/gabrielecirulli/2048 | MIT |
| 恐竜のゲーム（t-rex-runner） | https://github.com/wayou/t-rex-runner | BSD 3条項（Chromium の恐竜のゲームを取り出したもの） |
| StepFever | https://github.com/religa/stepfever | MIT |
| Sunniesnow | https://github.com/sunniesnow/sunniesnow | AGPL-3.0-or-later |
| 曲「Into My Dream」 Lagoona/Andreas Viklund | https://github.com/TeamRizu/OutFox-Serenity （OutFox Serenity Volume 2） | CC BY 4.0 |

「Into My Dream」の譜面は、OutFox Serenity Volume 2 に収められた 48productions と Daniel Rotwind によるものを用いた（2P の譜面は、画面を叩く形に直した）。譜面そのもののライセンスは、配布物に書かれていない。
もぐらたたき、リズムゲーム、奥へ走るロボットのゲームは、大規模言語モデル（Claude）がその場で作ったものである。
動画の冒頭で、百人一首かるた vs もぐらたたき（丸山礼華・栗原一貴、EC2024）の動画（https://www.youtube.com/watch?v=X1BmXal4_qQ ）の一部を引用した。

## 市販のゲーム

論文では市販の対戦格闘ゲームでの動作も報告している。市販のゲームのコードと画面写真は、
このリポジトリに含めていない。

ただし、ストリートファイター6での事例の紹介動画 `video/sf6_llmcon_intro.mp4` と `video/street_presentation_timer.mp4` には、
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
