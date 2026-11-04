#!/bin/bash
# GitHub の Releases に置く、Raspberry Pi 向けの配布物（llmconhub-linux-arm64.tar.gz）を作る。
# 中身は、組み立て済みのホスト（.NET の実行環境を同梱）、bridge、pi、tools、profile.json、README.md である。
# install.sh は、この配布物を ~/llmconhub に広げる。
#   tools/make_release_pi.sh [出力先のフォルダ]
set -euo pipefail
cd "$(dirname "$0")/.."
OUTDIR=${1:-publish}
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
dotnet publish src/LLMConHub.Host/LLMConHub.Host.csproj -c Release -r linux-arm64 --self-contained true -o "$STAGE"
cp -R bridge pi tools profile.json gamecontrollerdb.txt third_party README.md "$STAGE/"
find "$STAGE" -name '__pycache__' -prune -exec rm -rf {} +
find "$STAGE" -name '*.pdb' -delete
chmod +x "$STAGE"/pi/*.sh "$STAGE/llmconhub-host"
mkdir -p "$OUTDIR"
tar -czf "$OUTDIR/llmconhub-linux-arm64.tar.gz" -C "$STAGE" .
echo "作りました：$(cd "$OUTDIR" && pwd)/llmconhub-linux-arm64.tar.gz（$(du -h "$OUTDIR/llmconhub-linux-arm64.tar.gz" | cut -f1)）"
