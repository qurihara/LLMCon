#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""6.4節の事例。アステロイドとテトリスを、コントローラの層だけで接続する。

構成
  1P アステロイドの再実装（8081）  <- Gamepad API を見せる経路 <- LLMCon 8777
  2P テトリスの再実装  （8082）  <- キーボードの事象の経路   <- LLMCon 8778

  8777 に接続を1本張る。1P の人間入力で「下・右・発射」の並びが定めた間隔のうちに
  成立したら、8778 の人間入力に「回転のボタンを無効にする」改変を1秒だけ掛ける。

  どちらのゲームのコードにも一切手を加えない。

何を確かめるか
  (1) 並びが成立すると、2P の回転が1秒間効かなくなり、そののち自動で戻る
  (2) 単独のボタンの押下では発火せず、並びの成立でだけ発火する
  (3) 性質の異なる二つの出力の経路（Gamepad API とキーボード）が同時に動く

回転が止まったことは、テトリスの素のグローバル変数 current.dir が変わらないことで
確かめる。ゲームのスコアは見ない（スコアは面白さのために設計された合成値であって、
操作が効いたかどうかを表さない）。
"""
import json, sys, time

sys.path.insert(0, __file__.rsplit('/', 1)[0])
from browser_gamepad import Browser
from llmcon import Mcp, VCon

HOST = '127.0.0.1'
P1, P2 = 8777, 8778               # 1P と 2P の LLMCon
ASTEROIDS = 'http://127.0.0.1:8081/index.html'
TETRIS = 'http://127.0.0.1:8082/index.html'

# 2P のテトリスは矢印キーだけを読む。回転は上キーである。
TETRIS_KEYS = {
    'DLeft': 'ArrowLeft', 'DRight': 'ArrowRight',
    'DUp': 'ArrowUp',                 # ← これが回転。接続で封じる対象
    'DDown': 'ArrowDown', 'Start': 'Space',
}

SEQUENCE = ['DDown', 'DRight', 'A']   # 1P が成立させる並び
WINDOW_MS = 600                       # 並びの入力と入力のあいだの上限
DISABLE_SEC = 1.0                     # 2P の回転を止める時間

# テトリスのページに入れる観測用の道具。
# 上キーにあたるボタンを自分で叩き、そのたびに current.dir が変わったかを数える。
# 送るのは LLMCon への人間入力なので、改変ルールがそのまま効く。
ASTEROIDS_JS = r"""
(() => {
  // 1P を普通に遊ばせる。自機を回し、ときどき噴かす。
  // 発射（A）は押さない。並びの最後の入力として外から送られるためである。
  const WS_URL = "ws://__HOST__/vcon/ws";
  let ws = null, ready = false, t = 0;
  function connect() {
    try { ws = new WebSocket(WS_URL); } catch (e) { setTimeout(connect, 300); return; }
    ws.onopen = () => { ready = true; };
    ws.onclose = () => { ready = false; setTimeout(connect, 300); };
  }
  connect();
  const send = (o) => { if (ready) try { ws.send(JSON.stringify(o)); } catch (e) {} };
  let running = false, thrusting = false;

  let firing = false;
  setInterval(() => {
    if (!running) return;
    t++;
    send({ t: "stick", s: "left", x: Math.sin(t / 9) * 0.9, y: 0 });   // 旋回
    const want = (t % 14) < 5;                                          // ときどき噴かす
    if (want !== thrusting) { thrusting = want; send({ t: "btn", b: "X", d: want }); }
    const fire = (t % 6) < 2;                                           // 小惑星を撃つ
    if (fire !== firing) { firing = fire; send({ t: "btn", b: "A", d: fire }); }
  }, 90);

  window.__astAgent = {
    get ready() { return ready; },
    start() { running = true; },
    stop() {
      running = false;
      send({ t: "stick", s: "left", x: 0, y: 0 });
      send({ t: "btn", b: "X", d: false });
    },
    get state() {
      const g = (window.GameHandler && GameHandler.game) ? GameHandler.game : null;
      const sc = g ? g.scenes[g.sceneIndex] : null;
      return {
        frames: window.GameHandler ? GameHandler.frameCount : null,
        sceneIndex: g ? g.sceneIndex : null,
        score: g ? g.score : null,
        lives: g ? g.lives : null,
        asteroids: (sc && sc.actors && sc.actors[0]) ? sc.actors[0].length : null,
      };
    },
    async begin() {                       // タイトル画面から始める
      const sleep = (ms) => new Promise(r => setTimeout(r, ms));
      for (let i = 0; i < 10; i++) {
        const g = GameHandler.game;
        if (g && g.sceneIndex === 1) return true;
        send({ t: "btn", b: "A", d: true }); await sleep(80);
        send({ t: "btn", b: "A", d: false }); await sleep(500);
      }
      return GameHandler.game ? GameHandler.game.sceneIndex : null;
    },
    // 並びを入力する。噴射をいったん離してから入れる。
    // 並びの途中に無関係なボタンの押下が挟まると成立しないためであり、
    // これは格闘ゲームのコマンド入力と同じ性質である。
    async command(seq, gapMs) {
      const sleep = (ms) => new Promise(r => setTimeout(r, ms));
      const wasRunning = running;
      running = false;
      if (thrusting) { thrusting = false; send({ t: "btn", b: "X", d: false }); }
      if (firing) { firing = false; send({ t: "btn", b: "A", d: false }); }
      await sleep(80);
      const t0 = performance.now();
      for (const b of seq) {
        send({ t: "btn", b: b, d: true });  await sleep(60);
        send({ t: "btn", b: b, d: false }); await sleep(gapMs);
      }
      running = wasRunning;
      return Math.round(performance.now() - t0);
    },
  };
})();
"""

PROBE_JS = r"""
(() => {
  const WS_URL = "ws://__HOST__/vcon/ws";
  let ws = null, ready = false;
  function connect() {
    try { ws = new WebSocket(WS_URL); } catch (e) { setTimeout(connect, 300); return; }
    ws.onopen = () => { ready = true; };
    ws.onclose = () => { ready = false; setTimeout(connect, 300); };
  }
  connect();
  const send = (o) => { if (ready) try { ws.send(JSON.stringify(o)); } catch (e) {} };
  const sleep = (ms) => new Promise(r => setTimeout(r, ms));

  window.__rotProbe = {
    get ready() { return ready; },
    get playing() { return (typeof playing !== "undefined") ? playing : null; },
    async start() {                    // スペースにあたるボタンで開始する
      for (let i = 0; i < 8; i++) {
        if (typeof playing !== "undefined" && playing === true) return true;
        send({ t: "btn", b: "Start", d: true }); await sleep(60);
        send({ t: "btn", b: "Start", d: false }); await sleep(300);
      }
      return (typeof playing !== "undefined") ? playing : null;
    },
    // 上キーにあたるボタンを n 回叩き、current.dir が変わった回数を返す
    async taps(n, gapMs) {
      let changed = 0, tried = 0;
      for (let i = 0; i < n; i++) {
        if (typeof current === "undefined" || !current) { await sleep(gapMs); continue; }
        const before = current.dir;
        send({ t: "btn", b: "DUp", d: true }); await sleep(40);
        send({ t: "btn", b: "DUp", d: false });
        await sleep(gapMs);
        tried++;
        if (current.dir !== before) changed++;
      }
      return { tried: tried, changed: changed };
    },
    // 並びの成立から復帰までを、一定の間隔で見張る
    async watch(totalMs, gapMs) {
      const out = [];
      const t0 = performance.now();
      while (performance.now() - t0 < totalMs) {
        const before = (typeof current !== "undefined" && current) ? current.dir : null;
        send({ t: "btn", b: "DUp", d: true }); await sleep(35);
        send({ t: "btn", b: "DUp", d: false }); await sleep(gapMs);
        const after = (typeof current !== "undefined" && current) ? current.dir : null;
        out.push({ ms: Math.round(performance.now() - t0),
                   rotated: (before !== null && after !== null && before !== after) });
      }
      return out;
    },
  };
})();
"""


def evalp(page, expr, await_promise=True):
    r = page.eval(expr, await_promise=await_promise)
    if isinstance(r, dict) and '__error__' in r:
        raise RuntimeError(r['__error__'])
    return r


def main():
    trials = int(sys.argv[1]) if len(sys.argv) > 1 else 10
    m1, m2 = Mcp(HOST, P1), Mcp(HOST, P2)

    print('== 準備 ==')
    # 前の実験のルールと接続を消しておく
    for m in (m1, m2):
        m.call('set_mapping', rules=[])
    import re
    for m in (m1, m2):
        for line in m.call('list_connections').splitlines():
            mm = re.match(r'\s*(c\d+):', line)
            if mm:
                m.call('remove_connection', id=mm.group(1))

    br = Browser()
    br.close_all()
    time.sleep(0.5)

    # 1P アステロイド。Gamepad API を見せる経路で 8777 につなぐ
    p1 = br.open(ASTEROIDS, llmcon=f'{HOST}:{P1}')
    # 2P テトリス。キーボードの事象の経路で 8778 につなぐ
    p2 = br.open(TETRIS, llmcon=f'{HOST}:{P2}', keys=TETRIS_KEYS, wait=False)
    time.sleep(2.0)

    # 2つのゲームを同時に動かすので、どちらのタブも前面あつかいにしておく
    p1.keep_awake(); p2.keep_awake()

    p1.eval(ASTEROIDS_JS.replace('__HOST__', f'{HOST}:{P1}'))
    p2.eval(PROBE_JS.replace('__HOST__', f'{HOST}:{P2}'))
    for _ in range(20):
        if (p1.eval('!!(window.__astAgent && window.__astAgent.ready)') is True
                and p2.eval('!!(window.__rotProbe && window.__rotProbe.ready)') is True):
            break
        time.sleep(0.2)

    print('  1P Gamepad の橋渡し:', p1.eval('!!(window.__llmconBridge && window.__llmconBridge.live)'))
    print('  2P キーボードの橋渡し:', p2.eval('!!window.__llmconKeys'))

    # 接続を1本張る
    cid = m1.call('add_connection',
                  event={'type': 'sequence', 'buttons': SEQUENCE, 'windowMs': WINDOW_MS},
                  target={'host': HOST, 'port': P2},
                  action={'kind': 'mapping', 'durationSec': DISABLE_SEC,
                          'rules': [{'op': 'disable', 'button': 'DUp'}]})
    print('  接続:', cid.strip())

    # 1P のアステロイドを始めて、普通に遊ばせる
    print('  1P の開始:', evalp(p1, 'window.__astAgent.begin()'))
    p1.eval('window.__astAgent.start()')
    # 2P のテトリスを始める
    print('  2P の開始:', evalp(p2, 'window.__rotProbe.start()'))
    time.sleep(2.0)
    print('  1P の様子:', json.dumps(p1.eval('window.__astAgent.state'), ensure_ascii=False))

    v1 = VCon(HOST, P1)

    # ------------------------------------------------------------------
    print('\n== 確認(2) 単独のボタンでは発火しないこと ==')
    def fires():
        """list_connections が表示する「fired Nx」を読み取る。"""
        s = m1.call('list_connections')
        mm = re.search(r'fired\s+(\d+)x', s)
        return int(mm.group(1)) if mm else None

    base = fires()
    for b in ('A', 'DDown', 'DRight'):
        for _ in range(5):
            evalp(p1, f'window.__astAgent.command(["{b}"], 200)')
    time.sleep(0.5)
    after_single = fires()
    print(f'  単独の押下15回のあと 発火回数 {base} -> {after_single}')

    # 順序が違えば成立しないことも確かめる
    for _ in range(5):
        evalp(p1, f'window.__astAgent.command({json.dumps(SEQUENCE[::-1])}, 90)')
    time.sleep(0.5)
    after_reverse = fires()
    print(f'  逆の順序5回のあと     発火回数 {after_single} -> {after_reverse}')

    # 並びの途中に無関係なボタンが挟まると成立しないことも確かめる
    for _ in range(5):
        evalp(p1, f'window.__astAgent.command(["DDown", "X", "DRight", "A"], 90)')
    time.sleep(0.5)
    after_noise = fires()
    print(f'  途中にXを挟む5回のあと 発火回数 {after_reverse} -> {after_noise}')
    after_single = after_noise

    # ------------------------------------------------------------------
    print(f'\n== 確認(1) 並びの成立で回転が止まり，自動で戻ること（{trials}試行） ==')
    rows = []
    for i in range(trials):
        # 掛ける前。回転が効いていることを確かめる
        b = evalp(p2, 'window.__rotProbe.taps(6, 130)')
        # 1P が並びを成立させる（噴射を離してから入れる）
        t_fire = time.time()
        evalp(p1, f'window.__astAgent.command({json.dumps(SEQUENCE)}, 90)')
        # 無効化の窓のあいだ
        d = evalp(p2, 'window.__rotProbe.taps(5, 130)')
        # 窓が閉じるのを待ってから
        while time.time() - t_fire < DISABLE_SEC + 0.6:
            time.sleep(0.05)
        a = evalp(p2, 'window.__rotProbe.taps(6, 130)')
        rows.append({'before': b, 'during': d, 'after': a})
        print(f'  {i+1:2d} 掛ける前 {b["changed"]}/{b["tried"]} 回転  '
              f'窓の中 {d["changed"]}/{d["tried"]}  窓のあと {a["changed"]}/{a["tried"]}')

    fired_total = fires()
    print(f'  発火回数 {after_single} -> {fired_total}')

    # ------------------------------------------------------------------
    print('\n== 接続の遅延（この事例での実測） ==')
    ev1 = m1.call('get_events', count=60)
    ev2 = m2.call('get_events', count=60)

    out = {
        'trials': rows,
        'fires': {'start': base, 'after_singles': after_single,
                  'after_reverse': after_reverse, 'after_noise': after_noise,
                  'end': fired_total},
        'events_1p': ev1, 'events_2p': ev2,
        'sequence': SEQUENCE, 'window_ms': WINDOW_MS, 'disable_sec': DISABLE_SEC,
    }

    st1 = p1.eval('window.__astAgent.state')
    st2 = evalp(p2, 'window.__rotProbe.taps(0, 10)', await_promise=True) and p2.eval(
        'JSON.parse(JSON.stringify({playing: typeof playing!=="undefined"?playing:null,'
        ' score: typeof score!=="undefined"?score:null, rows: typeof rows!=="undefined"?rows:null}))')
    out['state_1p'] = st1
    out['state_2p'] = st2
    print('  1P の様子:', json.dumps(st1, ensure_ascii=False))
    print('  2P の様子:', json.dumps(st2, ensure_ascii=False))

    # 図8のための撮影。自機が生きている瞬間に命令を決めて、その直後を撮る
    print('\n== 図8のための撮影 ==')
    for attempt in range(30):
        alive = p1.eval('(() => { const g = GameHandler.game; if (!g || g.sceneIndex !== 1) return false;'
                        ' const sc = g.scenes[1]; return !!(sc && sc.player && sc.player.alive'
                        ' && (!sc.interval || sc.interval.complete)); })()')
        if alive is True:
            evalp(p1, f'window.__astAgent.command({json.dumps(SEQUENCE)}, 90)')
            p1.screenshot('shot_1p_asteroids.png')
            p2.screenshot('shot_2p_tetris.png')
            print(f'  {attempt+1}回目の試みで撮れた（自機が生きている状態で命令が決まった直後）')
            break
        time.sleep(1.0)
    else:
        p1.screenshot('shot_1p_asteroids.png')
        p2.screenshot('shot_2p_tetris.png')
        print('  自機の生存を確かめられないまま撮影した')

    # 集計
    nb = sum(r['before']['changed'] for r in rows); tb = sum(r['before']['tried'] for r in rows)
    nd = sum(r['during']['changed'] for r in rows); td = sum(r['during']['tried'] for r in rows)
    na = sum(r['after']['changed'] for r in rows);  ta = sum(r['after']['tried'] for r in rows)
    print(f'\n== まとめ（{trials}試行） ==')
    print(f'  掛ける前  回転できた割合 {nb}/{tb} = {100*nb/max(tb,1):.1f}%')
    print(f'  窓の中    回転できた割合 {nd}/{td} = {100*nd/max(td,1):.1f}%')
    print(f'  窓のあと  回転できた割合 {na}/{ta} = {100*na/max(ta,1):.1f}%')

    out['summary'] = {
        'before': {'changed': nb, 'tried': tb},
        'during': {'changed': nd, 'tried': td},
        'after': {'changed': na, 'tried': ta},
    }
    json.dump(out, open('connect_two_games_result.json', 'w'), ensure_ascii=False, indent=1)
    v1.close()


if __name__ == '__main__':
    main()
