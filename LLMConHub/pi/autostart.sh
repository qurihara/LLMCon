#!/bin/bash
# Raspberry Pi の起動時に systemd（llmconhub.service）から呼ばれ、LLMCon Hub を立ち上げる。
#
# 相手（Switch や PC）と USB でつながっていれば、USB の有線の Pro コントローラとして動く。
# つながっていなければ（AC アダプタだけで給電しているときなど）、Bluetooth の Pro コントローラとして動く。
# 見分け方：USB gadget を作ってから最大 USB_WAIT 秒待ち、口の状態が configured になれば USB とみなす。
#
# 設定は /etc/llmconhub.conf に、シェルの変数の形で書く（無ければ既定で動く）。
#   MODE=auto        auto（自動で選ぶ）、usb、bt のどれか
#   RECONNECT=       Bluetooth で前につないだ相手へ、Pi の側からつなぎ直す。アドレスか auto（nxbt が覚えている Switch）。空なら新しいペアリングを待つ
#   PORT=8777        MCP サーバの番号
#   BIND=            MCP サーバを開く番地。空なら Tailscale の番地（無ければ 127.0.0.1）
#   USB_WAIT=15      USB でつながるのを待つ秒数
#   VENV=/opt/llmconhub/venv   nxbt を入れた Python の環境
#
# 物理のパッドは USB-A の口につなぐ。起動のあとで挿しても拾う。割り当ては隣の profile.json に機体ごとに書く。
set -u
cd "$(dirname "$0")/.."

CONF=/etc/llmconhub.conf
[ -f "$CONF" ] && . "$CONF"
MODE=${MODE:-auto}
RECONNECT=${RECONNECT:-}
PORT=${PORT:-8777}
USB_WAIT=${USB_WAIT:-15}
VENV=${VENV:-/opt/llmconhub/venv}

if [ -z "${BIND:-}" ]; then
  # Tailscale は起動の直後には番地を持っていないことがあるので、少し待つ
  for _ in $(seq 1 30); do
    BIND=$(tailscale ip -4 2>/dev/null | head -n1 || true)
    [ -n "$BIND" ] && break
    sleep 1
  done
  [ -n "$BIND" ] || BIND=127.0.0.1
fi

HOST=(./llmconhub-host --bind "$BIND" --port "$PORT" --pad evdev --status)
usb_attached() { [ "$(cat /sys/class/udc/*/state 2>/dev/null | head -n1)" = "configured" ]; }

if [ "$MODE" = "auto" ] || [ "$MODE" = "usb" ]; then
  pi/procon_gadget.sh up
  for _ in $(seq 1 "$USB_WAIT"); do usb_attached && break; sleep 1; done
  if usb_attached || [ "$MODE" = "usb" ]; then
    echo "LLMCon Hub：USB でつながっています。USB の Pro コントローラとして動きます（MCP は http://$BIND:$PORT/ ）。"
    exec "${HOST[@]}" --sink procon-usb
  fi
fi
# Bluetooth で動くときは、前に作った USB の Pro コントローラを必ず外す。残すと、相手に入力の来ない
# USB の Pro コントローラが見えてしまう（2026-11-03、MODE=bt で起動し直したときに残っていた）。
pi/procon_gadget.sh down >/dev/null 2>&1 || true

echo "LLMCon Hub：Bluetooth の Pro コントローラとして動きます（MCP は http://$BIND:$PORT/ 、つなぎ直す相手：${RECONNECT:-なし}）。"
"$VENV/bin/python" bridge/llmconhub_bridge.py --backend nxbt ${RECONNECT:+--reconnect "$RECONNECT"} --log /var/tmp/llmconhub-latency.csv &
BRIDGE=$!
trap 'kill "$BRIDGE" 2>/dev/null || true' EXIT
"${HOST[@]}" --sink udp=127.0.0.1:9100
