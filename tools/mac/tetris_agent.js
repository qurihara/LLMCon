// テトリスを自動で遊ぶ。**ページの中で完結させる。**
//
// なぜページ内でやるか。外（Python）から盤面を読んで操作を送ると、読み取りのたびに
// 通信の往復が生じる。1秒に十数回も往復させるとページの処理が詰まり、描画が止まって
// ゲームが進まなくなる。反応遅延を掛けた条件ほどループが長引くので、遅延の大きい条件だけ
// 測定が成立しない、という形で表面化した。
//
// このスクリプトは、盤面を読む・操作を決める・LLMCon へ人間入力として送る、の3つを
// ページ内で回す。LLMCon は改変ルールを適用して状態を出力し、それを別に注入してある
// キーボード変換のスクリプトが受け取ってキーの事象に変える。閉じた輪がページの中で完結する。
//
// ゲームのコードには一切手を加えていない。読むのは素のグローバル変数である。
(() => {
  const WS_URL = "ws://__HOST__/vcon/ws";
  const TARGETS = __TARGETS__;      // 目標の列を順に使う
  const TICK = __TICK__;            // 操作の間隔（ミリ秒）

  let ws = null, ready = false;
  const held = new Set();

  function send(obj) { if (ready) try { ws.send(JSON.stringify(obj)); } catch (e) {} }
  function press(b) { if (!held.has(b)) { held.add(b); send({ t: "btn", b: b, d: true }); } }
  function release(b) { if (held.has(b)) { held.delete(b); send({ t: "btn", b: b, d: false }); } }
  function tap(b) { press(b); setTimeout(() => release(b), 45); }

  const st = {
    running: false, ti: 0, lastY: null, lastX: null,
    placed: 0, hit: 0, started: false, t0: 0,
  };

  function step() {
    if (!st.running) return;
    if (typeof playing === "undefined" || playing !== true) {
      // まだ始まっていなければ、スペースにあたるボタンで開始を試みる
      if (!st.started) tap("Start");
      return;
    }
    st.started = true;
    if (typeof current === "undefined" || !current) return;

    const x = current.x, y = current.y;
    // 新しいブロックが出た（y が戻った）。直前のブロックがどこに置かれたかを数える
    if (st.lastY !== null && y < st.lastY) {
      if (st.lastX !== null) {
        st.placed++;
        if (st.lastX === TARGETS[st.ti]) st.hit++;
      }
      st.ti = (st.ti + 1) % TARGETS.length;
    }
    st.lastY = y; st.lastX = x;

    const goal = TARGETS[st.ti];
    if (x < goal) tap("DRight");
    else if (x > goal) tap("DLeft");
    else tap("DDown");
  }

  function connect() {
    try { ws = new WebSocket(WS_URL); } catch (e) { setTimeout(connect, 400); return; }
    ws.onopen = () => { ready = true; };
    ws.onclose = () => { ready = false; setTimeout(connect, 400); };
    ws.onerror = () => { try { ws.close(); } catch (e) {} };
  }
  connect();
  setInterval(step, TICK);

  window.__tetrisAgent = {
    start() {
      st.running = true; st.ti = 0; st.lastY = null; st.lastX = null;
      st.placed = 0; st.hit = 0; st.started = false; st.t0 = Date.now();
    },
    stop() {
      st.running = false;
      for (const b of Array.from(held)) release(b);
    },
    get result() {
      return {
        placed: st.placed, hit: st.hit,
        accuracy: st.placed ? (st.hit / st.placed * 100) : null,
        score: (typeof score !== "undefined") ? score : null,
        rows: (typeof rows !== "undefined") ? rows : null,
        playing: (typeof playing !== "undefined") ? playing : null,
        elapsed: (Date.now() - st.t0) / 1000,
        wsReady: ready,
      };
    },
  };
})();
