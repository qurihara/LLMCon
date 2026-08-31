#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""既存のブラウザゲームに、LLMCon の出力を Gamepad API として見せる。

なぜ要るか。既存のブラウザゲームは Gamepad API（navigator.getGamepads）でコントローラを読む。
一方 mac の LLMCon が持つ出力は WebSocket であって、ゲーム側がそれに対応していないと届かない。
自作のゲームなら受け口を足せるが、それでは「既存のゲームに一切手を加えない」という研究の主張が
成り立たない。

そこで、ページが読み込まれる前に navigator.getGamepads を差し替える小さなスクリプトを注入し、
LLMCon の WebSocket から受け取った状態を Gamepad の形にして返す。**ゲームのコードは1行も
変えない。** Windows で ViGEm がオペレーティングシステムの層に仮想コントローラを見せるのと
同じことを、ブラウザの層で行う。実装の3層でいえば、出力シンクの一種にあたる。

使い方（画面を持たない Chrome を先に起動しておくこと）:

    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \\
      --headless=new --remote-debugging-port=9333 --user-data-dir=/tmp/chrome-llmcon \\
      --no-first-run --disable-gpu about:blank &

    from browser_gamepad import Browser
    b = Browser()                                  # 既定は 127.0.0.1:9333
    page = b.open("http://127.0.0.1:8080/game.html", llmcon="127.0.0.1:8777")
    page.eval("document.title")
    page.screenshot("shot.png")
"""
import base64
import json
import os
import time
import urllib.parse
import urllib.request

import websocket   # websocket-client

# Gamepad API の標準の並び。XInput のボタン名との対応である。
BUTTON_ORDER = ['A', 'B', 'X', 'Y', 'LB', 'RB', 'LT', 'RT',
                'Back', 'Start', 'LS', 'RS',
                'DUp', 'DDown', 'DLeft', 'DRight', 'Guide']

# ページが読み込まれる前に走らせるスクリプト。
# ly と ry の符号を反転しているのは、LLMCon が上を正とするのに対し、
# Gamepad API は下を正とするためである（games/shooter.html にも同じ注記がある）。
BRIDGE_JS = r"""
(() => {
  const WS_URL = "ws://__HOST__/vcon/ws";
  const ORDER = __ORDER__;
  let st = null, live = false, ws = null;

  function makePad() {
    const set = new Set((st && st.buttons) || []);
    const buttons = ORDER.map((name) => {
      let v = set.has(name) ? 1 : 0;
      if (name === "LT") v = (st && st.lt) || 0;
      if (name === "RT") v = (st && st.rt) || 0;
      return { pressed: v > 0.12, touched: v > 0.12, value: v };
    });
    return {
      id: "LLMCon Virtual Controller (STANDARD GAMEPAD Vendor: 045e Product: 028e)",
      index: 0,
      connected: true,
      mapping: "standard",
      timestamp: performance.now(),
      // ly と ry は符号を反転する（LLMCon は上が正、Gamepad API は下が正）
      axes: [ (st && st.lx) || 0, -((st && st.ly) || 0),
              (st && st.rx) || 0, -((st && st.ry) || 0) ],
      buttons: buttons,
      vibrationActuator: null,
    };
  }

  const empty = [null, null, null, null];
  navigator.getGamepads = function () { return live ? [makePad(), null, null, null] : empty; };
  if (navigator.webkitGetGamepads) navigator.webkitGetGamepads = navigator.getGamepads;

  function fire(type) {
    // GamepadEvent を作れない場合に備えて、素の Event に gamepad を付けたものを使う
    let ev;
    try { ev = new GamepadEvent(type, { gamepad: makePad() }); }
    catch (e) { ev = new Event(type); try { ev.gamepad = makePad(); } catch (_) {} }
    window.dispatchEvent(ev);
  }

  function connect() {
    try { ws = new WebSocket(WS_URL); } catch (e) { setTimeout(connect, 500); return; }
    ws.onmessage = (e) => {
      let m; try { m = JSON.parse(e.data); } catch (_) { return; }
      if (m && m.t === "pad") {
        st = m;
        if (!live) { live = true; fire("gamepadconnected"); }
      }
    };
    ws.onclose = () => { if (live) { live = false; fire("gamepaddisconnected"); } setTimeout(connect, 500); };
    ws.onerror = () => { try { ws.close(); } catch (_) {} };
  }
  connect();

  // 覗き見用。橋渡しが効いているかを外から確かめられるようにしておく
  window.__llmconBridge = {
    get live() { return live; },
    get state() { return st; },
    get pad() { return live ? makePad() : null; },
  };
})();
"""


class Page:
    """画面を持たない Chrome の1つのタブ。"""

    def __init__(self, ws_url, target_id):
        self.ws = websocket.create_connection(ws_url, suppress_origin=True, timeout=30)
        self.target_id = target_id
        self._id = 0

    def call(self, method, params=None):
        self._id += 1
        self.ws.send(json.dumps({'id': self._id, 'method': method, 'params': params or {}}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get('id') == self._id:
                if 'error' in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg.get('result', {})

    def eval(self, expression, await_promise=False):
        r = self.call('Runtime.evaluate', {
            'expression': expression, 'returnByValue': True, 'awaitPromise': await_promise})
        res = r.get('result', {})
        if r.get('exceptionDetails'):
            return {'__error__': res.get('description', str(r['exceptionDetails']))[:300]}
        return res.get('value')

    def reload(self, ignore_cache=True):
        """書き換えたページを読み直す。キャッシュが効くので既定で無視する。"""
        self.call('Page.enable')
        self.call('Page.reload', {'ignoreCache': ignore_cache})

    def keep_awake(self):
        """このタブの描画ループを、前面でなくても回し続けさせる。

        画面を持たない Chrome では、前面でないタブの requestAnimationFrame が
        止まる。タブが1つのうちは表面化しないが、2つのゲームを同時に動かすと、
        あとから開いたほうだけが動き、先に開いたほうは描画が止まったまま
        「入力は届いているのに何も起きない」という紛らわしい状態になる。
        Chrome の起動引数（--disable-background-timer-throttling など）だけでは
        足りず、タブごとに次の2つを指示する必要がある。
        """
        for method, params in (('Emulation.setFocusEmulationEnabled', {'enabled': True}),
                               ('Page.setWebLifecycleState', {'state': 'active'})):
            try:
                self.call(method, params)
            except Exception:
                pass

    def wait_ready(self, timeout=10.0):
        """Gamepad の橋渡しがつながるまで待つ。"""
        t0 = time.time()
        while time.time() - t0 < timeout:
            if self.eval("!!(window.__llmconBridge && window.__llmconBridge.live)") is True:
                return True
            time.sleep(0.15)
        return False

    def screenshot(self, path, scale=2, width=None, height=None):
        if width and height:
            self.call('Emulation.setDeviceMetricsOverride',
                      {'width': width, 'height': height,
                       'deviceScaleFactor': scale, 'mobile': False})
        r = self.call('Page.captureScreenshot', {'format': 'png'})
        os.makedirs(os.path.dirname(os.path.abspath(path)) or '.', exist_ok=True)
        open(path, 'wb').write(base64.b64decode(r['data']))
        return path

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


class Browser:
    """画面を持たない Chrome を、DevTools Protocol 越しに操る。"""

    def __init__(self, host='127.0.0.1', port=9333):
        self.base = f'http://{host}:{port}'

    def _http(self, path, method='GET'):
        req = urllib.request.Request(self.base + path, method=method)
        return json.loads(urllib.request.urlopen(req).read())

    def version(self):
        return self._http('/json/version')

    def open(self, url, llmcon='127.0.0.1:8777', wait=True, keys=None):
        """新しいタブで url を開き、LLMCon の状態を届ける経路を注入する。

        llmcon は、状態をもらう LLMCon の「ホスト:ポート」である。
        keys にボタン名からキー名への対応を渡すと、Gamepad API の代わりに
        キーボードの事象として届ける。Gamepad API を読まないゲーム向けである。
        """
        tab = self._http('/json/new?' + urllib.parse.quote(url, safe=':/?=&.#'), method='PUT')
        page = Page(tab['webSocketDebuggerUrl'], tab['id'])
        if keys:
            js = keyboard_js(keys, llmcon)
        else:
            js = (BRIDGE_JS
                  .replace('__HOST__', llmcon)
                  .replace('__ORDER__', json.dumps(BUTTON_ORDER)))
        page.call('Page.enable')
        page.call('Page.addScriptToEvaluateOnNewDocument', {'source': js})
        page.keep_awake()
        # 注入は「次に読み込むページ」から効くので、いったん読み直す
        page.reload(ignore_cache=True)
        page.keep_awake()
        if wait and not keys:
            page.wait_ready()
        return page

    def close_all(self):
        for t in self._http('/json/list'):
            if t.get('type') == 'page':
                try:
                    self._http('/json/close/' + t['id'])
                except Exception:
                    pass


# ---------------------------------------------------------------------------
# キーボードとして届ける経路
#
# ブラウザ上の既存のゲームには、Gamepad API を読まずキーボードだけを受け付けるものが多い。
# 実際、著名なゲームの再実装を調べたところ、テトリスもスペースインベーダーも
# キーボードのみであった。そうしたゲームにも改変の効果を届けるために、
# 確定した状態をキーボードの事象に変換して送る経路を用意する。
#
# 変換は**ページの中の JavaScript で**行う。外（Python）から1つずつ送ると、
# DevTools Protocol の往復が入力のたびに生じて実用にならない。Gamepad API を
# 見せる経路と同じく、LLMCon の WebSocket をページ自身に読ませる。
#
# 3層の枠組みでは、これも出力シンクの一種である。ゲームのコードには手を加えない。

# ボタン名から、KeyboardEvent に渡す (key, code, keyCode) への対応
KEYDEF = {
    'ArrowLeft':  ('ArrowLeft', 'ArrowLeft', 37),
    'ArrowRight': ('ArrowRight', 'ArrowRight', 39),
    'ArrowUp':    ('ArrowUp', 'ArrowUp', 38),
    'ArrowDown':  ('ArrowDown', 'ArrowDown', 40),
    'Space':      (' ', 'Space', 32),
    'Enter':      ('Enter', 'Enter', 13),
    'Escape':     ('Escape', 'Escape', 27),
}
for _c in 'abcdefghijklmnopqrstuvwxyz':
    KEYDEF[_c] = (_c, 'Key' + _c.upper(), ord(_c.upper()))

KEYBOARD_JS = r"""
(() => {
  const WS_URL = "ws://__HOST__/vcon/ws";
  const MAP = __MAP__;          // ボタン名 -> [key, code, keyCode]
  const DEAD = __DEAD__;        // スティックを十字とみなすしきい値
  let held = new Set(), ws = null;

  function fire(type, def) {
    const e = new KeyboardEvent(type, {
      key: def[0], code: def[1], bubbles: true, cancelable: true });
    Object.defineProperty(e, "keyCode", { get: () => def[2] });
    Object.defineProperty(e, "which",   { get: () => def[2] });
    document.dispatchEvent(e);
    window.dispatchEvent(e);
    if (document.body) document.body.dispatchEvent(e);
  }

  function apply(st) {
    const cur = new Set(st.buttons || []);
    // スティックは十字と同じ扱いにする。既存のゲームの多くは矢印キーで動くので、
    // これで感度や不感帯といったアナログの改変も効くようになる。
    const lx = st.lx || 0, ly = st.ly || 0;
    if (lx < -DEAD) cur.add("DLeft");
    if (lx >  DEAD) cur.add("DRight");
    if (ly >  DEAD) cur.add("DUp");      // LLMCon は上が正
    if (ly < -DEAD) cur.add("DDown");

    for (const b of cur) if (!held.has(b) && MAP[b]) fire("keydown", MAP[b]);
    for (const b of held) if (!cur.has(b) && MAP[b]) fire("keyup", MAP[b]);
    held = cur;
  }

  function connect() {
    try { ws = new WebSocket(WS_URL); } catch (e) { setTimeout(connect, 500); return; }
    ws.onmessage = (e) => {
      let m; try { m = JSON.parse(e.data); } catch (_) { return; }
      if (m && m.t === "pad") apply(m);
    };
    ws.onclose = () => {
      for (const b of held) if (MAP[b]) fire("keyup", MAP[b]);
      held = new Set();
      setTimeout(connect, 500);
    };
    ws.onerror = () => { try { ws.close(); } catch (_) {} };
  }
  connect();

  window.__llmconKeys = {
    get held() { return Array.from(held); },
    get map() { return MAP; },
  };
})();
"""


def keyboard_js(mapping, llmcon='127.0.0.1:8777', deadzone=0.4):
    """ボタン名からキー名への対応を受け取り、注入するスクリプトを作る。

    mapping の例: {'DLeft': 'ArrowLeft', 'A': 'ArrowUp', 'Start': 'Space'}
    """
    m = {b: list(KEYDEF[k]) for b, k in mapping.items() if k in KEYDEF}
    return (KEYBOARD_JS.replace('__HOST__', llmcon)
                       .replace('__MAP__', json.dumps(m))
                       .replace('__DEAD__', str(deadzone)))
