#!/bin/bash
# Raspberry Pi の USB の機器の口（Pi 4 は USB-C の口、Zero 2 W は「USB」と書かれた口）を、
# Switch の有線の Pro コントローラとして名乗る USB の機器にする。作ると /dev/hidg0 ができ、
# llmconhub-host の --sink procon-usb がそこを読み書きする。
#
# 使い方（root で動かす）：
#   sudo ./procon_gadget.sh up     機器を作り、口に結びつける
#   sudo ./procon_gadget.sh down   口から外し、機器を消す
#   sudo ./procon_gadget.sh status いまの状態を表示する
#
# 前提：/boot/firmware/config.txt に dtoverlay=dwc2,dr_mode=peripheral があり、libcomposite が読み込まれていること
# （pi/setup_pi.sh が設定する）。
#
# 名乗る値（製造元の番号 057e、製品の番号 2009、HID の報告の記述子）は、本物の Pro コントローラの値である。
# 記述子は、nscon の作者が本物の Pro コントローラから読み出したもの
# （https://gist.github.com/mzyy94/60ae253a45e2759451789a117c59acf9 の add_procon_gadget.sh）と同じバイトの列である。
# 2026-10-31 の時点で、実機では確かめていない。
set -euo pipefail

G=/sys/kernel/config/usb_gadget/procon
DESC=050115000904A1018530050105091901290A150025017501950A5500650081020509190B290E150025017501950481027501950281030B01000100A1000B300001000B310001000B320001000B35000100150027FFFF0000751095048102C00B39000100150025073500463B0165147504950181020509190F2912150025017501950481027508953481030600FF852109017508953F8103858109027508953F8103850109037508953F9183851009047508953F9183858009057508953F9183858209067508953F9183C0

up() {
  modprobe libcomposite
  mountpoint -q /sys/kernel/config || mount -t configfs none /sys/kernel/config
  if [ -z "$(ls /sys/class/udc 2>/dev/null)" ]; then
    echo "USB の機器の口（/sys/class/udc）が見つかりません。config.txt の dtoverlay=dwc2,dr_mode=peripheral と、再起動を確かめてください。" >&2
    exit 1
  fi
  if [ -d "$G" ]; then
    echo "機器はすでにあります。作り直すときは、先に down を実行してください。"
    status
    return
  fi
  mkdir -p "$G"
  cd "$G"
  echo 0x057e > idVendor
  echo 0x2009 > idProduct
  echo 0x0200 > bcdDevice
  echo 0x0200 > bcdUSB
  echo 0x00 > bDeviceClass
  echo 0x00 > bDeviceSubClass
  echo 0x00 > bDeviceProtocol
  mkdir -p strings/0x409
  echo "000000000001" > strings/0x409/serialnumber
  echo "Nintendo Co., Ltd." > strings/0x409/manufacturer
  echo "Pro Controller" > strings/0x409/product
  mkdir -p configs/c.1/strings/0x409
  echo "Nintendo Switch Pro Controller" > configs/c.1/strings/0x409/configuration
  echo 500 > configs/c.1/MaxPower
  echo 0xa0 > configs/c.1/bmAttributes
  mkdir -p functions/hid.usb0
  echo 0 > functions/hid.usb0/protocol
  echo 0 > functions/hid.usb0/subclass
  echo 64 > functions/hid.usb0/report_length
  echo "$DESC" | xxd -r -ps > functions/hid.usb0/report_desc
  ln -s functions/hid.usb0 configs/c.1/
  ls /sys/class/udc | head -n1 > UDC
  sleep 0.5
  # llmconhub-host を root 以外で動かすときのために、/dev/hidg0 を input の集団から読み書きできるようにする
  if [ -e /dev/hidg0 ]; then chgrp input /dev/hidg0 && chmod 660 /dev/hidg0; fi
  status
}

down() {
  [ -d "$G" ] || { echo "機器はありません。"; return; }
  cd "$G"
  echo "" > UDC 2>/dev/null || true
  rm -f configs/c.1/hid.usb0
  rmdir configs/c.1/strings/0x409 configs/c.1 functions/hid.usb0 strings/0x409 2>/dev/null || true
  cd /
  rmdir "$G"
  echo "機器を消しました。"
}

status() {
  if [ -d "$G" ]; then
    echo "機器：あり（結びつけた口：$(cat "$G/UDC" 2>/dev/null || echo なし)）"
  else
    echo "機器：なし"
  fi
  ls -l /dev/hidg0 2>/dev/null || echo "/dev/hidg0：なし"
  for u in /sys/class/udc/*; do
    [ -e "$u" ] && echo "口 $(basename "$u") の状態：$(cat "$u/state" 2>/dev/null)"
  done
}

case "${1:-}" in
  up) up ;;
  down) down ;;
  status) status ;;
  *) echo "使い方：sudo $0 up|down|status" >&2; exit 2 ;;
esac
