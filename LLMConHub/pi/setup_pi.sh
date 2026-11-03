#!/bin/bash
# Raspberry Pi（4 か Zero 2 W）を、LLMConHub の案6（Bluetooth と USB の両方で Switch につなぐ）で使えるようにする最初の設定。
# 1回だけ、Raspberry Pi の上で root で動かす。終わったら再起動する。
#
#   sudo ./setup_pi.sh
#
# この台本は、Raspberry Pi の設定を書き換える（/boot/firmware/config.txt と /etc/modules）。
# 書き換える前に、元のファイルを .bak-llmconhub の名前で残す。
# Pi 4 では、USB-C の口が USB の機器の専用になる（USB-A の4つの口は、これまでどおり物理のパッドなどに使える）。
# Zero 2 W では、「USB」と書かれた口が USB の機器の専用になり、その口に物理のパッドをつなげなくなる。
#
# 鍵や認証情報は、この台本では一切扱わない。Tailscale へのログインは、利用者が README の手順で行う。
# 2026-11-02 に、Raspberry Pi 4 Model B（Raspberry Pi OS Lite 64-bit、Trixie）の実機で動かした。
set -euo pipefail

if [ "$(id -u)" != 0 ]; then echo "sudo で動かしてください。" >&2; exit 1; fi
if [ "$(uname -m)" != aarch64 ]; then echo "64ビット版の Raspberry Pi OS（aarch64）で動かしてください。いまは $(uname -m) です。" >&2; exit 1; fi

CFG=/boot/firmware/config.txt
[ -f "$CFG" ] || CFG=/boot/config.txt
MODEL=$(tr -d '\0' < /proc/device-tree/model 2>/dev/null || echo "不明")
echo "機種：$MODEL"
echo "設定のファイル：$CFG"

# 1. USB の機器の口を有効にする
# Raspberry Pi OS の config.txt の [cm5] の節には、もとから dtoverlay=dwc2,dr_mode=host がある。
# これは CM5 の基板だけに効く行なので、dr_mode=peripheral の行があるかどうかで判断する（2026-11-02 に取り違えた）。
if grep -q '^dtoverlay=dwc2,dr_mode=peripheral' "$CFG"; then
  echo "config.txt には、すでに dtoverlay=dwc2,dr_mode=peripheral があります。"
else
  cp -n "$CFG" "$CFG.bak-llmconhub"
  printf '\n[all]\n# LLMConHub：USB の機器の口で Pro コントローラを名乗るため\ndtoverlay=dwc2,dr_mode=peripheral\n' >> "$CFG"
  echo "config.txt に dtoverlay=dwc2,dr_mode=peripheral を足しました（元のファイルは $CFG.bak-llmconhub）。"
fi
if ! grep -q '^libcomposite' /etc/modules; then
  cp -n /etc/modules /etc/modules.bak-llmconhub
  echo libcomposite >> /etc/modules
  echo "/etc/modules に libcomposite を足しました。"
fi

# 1b. Bluetooth の Device ID を、本物の Pro コントローラの番号（製造元 057E、製品 2009）にする。
# 既定のままだと BlueZ の番号（1D6B、0246）で名乗り、Windows は「Wireless Gamepad」として扱い、
# Steam は Pro コントローラと見分けない（2026-11-02 に ROG Ally で確かめた）。bluetoothd を起動し直すと効く。
BTCONF=/etc/bluetooth/main.conf
if [ -f "$BTCONF" ] && ! grep -q '^DeviceID = usb:057E:2009' "$BTCONF"; then
  cp -n "$BTCONF" "$BTCONF.bak-llmconhub"
  sed -i '/^\[General\]/a DeviceID = usb:057E:2009:0001' "$BTCONF"
  echo "$BTCONF に DeviceID = usb:057E:2009:0001 を足しました。"
fi
# 本物の Pro コントローラは BLE で名乗らない。BLE を有効にしたままだと、Windows は BLE の側の項目も作り、
# その項目が古い番号（1D6B、0246）をレジストリに残すので、従来の方式（BR/EDR）だけにする（2026-11-02 に ROG Ally で確かめた）。
if [ -f "$BTCONF" ] && ! grep -q '^ControllerMode = bredr' "$BTCONF"; then
  sed -i '/^DeviceID = usb:057E:2009/a ControllerMode = bredr' "$BTCONF"
  echo "$BTCONF に ControllerMode = bredr を足しました。"
fi

# 2. 必要な道具を入れる（xxd は記述子を書くため、bluez と dbus の開発用の部品は nxbt のため）
apt-get update
apt-get install -y xxd bluez python3-venv python3-dev python3-dbus libdbus-1-dev libglib2.0-dev git rsync

# 3. nxbt を専用の Python の環境に入れる（Bluetooth の出力のため。nxbt は root で動く）
# nxbt 0.1.4 は古い版の部品（dbus-python 1.2.16、Flask 1.1.2、cryptography 3.3.2 など）に版を固定していて、
# Raspberry Pi OS（Trixie、Python 3.13）では dbus-python の組み立てに失敗する（2026-11-02 に実機で起きた）。
# そこで、dbus-python は Debian の python3-dbus を使い（venv は system-site-packages で作る）、
# nxbt は --no-deps で入れ、ほかの部品は版を固定せずに入れる。
VENV=/opt/llmconhub/venv
if [ ! -x "$VENV/bin/python" ]; then
  mkdir -p /opt/llmconhub
  python3 -m venv --system-site-packages "$VENV"
fi
"$VENV/bin/pip" install --upgrade pip
"$VENV/bin/pip" install --no-deps nxbt
"$VENV/bin/pip" install blessed pynput psutil cryptography Flask Flask-SocketIO eventlet
"$VENV/bin/python" -c 'import dbus, nxbt; print("nxbt を読み込めました：", nxbt.__file__)'
# nxbt 0.1.4 の SIGTERM の受け口は引数を1つしか取らないので、止めるときに TypeError を出して後始末が残る。
# 引数を2つ取る形に直す（2026-11-02 に実機で起きた）。
NXPY=$("$VENV/bin/python" -c 'import nxbt, os; print(os.path.join(os.path.dirname(nxbt.__file__), "nxbt.py"))')
sed -i 's/lambda sigterm_handler: sys.exit(0)/lambda signum, frame: sys.exit(0)/' "$NXPY"
echo "nxbt を $VENV に入れました。"

# 4. ログインしている利用者を input の集団に入れる（物理のパッドの /dev/input/event* と /dev/hidg0 を読むため）
U=${SUDO_USER:-}
if [ -n "$U" ] && [ "$U" != root ]; then
  usermod -aG input "$U"
  echo "$U を input の集団に入れました。"
fi

echo
echo "最初の設定が終わりました。sudo reboot で再起動してください。"
echo "再起動のあと、sudo ./procon_gadget.sh up で USB の機器を作れるかを確かめます。"
