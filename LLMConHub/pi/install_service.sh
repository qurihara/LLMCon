#!/bin/bash
# LLMCon Hub を、Raspberry Pi の起動時に自動で立ち上がるようにする（systemd のサービス llmconhub）。
#   sudo ~/llmconhub/pi/install_service.sh          入れて、有効にして、いま起動する
#   sudo ~/llmconhub/pi/install_service.sh --no-start   入れて、有効にするだけ（次の起動から動く。install.sh が使う）
#   sudo ~/llmconhub/pi/install_service.sh remove   止めて、外す
# 動きの記録は journalctl -u llmconhub -f で読める。
set -euo pipefail
DIR=$(cd "$(dirname "$0")/.." && pwd)
UNIT=/etc/systemd/system/llmconhub.service

if [ "${1:-}" = "remove" ]; then
  systemctl disable --now llmconhub.service 2>/dev/null || true
  rm -f "$UNIT"
  systemctl daemon-reload
  echo "llmconhub のサービスを外しました。"
  exit 0
fi

cat > "$UNIT" <<UNIT
[Unit]
Description=LLMCon Hub（USB か Bluetooth の Pro コントローラとして LLMCon を動かす）
After=network-online.target bluetooth.service tailscaled.service
Wants=network-online.target bluetooth.service

[Service]
Type=simple
WorkingDirectory=$DIR
ExecStart=$DIR/pi/autostart.sh
Restart=on-failure
RestartSec=5
TimeoutStopSec=10
KillMode=control-group

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable llmconhub.service
if [ "${1:-}" = "--no-start" ]; then
  echo "llmconhub のサービスを入れました。次の起動から自動で立ち上がります。"
  exit 0
fi
systemctl restart llmconhub.service
echo "llmconhub のサービスを入れて、起動しました。記録は journalctl -u llmconhub -f で読めます。"
