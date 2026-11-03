#!/bin/bash
# Raspberry Pi OS（Trixie 以降、cloud-init で最初の設定を行う版）を書き込んだ microSD カードの bootfs に、
# LLMConHub 用の最初の設定を書く。mac の上で、root ではなく普通の利用者として動かす。
#
#   tools/make_cloudinit_mac.sh [bootfs の場所（既定 /Volumes/bootfs）] [SSH の公開鍵のファイル（既定 ~/.ssh/id_ed25519.pub）]
#
# 設定する内容：
#   1. ホスト名 llmconhub（同じ LAN の中から llmconhub.local で届く）
#   2. 利用者（既定は mac の利用者と同じ名前。PIUSER=名前 で変えられる。パスワードでのログインは閉じ、SSH の公開鍵でだけ入れる。sudo はパスワードなし）
#   3. 時間帯 Asia/Tokyo、キーボード jp、無線の国コード JP
#   4. 有線 LAN（eth0）は DHCP で番地をもらう
# Raspberry Pi OS は NetworkManager でネットワークを管理するので、network-config に renderer: NetworkManager が要る。
# この行が無いと、netplan は systemd-networkd 向けに設定を作り、ネットワークにつながらない（2026-10-31 に実機で起きた）。
# renderer は network: の直下に書く。ethernets: や wifis: の下に書くと、cloud-init 25.2 はそれを機器の名前と取り違えて警告を出す。
# 無線 LAN の SSID とパスワードは、この台本では書かない。必要なら利用者が network-config の wifis の節に書く。
# 公開鍵は秘密の情報ではないが、リポジトリには置かず、書き込むときに ~/.ssh から読む。
# 一度起動したあとで無線 LAN の SSID やパスワードを直しても、この台本を流し直すだけでは足りない。
# Raspberry Pi OS は、最初の起動で作った接続の設定を /etc/netplan/90-NM-<UUID>.yaml に移し、こちらを優先する。
# そのため、Pi に SSH で入れるなら、そのファイルを消して `sudo cloud-init clean` を実行し、再起動する（2026-11-02 に実機で起きた）。
set -euo pipefail

BOOT=${1:-/Volumes/bootfs}
PIUSER=${PIUSER:-$(id -un)}
PUB=${2:-$HOME/.ssh/id_ed25519.pub}

[ -f "$BOOT/cmdline.txt" ] && [ -f "$BOOT/user-data" ] || { echo "$BOOT は cloud-init の版の bootfs ではありません。" >&2; exit 1; }
[ -f "$PUB" ] || { echo "公開鍵がありません：$PUB" >&2; exit 1; }
KEY=$(head -n1 "$PUB")

cat > "$BOOT/user-data" <<EOF
#cloud-config
# LLMConHub の最初の設定（tools/make_cloudinit_mac.sh が書いた）
hostname: llmconhub
manage_etc_hosts: true
timezone: Asia/Tokyo
keyboard:
  model: pc105
  layout: jp
users:
- name: $PIUSER
  groups: users,adm,dialout,audio,netdev,video,plugdev,cdrom,games,input,gpio,spi,i2c,render,sudo
  shell: /bin/bash
  lock_passwd: true
  sudo: ALL=(ALL) NOPASSWD:ALL
  ssh_authorized_keys:
  - $KEY
ssh_pwauth: false
enable_ssh: true
# 無線 LAN が rfkill で止められていても動くように、起動のたびに止めを外す
bootcmd:
- [sh, -c, 'rfkill unblock wifi || true; for f in /var/lib/systemd/rfkill/*:wlan; do [ -e "\$f" ] && echo 0 > "\$f"; done; true']
# 起動して 90 秒たったら、そのあと 60 秒ごとに 10 回、ネットワークの状態を bootfs の llmconhub-diag.txt に書き出す
# （ネットワークにつながらないときに mac で読むため。Raspberry Pi OS の journal は電源を切ると消えるので、ここに残す。パスワードは書き出さない）
write_files:
- path: /usr/local/sbin/llmconhub-diag.sh
  permissions: '0755'
  content: |
    #!/bin/sh
    out=/boot/firmware/llmconhub-diag.txt
    sleep 90
    for i in 1 2 3 4 5 6 7 8 9 10; do
      {
        echo "== round \$i"; date; uptime
        echo "== hostname"; hostname
        echo "== ip"; ip -br addr
        echo "== rfkill"; rfkill list
        echo "== iw reg"; iw reg get 2>&1 | head -5
        echo "== nmcli general"; nmcli general 2>&1
        echo "== nmcli dev"; nmcli -t dev 2>&1
        echo "== nmcli con"; nmcli -t -f NAME,UUID,TYPE,DEVICE,AUTOCONNECT con show 2>&1
        echo "== wifi scan"; nmcli -t -f SSID,BSSID,CHAN,FREQ,SIGNAL,SECURITY dev wifi list --rescan yes 2>&1 | head -40
        echo "== netplan files"; ls -l /etc/netplan /run/NetworkManager/system-connections 2>&1
        echo "== cloud-init"; cloud-init status --long 2>&1
        echo "== NetworkManager log"; journalctl -b -u NetworkManager --no-pager 2>&1 | tail -n 120
        echo "== wpa_supplicant log"; journalctl -b -u wpa_supplicant --no-pager 2>&1 | tail -n 60
        echo "== kernel brcmfmac"; dmesg 2>&1 | grep -iE 'brcm|wlan|cfg80211|regdom' | tail -n 40
        echo "== ssh"; systemctl is-active ssh
      } > "\$out.tmp" 2>&1
      mv "\$out.tmp" "\$out"
      sync
      sleep 60
    done
- path: /etc/systemd/system/llmconhub-diag.service
  content: |
    [Unit]
    Description=LLMConHub network diagnostics to bootfs
    After=network.target
    [Service]
    Type=simple
    ExecStart=/usr/local/sbin/llmconhub-diag.sh
    [Install]
    WantedBy=multi-user.target
runcmd:
- [sh, -c, 'nmcli radio wifi on || true']
- [systemctl, enable, --now, ssh]
- [systemctl, daemon-reload]
- [systemctl, enable, --now, --no-block, llmconhub-diag.service]
EOF

if grep -qE '^  wifis:' "$BOOT/network-config" 2>/dev/null; then
  # 利用者が無線 LAN の節（SSID とパスワード）を書いたあとなので、上書きしない。renderer の位置だけを直す。
  sed -i '' -e '/^    renderer: NetworkManager$/d' "$BOOT/network-config"
  if ! grep -qE '^  renderer: NetworkManager$' "$BOOT/network-config"; then
    sed -i '' -e '/^  version: 2$/a\
  renderer: NetworkManager
' "$BOOT/network-config"
  fi
  echo "network-config の無線 LAN の節は残し、renderer の位置だけを直しました。"
else
cat > "$BOOT/network-config" <<'EOF'
# LLMConHub の最初のネットワークの設定（tools/make_cloudinit_mac.sh が書いた）。
# cloud-init は、meta-data の instance-id が変わった起動のときにだけ、この設定を読み直す。
network:
  version: 2
  renderer: NetworkManager
  ethernets:
    eth0:
      dhcp4: true
      optional: true
# 無線 LAN を使うときは、次の行の先頭の # を外し、SSID とパスワードを書く（利用者が書く）。
#  wifis:
#    wlan0:
#      dhcp4: true
#      optional: true
#      regulatory-domain: JP
#      access-points:
#        "ここにSSID":
#          password: "ここにパスワード"
EOF
fi

# cloud-init の NoCloud は、meta-data の instance-id（ハイフン）だけを読む。instance_id（下線）と書くと読まれず、
# 機械の識別子は既定の nocloud のままになり、2回目からの起動ではネットワークの設定も runcmd も実行されない（2026-10-31 に実機で起きた）。
# この台本を流すたびに新しい識別子を書くので、カードを Pi に挿して起動すると、設定がすべて実行し直される。
cat > "$BOOT/meta-data" <<EOF
# LLMConHub の meta-data（tools/make_cloudinit_mac.sh が書いた）
dsmode: local
instance-id: llmconhub-$(date +%Y%m%d-%H%M%S)
EOF

if ! grep -q 'cfg80211.ieee80211_regdom=' "$BOOT/cmdline.txt"; then
  printf '%s cfg80211.ieee80211_regdom=JP\n' "$(tr -d '\n' < "$BOOT/cmdline.txt")" > "$BOOT/cmdline.txt.new"
  mv "$BOOT/cmdline.txt.new" "$BOOT/cmdline.txt"
fi

echo "書きました：$BOOT/user-data、$BOOT/meta-data、$BOOT/network-config、$BOOT/cmdline.txt"
echo "meta-data：$(grep instance-id "$BOOT/meta-data")"
echo "cmdline.txt：$(cat "$BOOT/cmdline.txt")"
