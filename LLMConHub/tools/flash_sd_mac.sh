#!/bin/bash
# mac で、Raspberry Pi OS のイメージを microSD カードへ書き込む。root で動かす。
#
#   sudo tools/flash_sd_mac.sh <展開したイメージ.img> <ディスクの名前（例 disk4）>
#
# 書き込む先のディスクの中身は、すべて消える。取り違えを防ぐため、次の条件をすべて満たすディスクにだけ書き込む。
#   1. diskutil list external physical に載っている（外付けの物理ディスクである）
#   2. 取り外しのできる媒体である（Removable Media: Removable）
#   3. 容量が 64GB 以下である
# 書き込んだあと、boot の領域（bootfs）を mac に接続し直す。cloud-init の設定は tools/make_cloudinit_mac.sh で書く。
set -euo pipefail

IMG=${1:?イメージのファイルを指定してください}
DISK=${2:?ディスクの名前（例 disk4）を指定してください}

if [ "$(id -u)" != 0 ]; then echo "sudo で動かしてください。" >&2; exit 1; fi
[[ "$DISK" =~ ^disk[0-9]+$ ]] || { echo "ディスクの名前は disk4 のように書いてください：$DISK" >&2; exit 1; }
[ -f "$IMG" ] || { echo "イメージのファイルがありません：$IMG" >&2; exit 1; }
case "$IMG" in *.img) ;; *) echo "展開した .img のファイルを指定してください（.xz のままでは書き込まない）：$IMG" >&2; exit 1 ;; esac

diskutil list external physical | grep -q "^/dev/$DISK " || { echo "$DISK は外付けの物理ディスクではありません。中止します。" >&2; exit 1; }
INFO=$(diskutil info "$DISK")
echo "$INFO" | grep -qE 'Removable Media: +Removable' || { echo "$DISK は取り外しのできる媒体ではありません。中止します。" >&2; exit 1; }
BYTES=$(echo "$INFO" | sed -nE 's/.*Disk Size:.*\(([0-9]+) Bytes\).*/\1/p')
[ -n "$BYTES" ] && [ "$BYTES" -le 64000000000 ] || { echo "$DISK の容量が 64GB を超えているか、読み取れません（$BYTES バイト）。中止します。" >&2; exit 1; }

echo "書き込む先：/dev/$DISK（$BYTES バイト）"
diskutil list "$DISK"
echo "イメージ：$IMG（$(stat -f %z "$IMG") バイト）"

diskutil unmountDisk "/dev/$DISK"
dd if="$IMG" of="/dev/r$DISK" bs=4m status=progress
sync
echo "書き込みが終わりました。"
sleep 2
diskutil mount "/dev/${DISK}s1" || echo "bootfs の接続に失敗しました。カードを挿し直してください。" >&2
