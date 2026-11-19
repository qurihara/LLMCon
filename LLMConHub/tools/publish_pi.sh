#!/bin/bash
# mac の上で、LLMConHub のホストを Raspberry Pi 向け（linux-arm64、.NET の実行環境を同梱）に組み立て、
# 引数を付けたときは、Raspberry Pi の ~/llmconhub へ写す。
#
#   tools/publish_pi.sh                     組み立てるだけ（publish/linux-arm64 にできる）
#   tools/publish_pi.sh 利用者名@llmconhub   組み立てて、Raspberry Pi へ rsync で写す
#
# .NET の実行環境を同梱するので、Raspberry Pi に .NET を入れる必要はない（大きさは約 105MB）。
# 写す先には鍵を置かない。SSH の鍵は mac の側の ~/.ssh にあるものを使う。
set -euo pipefail
cd "$(dirname "$0")/.."

OUT=publish/linux-arm64
dotnet publish src/LLMConHub.Host/LLMConHub.Host.csproj -c Release -r linux-arm64 --self-contained true -o "$OUT"
chmod +x pi/*.sh
echo "組み立てました：$(pwd)/$OUT"

if [ -n "${1:-}" ]; then
  DEST="$1"
  ssh "$DEST" 'mkdir -p ~/llmconhub'
  rsync -av "$OUT/" "$DEST:llmconhub/"
  rsync -av bridge pi tools profile.json gamecontrollerdb.txt third_party "$DEST:llmconhub/"
  echo "写しました：$DEST:~/llmconhub"
fi
