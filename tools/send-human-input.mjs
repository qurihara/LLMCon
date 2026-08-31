// ウェブ版コントローラのふりをして、人間入力と画面由来の改変ルールを送る道具。
// LLMCon の /vcon/ws につなぎ、{"t":"btn",...} と {"t":"uirules",...} を送る。
// 依存は無い（Node の標準機能だけ）。
//
// 改変ルールは人間入力の経路にだけ効く。MCP の hold や set_stick はルールの後に
// 合成されるので、反転などの確認には使えない。この道具はそのために要る。
//
// 使い方:
//   node tools/send-human-input.mjs <host> <port> <台本>
//
// 台本は、次の命令をセミコロンで区切って並べる。
//   rules:<JSON>      画面由来の改変ルールを差し替える。rules:[] で解除
//   rulesfile:<パス>  同じものをファイルから読む。引用符がコマの受け渡しで壊れないので、こちらが安全
//   down:<ボタン>     ボタンを押す
//   up:<ボタン>       ボタンを離す
//   wait:<ミリ秒>     待つ
//
// 例（反転を掛けて、A を押して離す）:
//   node tools/send-human-input.mjs 127.0.0.1 8788 \
//     'rules:[{"op":"invert","button":"A"}];wait:800;down:A;wait:800;up:A;wait:800;rules:[]'
import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';

const HOST = process.argv[2] || '127.0.0.1';
const PORT = parseInt(process.argv[3] || '8777', 10);
const SCRIPT = process.argv[4] || '';

// WebSocket のフレームを組み立てる（クライアントなので必ずマスクする）
function frame(text) {
  const payload = Buffer.from(text, 'utf8');
  const mask = crypto.randomBytes(4);
  const len = payload.length;
  let header;
  if (len < 126) {
    header = Buffer.alloc(2);
    header[1] = 0x80 | len;
  } else {
    header = Buffer.alloc(4);
    header[1] = 0x80 | 126;
    header.writeUInt16BE(len, 2);
  }
  header[0] = 0x81;                       // FIN + テキスト
  const masked = Buffer.alloc(len);
  for (let i = 0; i < len; i++) masked[i] = payload[i] ^ mask[i % 4];
  return Buffer.concat([header, mask, masked]);
}

const req = http.request({
  host: HOST, port: PORT, path: '/vcon/ws', method: 'GET',
  headers: {
    'Connection': 'Upgrade', 'Upgrade': 'websocket',
    'Sec-WebSocket-Key': crypto.randomBytes(16).toString('base64'),
    'Sec-WebSocket-Version': '13',
  },
});

req.on('upgrade', async (res, socket) => {
  console.log('つながりました。台本を流します。');
  const steps = SCRIPT.split(';').map(s => s.trim()).filter(Boolean);
  for (const step of steps) {
    const i = step.indexOf(':');
    const op = step.slice(0, i);
    const arg = step.slice(i + 1);
    if (op === 'wait') {
      await new Promise(r => setTimeout(r, parseInt(arg, 10)));
      continue;
    }
    let msg;
    if (op === 'rules') msg = JSON.stringify({ t: 'uirules', rules: JSON.parse(arg) });
    else if (op === 'rulesfile') msg = JSON.stringify({ t: 'uirules', rules: JSON.parse(fs.readFileSync(arg, 'utf8')) });
    else if (op === 'down') msg = JSON.stringify({ t: 'btn', b: arg, d: true });
    else if (op === 'up') msg = JSON.stringify({ t: 'btn', b: arg, d: false });
    else { console.log(`知らない命令です: ${step}`); continue; }
    socket.write(frame(msg));
    console.log(`送信: ${msg}`);
    await new Promise(r => setTimeout(r, 60));
  }
  await new Promise(r => setTimeout(r, 300));
  socket.end();
  console.log('おわり。');
  process.exit(0);
});

req.on('error', e => { console.log('つながりません: ' + e.message); process.exit(1); });
req.end();
