#!/bin/bash
# LLMCon Hub を Raspberry Pi OS（64-bit）にワンコマンドで入れる。
#
#   curl -fsSL https://raw.githubusercontent.com/qurihara/LLMCon/main/LLMConHub/install.sh | sudo bash
#
# することは4つである。
#   1. GitHub の Releases から、組み立て済みの LLMCon Hub（llmconhub-linux-arm64.tar.gz）を取ってきて、~/llmconhub に広げる。
#      .NET の実行環境を同梱しているので、Raspberry Pi に .NET を入れる必要はない。
#   2. pi/setup_pi.sh で、USB の機器の口、Bluetooth の名乗り（Pro コントローラの番号）、nxbt を用意する。
#   3. pi/install_service.sh で、起動時に LLMCon Hub が立ち上がるサービスを入れる。
#   4. 再起動を促す。USB の機器の口と Bluetooth の設定は、再起動のあとで効く。
#
# 版を決め打ちするときは LLMCONHUB_VERSION=llmconhub-v0.1.0 のように、手元の配布物を使うときは
# LLMCONHUB_TARBALL=/path/to/llmconhub-linux-arm64.tar.gz のように、環境変数を渡す（sudo -E bash で渡る）。
# 鍵や認証情報は一切扱わない。離れた機械の大規模言語モデルから使うときは、別に Tailscale を入れる（README を参照）。
set -euo pipefail

REPO=${LLMCONHUB_REPO:-qurihara/LLMCon}
ASSET=llmconhub-linux-arm64.tar.gz

if [ "$(id -u)" != 0 ]; then echo "sudo で動かしてください（curl ... | sudo bash）。" >&2; exit 1; fi
if [ "$(uname -m)" != aarch64 ]; then echo "64ビット版の Raspberry Pi OS（aarch64）で動かしてください。いまは $(uname -m) です。" >&2; exit 1; fi

# 入れる先の利用者（sudo を呼んだ人。分からなければ番号 1000 の人）
U=${SUDO_USER:-}
if [ -z "$U" ] || [ "$U" = root ]; then U=$(getent passwd 1000 | cut -d: -f1 || true); fi
if [ -z "$U" ]; then echo "入れる先の利用者が分かりません。sudo を使って、ふだんの利用者から動かしてください。" >&2; exit 1; fi
HOMEU=$(getent passwd "$U" | cut -d: -f6)
DEST="$HOMEU/llmconhub"
echo "LLMCon Hub を $DEST に入れます（利用者：$U）。"

command -v curl >/dev/null || { apt-get update; apt-get install -y curl; }
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

if [ -n "${LLMCONHUB_TARBALL:-}" ]; then
  cp "$LLMCONHUB_TARBALL" "$TMP/$ASSET"
else
  VER=${LLMCONHUB_VERSION:-}
  if [ -z "$VER" ]; then
    # Releases は新しい順に並ぶ。LLMCon Hub の版（llmconhub- で始まる）のうち、いちばん新しいものを選ぶ
    VER=$(curl -fsSL "https://api.github.com/repos/$REPO/releases" | grep -o '"tag_name": *"llmconhub-[^"]*"' | head -n1 | sed 's/.*"\(llmconhub-[^"]*\)"/\1/')
  fi
  if [ -z "$VER" ]; then echo "LLMCon Hub の配布物が $REPO の Releases に見つかりません。" >&2; exit 1; fi
  echo "版：$VER"
  curl -fL "https://github.com/$REPO/releases/download/$VER/$ASSET" -o "$TMP/$ASSET"
fi

mkdir -p "$DEST"
# すでにある profile.json（pad_setup で保存した割り当てや、手で書き足した項目）は残す。
# 新しい版の profile.json は profile.json.dist として置く。
if [ -f "$DEST/profile.json" ]; then cp "$DEST/profile.json" "$TMP/profile.json.keep"; fi
tar -xzf "$TMP/$ASSET" -C "$DEST"
if [ -f "$TMP/profile.json.keep" ]; then
  cp "$DEST/profile.json" "$DEST/profile.json.dist"
  cp "$TMP/profile.json.keep" "$DEST/profile.json"
  echo "すでにある profile.json を残しました（新しい版のものは profile.json.dist にあります）。"
fi
chown -R "$U:" "$DEST"
chmod +x "$DEST/llmconhub-host" "$DEST"/pi/*.sh

SUDO_USER="$U" bash "$DEST/pi/setup_pi.sh"
bash "$DEST/pi/install_service.sh" --no-start

echo
echo "LLMCon Hub を入れました。sudo reboot で再起動すると、自動で立ち上がります。"
echo "  USB でゲーム機や PC につないでいれば USB の Pro コントローラとして、そうでなければ Bluetooth の Pro コントローラとして動きます。"
echo "  Switch では「持ちかた/順番を変える」の画面を開き、PC では Bluetooth の機器の追加から「Pro Controller」を選びます。"
echo "  動きの記録：journalctl -u llmconhub -f    設定：/etc/llmconhub.conf（書ける項目は $DEST/pi/autostart.sh の先頭にある）"
