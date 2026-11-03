#!/bin/bash
# Raspberry Pi の上で、LLMConHub を案6の3つの形のどれかで起動する。
#
#   ./run.sh usb    USB の有線の Pro コントローラとして Switch につなぐ（Switch とは USB-C の口でつなぐ）
#   ./run.sh bt     Bluetooth の Pro コントローラとして Switch につなぐ（nxbt を使う）
#   ./run.sh both   USB と Bluetooth の両方へ同じ状態を出す（片方ずつ確かめたあとに使う）
#
# 物理のパッドは USB-A の口につなぐ（Zero 2 W の usb と both では、物理のパッドを USB でつなげない）。
# MCP サーバは、既定では Tailscale の番地にだけ開く。Tailscale が無ければ 127.0.0.1 に開く。
# 開く番地を変えるときは、BIND=0.0.0.0 ./run.sh usb のように書く。
# Bluetooth で前につないだ相手（Switch や PC）へ Pi の側からつなぎ直すときは、RECONNECT=AA:BB:CC:DD:EE:FF ./run.sh bt のように
# 相手の Bluetooth のアドレスを渡す。RECONNECT=auto なら、nxbt が覚えている Switch へつなぎ直す。
#
# このフォルダ（~/llmconhub）の中に、mac の tools/publish_pi.sh が写した llmconhub-host と bridge/ と pi/ がある前提である。
# 2026-11-02 に、bt の形を Raspberry Pi 4 と ROG Ally（Windows）で確かめた。usb と both は、まだ実機で確かめていない。
set -euo pipefail
cd "$(dirname "$0")/.."

MODE=${1:-}
PORT=${PORT:-8777}
BIND=${BIND:-$(tailscale ip -4 2>/dev/null | head -n1 || true)}
[ -n "$BIND" ] || BIND=127.0.0.1
PAD=${PAD:-evdev}
VENV=/opt/llmconhub/venv
HOST=(./llmconhub-host --bind "$BIND" --port "$PORT" --pad "$PAD" --status)

start_bridge() {
  echo "Switch の「持ちかた/順番を変える」の画面を開いてください。" >&2
  sudo "$VENV/bin/python" bridge/llmconhub_bridge.py --backend nxbt ${RECONNECT:+--reconnect "$RECONNECT"} --log "latency-$(date +%Y%m%d-%H%M%S).csv" &
  BRIDGE=$!
  trap 'sudo kill $BRIDGE 2>/dev/null || true' EXIT
}

case "$MODE" in
  usb)
    sudo pi/procon_gadget.sh up
    echo "Switch の設定の「コントローラーとセンサー」で、「Proコントローラーの有線通信」をオンにしておいてください。" >&2
    "${HOST[@]}" --sink procon-usb
    ;;
  bt)
    start_bridge
    "${HOST[@]}" --sink udp=127.0.0.1:9100
    ;;
  both)
    sudo pi/procon_gadget.sh up
    start_bridge
    "${HOST[@]}" --sink procon-usb+udp=127.0.0.1:9100
    ;;
  *)
    echo "使い方：$0 usb|bt|both" >&2
    exit 2
    ;;
esac
