# ドキュメント

## ソフトウェアコントローラのプリセット・ギャラリー

[controller-presets.html](controller-presets.html) は、LLMCon のウェブ版仮想コントローラのプリセット（14種類）を、実際に動く状態で並べて見られるギャラリーです。

GitHub の画面上ではこのファイルはソースとして表示されるだけで、描画はされません。実際の見た目を見るには、次のいずれかにしてください。

- このファイルをダウンロードして、ブラウザで開く（単体で描画できます。動くデザインも動きます）。
- あるいは GitHub Pages を有効にして、公開ページとして描画する。

プリセットそのものの実体（アプリが配信する HTML）は、[../CntlLevelConnection/ControllerPresets.cs](../CntlLevelConnection/ControllerPresets.cs) にあります。アプリを起動して、ブラウザで `http://127.0.0.1:8777/vcon.html` を開き、MCP のツール `set_controller_preset` に名前を渡すと、その場でそのデザインに切り替わります。
