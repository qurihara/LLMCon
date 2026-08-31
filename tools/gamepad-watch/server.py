# watch.html を配り、ページから送られてくるパッドの一覧を記録する小さなサーバ。
#
# なぜサーバに送り返すのか:
#   Chrome は、ページが可視かつ焦点を持ち、さらにゲームパッドのボタンが一度押される
#   まで navigator.getGamepads() を空のままにする（指紋採取の対策）。
#   遠隔から中を覗くのは難しいので、ページ側から結果を送らせる。
#   人が押すのは最初の一回だけでよい。
#
# 使い方:
#   python tools/gamepad-watch/server.py
#   ブラウザで http://127.0.0.1:8899/watch.html を開く
#   記録は tools/gamepad-watch/report.jsonl に増えていく
import json
import os
# ThreadingHTTPServer であること。HTTPServer は一度に一接続しか捌けないので、
# ブラウザが keep-alive の接続を残したまま落ちると、そこで全体が詰まる。
# 実際にそれで「サイトが表示されない」状態になった。
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
LOG = os.path.join(HERE, "report.jsonl")


class Handler(SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=HERE, **kw)

    def do_POST(self):
        if self.path != "/report":
            self.send_error(404)
            return
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode("utf-8", "replace")
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(body + "\n")
        try:
            rec = json.loads(body)
            print(f"[report] {rec.get('count')} pad(s): "
                  + ", ".join(p.get("id", "?") for p in rec.get("pads", [])))
        except Exception:
            print("[report] (unparsed)")
        self.send_response(204)
        self.end_headers()

    def log_message(self, *a):
        pass  # GET のログは黙らせる


if __name__ == "__main__":
    # 追記にすること。作り直しにすると、サーバを入れ直しただけで
    # それまでの観測が消える。実際にそれで前半の記録を失った。
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(json.dumps({"event": "server-start"}) + "\n")
    print("http://127.0.0.1:8899/watch.html  を開いてください")
    print(f"記録: {LOG}")
    srv = ThreadingHTTPServer(("127.0.0.1", 8899), Handler)
    srv.daemon_threads = True
    srv.serve_forever()
