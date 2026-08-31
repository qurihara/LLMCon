// WebSocket の出力シンクが、コントローラの状態を配っているかを確かめる道具。
// LLMCon の /vcon/ws につなぎ、届いたメッセージのうち t:"pad" のものを表示する。
// 依存は無い（Node の標準機能だけ）。games/ のゲームが受け取るのと同じものを、そのまま見られる。
//
// 使い方:
//   node tools/check-ws-sink.mjs [host] [port] [seconds]
//   例: node tools/check-ws-sink.mjs 127.0.0.1 8777 10
//       node tools/check-ws-sink.mjs 100.119.199.18 8777 10   （別の機械から）
//
// LLMCon は出力先に websocket を含めて起動しておくこと:
//   CntlLevelConnection.exe --sink vigem+websocket
import http from 'node:http';
import crypto from 'node:crypto';

const HOST = process.argv[2] || '127.0.0.1';
const PORT = parseInt(process.argv[3] || '8777', 10);
const SECONDS = parseInt(process.argv[4] || '10', 10);

const req = http.request({
  host: HOST, port: PORT, path: '/vcon/ws', method: 'GET',
  headers: {
    'Connection': 'Upgrade', 'Upgrade': 'websocket',
    'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64'),
    'Sec-WebSocket-Version': '13',
  },
});

let padCount = 0, otherCount = 0;
const samples = [];

req.on('upgrade', (res, socket) => {
  console.log(`connected to ws://${HOST}:${PORT}/vcon/ws  (listening ${SECONDS}s)`);
  let buf = Buffer.alloc(0);
  socket.on('data', chunk => {
    buf = Buffer.concat([buf, chunk]);
    // サーバからはマスクなしのテキストのフレームが来る。最低限の解析で十分である。
    while (buf.length >= 2) {
      const len0 = buf[1] & 0x7f;
      let off = 2, len = len0;
      if (len0 === 126) { if (buf.length < 4) break; len = buf.readUInt16BE(2); off = 4; }
      else if (len0 === 127) { if (buf.length < 10) break; len = Number(buf.readBigUInt64BE(2)); off = 10; }
      if (buf.length < off + len) break;
      const payload = buf.subarray(off, off + len).toString('utf8');
      buf = buf.subarray(off + len);
      try {
        const m = JSON.parse(payload);
        if (m && m.t === 'pad') { padCount++; if (samples.length < 10) samples.push(payload); }
        else otherCount++;
      } catch { otherCount++; }   // "reload" のような JSON でないものが来ることもある
    }
  });
  setTimeout(() => {
    console.log(`--- ${SECONDS}s の結果 ---`);
    console.log(`pad のメッセージ  : ${padCount}`);
    console.log(`それ以外          : ${otherCount}`);
    console.log('見本:');
    for (const s of samples) console.log('  ' + s);
    if (padCount === 0) {
      console.log('');
      console.log('pad のメッセージが1件も来ていない。LLMCon が --sink に websocket を含めて');
      console.log('起動しているかを確認すること（get_info の sink= で分かる）。');
    }
    socket.destroy();
    process.exit(padCount > 0 ? 0 : 1);
  }, SECONDS * 1000);
});
req.on('error', e => { console.log('接続できない: ' + e.message); process.exit(2); });
req.end();
