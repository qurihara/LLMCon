"""LLMCon を遠隔から操作するための小さな道具（標準ライブラリだけで動く）。

Mcp  : HTTP の MCP を叩く。initialize してセッションを保ち、tools/call を呼ぶ。
VCon : ウェブ版コントローラの WebSocket へつなぎ、人間入力（ボタンとスティック）を送る。
       改変ルールは人間入力にしか効かないので、ルールの検証にはこちらを使う。
"""
import base64, json, os, socket, struct, time, urllib.request


class Mcp:
    def __init__(self, host, port=8777, name="mac"):
        self.url = f"http://{host}:{port}/"
        self.sid = None
        self.rid = 0
        self._init(name)

    def _post(self, body):
        self.rid += 1
        body = dict(body)
        if "id" not in body and body.get("method") != "notifications/initialized":
            body["id"] = self.rid
        body["jsonrpc"] = "2.0"
        data = json.dumps(body).encode()
        req = urllib.request.Request(self.url, data=data, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json, text/event-stream")
        if self.sid:
            req.add_header("Mcp-Session-Id", self.sid)
        with urllib.request.urlopen(req, timeout=15) as r:
            if not self.sid:
                self.sid = r.headers.get("Mcp-Session-Id")
            raw = r.read().decode()
        for line in raw.splitlines():
            if line.startswith("data: "):
                return json.loads(line[6:])
        return None

    def _init(self, name):
        self._post({"method": "initialize", "params": {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": name, "version": "1.0"}}})
        self._post({"method": "notifications/initialized"})

    def call(self, tool, **args):
        """ツールを呼び、返ってきた文字列を返す。"""
        r = self._post({"method": "tools/call",
                        "params": {"name": tool, "arguments": args}})
        if r is None:
            return "(応答なし)"
        if "error" in r:
            return "エラー: " + json.dumps(r["error"], ensure_ascii=False)
        return r["result"]["content"][0]["text"]

    def state(self):
        return self.call("get_state")


class VCon:
    """ウェブ版コントローラと同じ経路で人間入力を送る WebSocket のクライアント。"""

    def __init__(self, host, port=8777):
        key = base64.b64encode(os.urandom(16)).decode()
        self.s = socket.create_connection((host, port), timeout=10)
        req = (f"GET /vcon/ws HTTP/1.1\r\nHost: {host}:{port}\r\n"
               f"Upgrade: websocket\r\nConnection: Upgrade\r\n"
               f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
        self.s.sendall(req.encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            buf += self.s.recv(4096)
        if b"101" not in buf.split(b"\r\n")[0]:
            raise RuntimeError("WebSocket の接続に失敗した: " + buf.split(b"\r\n")[0].decode())
        self.s.settimeout(0.05)

    def _send(self, obj):
        payload = json.dumps(obj).encode()
        n = len(payload)
        head = bytearray([0x81])          # FIN + テキストフレーム
        mask = os.urandom(4)
        if n < 126:
            head.append(0x80 | n)
        elif n < 65536:
            head.append(0x80 | 126)
            head += struct.pack(">H", n)
        else:
            head.append(0x80 | 127)
            head += struct.pack(">Q", n)
        head += mask
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        self.s.sendall(bytes(head) + masked)

    def recv(self, timeout=0.3):
        """サーバから届いたテキストのフレームを、あるだけ取り出して返す。
        WebSocketSink が配る {"t":"pad",...} も、ここに流れてくる。"""
        out = []
        end = time.time() + timeout
        self.s.settimeout(0.05)
        while time.time() < end:
            try:
                head = self._read_exact(2)
            except socket.timeout:
                continue          # まだ届いていないだけ。期限まで待つ
            except Exception:
                break
            op = head[0] & 0x0F
            n = head[1] & 0x7F
            masked = bool(head[1] & 0x80)
            if n == 126:
                n = struct.unpack(">H", self._read_exact(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", self._read_exact(8))[0]
            mask = self._read_exact(4) if masked else None
            body = self._read_exact(n) if n else b""
            if mask:
                body = bytes(b ^ mask[i % 4] for i, b in enumerate(body))
            if op == 1:
                out.append(body.decode("utf-8", "replace"))
            elif op == 8:      # 相手からの切断
                break
        return out

    def _read_exact(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.s.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("切断された")
            buf += chunk
        return buf

    def pads(self, timeout=0.3):
        """届いたもののうち、コントローラの状態（t=pad）だけを解釈して返す。"""
        out = []
        for m in self.recv(timeout):
            try:
                o = json.loads(m)
            except Exception:
                continue
            if isinstance(o, dict) and o.get("t") == "pad":
                out.append(o)
        return out

    def press(self, b):
        self._send({"t": "btn", "b": b, "d": True})

    def release(self, b):
        self._send({"t": "btn", "b": b, "d": False})

    def stick(self, side, x, y):
        self._send({"t": "stick", "s": side, "x": x, "y": y})

    def close(self):
        try:
            self.s.close()
        except Exception:
            pass
