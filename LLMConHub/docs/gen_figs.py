#!/usr/bin/env python3
"""LLMCon Hub の説明図（全体像 v2 と、1台で2人）を SVG で書き出す。

使い方：python3 gen_figs.py <書き出し先のフォルダ>
PNG にするときは、Chrome を画面を持たない形で起動し、--window-size=1600,1000 --screenshot で撮る。
"""
import sys, os

OUT = sys.argv[1]
FONT = "'Hiragino Sans','Noto Sans JP',sans-serif"

# 色
INK = "#2d3142"
BT = "#2f7be0"       # Bluetooth（2P）
USBC = "#ef7d22"     # USB-C（1P）
P1 = "#ef7d22"
P2 = "#2f7be0"
LLMC = "#7b61d6"
BOARD = "#2f9e5f"
BOARD_D = "#1d6e40"


def T(x, y, s, size=24, weight=400, fill=INK, anchor="middle"):
    return (f'<text x="{x}" y="{y}" font-size="{size}" font-weight="{weight}" '
            f'fill="{fill}" text-anchor="{anchor}" dominant-baseline="central">{s}</text>')


def head(w, h, bg):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" '
            f'font-family="{FONT}">\n'
            '<defs>'
            f'<marker id="arB" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M1 1L9 5L1 9z" fill="{BT}"/></marker>'
            f'<marker id="arO" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M1 1L9 5L1 9z" fill="{USBC}"/></marker>'
            f'<marker id="arP" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M1 1L9 5L1 9z" fill="{LLMC}"/></marker>'
            f'<marker id="arG" viewBox="0 0 10 10" refX="7" refY="5" markerWidth="5" markerHeight="5" orient="auto"><path d="M1 1L9 5L1 9z" fill="#555"/></marker>'
            '</defs>\n'
            f'<rect width="{w}" height="{h}" fill="{bg}"/>\n')


# ---------- 部品 ----------

def pro_pad(x, y, s=1.0, body="#55607a", grip="#55607a"):
    """一般的なパッド型（200x120 を基準）"""
    g = [f'<g transform="translate({x},{y}) scale({s})" stroke="{INK}" stroke-width="4" stroke-linejoin="round">']
    g.append(f'<path d="M30 22 Q30 6 52 6 L148 6 Q170 6 170 22 L196 92 Q202 120 176 118 Q158 116 142 88 L58 88 Q42 116 24 118 Q-2 120 4 92 Z" fill="{body}"/>')
    # 十字キー
    g.append('<path d="M44 38h12v-12h12v12h12v12h-12v12h-12v-12h-12z" fill="#2d3142" stroke="none"/>')
    # ボタン4つ
    for (bx, by, c) in [(150, 28, "#ffd23f"), (150, 58, "#4cc9f0"), (135, 43, "#f15bb5"), (165, 43, "#06d6a0")]:
        g.append(f'<circle cx="{bx}" cy="{by}" r="8" fill="{c}" stroke-width="3"/>')
    # スティック
    g.append('<circle cx="82" cy="70" r="13" fill="#2d3142" stroke-width="3"/><circle cx="118" cy="70" r="13" fill="#2d3142" stroke-width="3"/>')
    g.append('</g>')
    return "".join(g)


def arcade(x, y, s=1.0, body="#3a3f58", accent=P1):
    """アーケードコントローラ（260x150 を基準）：レバーと8つのボタン"""
    g = [f'<g transform="translate({x},{y}) scale({s})" stroke="{INK}" stroke-width="4" stroke-linejoin="round">']
    g.append(f'<rect x="0" y="40" width="260" height="96" rx="16" fill="{body}"/>')
    g.append(f'<rect x="0" y="118" width="260" height="22" rx="8" fill="#23263a"/>')
    # レバー
    g.append('<ellipse cx="56" cy="92" rx="22" ry="9" fill="#23263a" stroke-width="3"/>')
    g.append('<line x1="56" y1="90" x2="56" y2="36" stroke="#cfd3dc" stroke-width="8" stroke-linecap="round"/>')
    g.append(f'<circle cx="56" cy="28" r="20" fill="{accent}"/>')
    # ボタン8つ（2段×4）
    cols = ["#ff595e", "#ffca3a", "#8ac926", "#1982c4", "#ff595e", "#ffca3a", "#8ac926", "#1982c4"]
    i = 0
    for row, yy in enumerate([70, 104]):
        for k in range(4):
            bx = 118 + k * 36 + (0 if row == 0 else 0)
            by = yy - (k == 1 or k == 2) * 6
            g.append(f'<circle cx="{bx}" cy="{by}" r="13" fill="{cols[i]}" stroke-width="3"/>')
            i += 1
    g.append('</g>')
    return "".join(g)


def sfc_pad(x, y, s=1.0, body="#d9dbe6"):
    """スーパーファミコン型のパッド（280x120 を基準）"""
    g = [f'<g transform="translate({x},{y}) scale({s})" stroke="{INK}" stroke-width="4" stroke-linejoin="round">']
    g.append(f'<rect x="0" y="0" width="280" height="120" rx="60" fill="{body}"/>')
    g.append('<path d="M40 50h14v-14h14v14h14v14h-14v14h-14v-14h-14z" fill="#3a3f58" stroke="none"/>')
    g.append('<rect x="112" y="64" width="22" height="10" rx="5" fill="#8a8fa3" stroke-width="2" transform="rotate(-25 123 69)"/>')
    g.append('<rect x="146" y="64" width="22" height="10" rx="5" fill="#8a8fa3" stroke-width="2" transform="rotate(-25 157 69)"/>')
    g.append('<circle cx="222" cy="60" r="44" fill="#b9bccb" stroke-width="2"/>')
    for (bx, by, c) in [(222, 34, "#3a86ff"), (196, 60, "#2fbf71"), (248, 60, "#ef233c"), (222, 86, "#ffd60a")]:
        g.append(f'<circle cx="{bx}" cy="{by}" r="12" fill="{c}" stroke-width="3"/>')
    g.append('</g>')
    return "".join(g)


def phone(x, y, s=1.0):
    """スマートフォン（120x220）の画面にウェブ版コントローラ"""
    g = [f'<g transform="translate({x},{y}) scale({s})" stroke="{INK}" stroke-width="4" stroke-linejoin="round">']
    g.append('<rect x="0" y="0" width="120" height="220" rx="18" fill="#3a3f58"/>')
    g.append('<rect x="9" y="22" width="102" height="176" rx="6" fill="#f3f6ff" stroke-width="2"/>')
    g.append('<rect x="44" y="8" width="32" height="6" rx="3" fill="#23263a" stroke="none"/>')
    # 画面の中のコントローラ（縦向き）
    g.append('<rect x="16" y="30" width="88" height="22" rx="4" fill="#dfe6ff" stroke="none"/>')
    g.append('<path d="M26 112h10v-10h10v10h10v10h-10v10h-10v-10h-10z" fill="#3a3f58" stroke="none"/>')
    for (bx, by, c) in [(86, 98, "#ffd23f"), (86, 132, "#4cc9f0"), (70, 115, "#f15bb5"), (102, 115, "#06d6a0")]:
        g.append(f'<circle cx="{bx}" cy="{by}" r="8" fill="{c}" stroke-width="2"/>')
    g.append('<circle cx="40" cy="168" r="12" fill="#c9d2ea" stroke-width="2"/><circle cx="82" cy="168" r="12" fill="#c9d2ea" stroke-width="2"/>')
    g.append('</g>')
    return "".join(g)


def cloud(cx, cy, s=1.0, fill="#efeaff", stroke=LLMC):
    """雲（中心 cx,cy、幅およそ 260）"""
    d = ("M-125 45 A42 42 0 0 1 -112 -26 A58 58 0 0 1 -18 -62 A60 60 0 0 1 82 -36 "
         "A48 48 0 0 1 128 45 Z")
    return (f'<g transform="translate({cx},{cy}) scale({s})"><path d="{d}" fill="{fill}" stroke="{stroke}" '
            f'stroke-width="5" stroke-linejoin="round"/></g>')


def bubble(x, y, w, h, tail_x, tail_y, text_lines, fill="#ffffff", stroke=LLMC, size=22, color=INK, weight=700):
    """吹き出し。尾の先は (tail_x, tail_y)"""
    cx = x + w / 2
    # 尾の根元は、尾の先に近い辺の上に置き、辺より内側へ少し食い込ませる
    hw, ins = 20, 12
    if tail_y > y + h:
        bx = min(max(tail_x, x + 40), x + w - 40)
        tail = f'M{bx-hw} {y+h-ins} L{tail_x} {tail_y} L{bx+hw} {y+h-ins}'
    elif tail_y < y:
        bx = min(max(tail_x, x + 40), x + w - 40)
        tail = f'M{bx-hw} {y+ins} L{tail_x} {tail_y} L{bx+hw} {y+ins}'
    elif tail_x > x + w:
        by = min(max(tail_y, y + 30), y + h - 30)
        tail = f'M{x+w-ins} {by-hw} L{tail_x} {tail_y} L{x+w-ins} {by+hw}'
    else:
        by = min(max(tail_y, y + 30), y + h - 30)
        tail = f'M{x+ins} {by-hw} L{tail_x} {tail_y} L{x+ins} {by+hw}'
    g = [f'<path d="{tail}" fill="{fill}" stroke="{stroke}" stroke-width="4" stroke-linejoin="round"/>',
         f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="18" fill="{fill}" stroke="{stroke}" stroke-width="4"/>',
         f'<path d="{tail}" fill="{fill}" stroke="none"/>']
    n = len(text_lines)
    lh = size * 1.35
    y0 = y + h / 2 - lh * (n - 1) / 2
    for i, s in enumerate(text_lines):
        g.append(T(cx, y0 + i * lh, s, size, weight, color))
    return "".join(g)


def wifi(cx, cy, s=1.0, color=LLMC):
    """Wi-Fi の扇形の記号"""
    return (f'<g transform="translate({cx},{cy}) scale({s})" fill="none" stroke="{color}" stroke-width="5" stroke-linecap="round">'
            '<path d="M-24 -6 A34 34 0 0 1 24 -6"/><path d="M-14 4 A20 20 0 0 1 14 4"/>'
            f'<circle cx="0" cy="13" r="4" fill="{color}" stroke="none"/></g>')


def radio(cx, cy, s=1.0, color=BT, bg="#ffffff"):
    """電波のマーク（丸い札の中に、点と左右の弧）"""
    return (f'<g transform="translate({cx},{cy}) scale({s})">'
            f'<circle r="22" fill="{bg}" stroke="{color}" stroke-width="4"/>'
            f'<g fill="none" stroke="{color}" stroke-width="3.5" stroke-linecap="round">'
            '<path d="M-7 -8 A11 11 0 0 0 -7 8"/><path d="M7 -8 A11 11 0 0 1 7 8"/>'
            '<path d="M-13 -13 A19 19 0 0 0 -13 13"/><path d="M13 -13 A19 19 0 0 1 13 13"/></g>'
            f'<circle r="4" fill="{color}"/></g>')


def plug(x, y, ang=0, color="#8a8fa3"):
    """ケーブルの先の USB プラグ（左向きに挿す）"""
    return (f'<g transform="translate({x},{y}) rotate({ang})" stroke="{INK}" stroke-width="3">'
            f'<rect x="0" y="-11" width="30" height="22" rx="4" fill="{color}"/>'
            '<rect x="-14" y="-8" width="16" height="16" rx="2" fill="#dfe3ea"/></g>')


def handheld(x, y, s=1.0):
    """携帯ゲーム機型（380x180）。一般的な形"""
    g = [f'<g transform="translate({x},{y}) scale({s})" stroke="{INK}" stroke-width="4" stroke-linejoin="round">']
    g.append('<rect x="0" y="0" width="100" height="180" rx="44" fill="#9aa5bd"/>')
    g.append('<rect x="280" y="0" width="100" height="180" rx="44" fill="#9aa5bd"/>')
    g.append('<rect x="64" y="0" width="252" height="180" rx="10" fill="#33384d"/>')
    g.append('<rect x="78" y="14" width="224" height="152" rx="6" fill="#bfe6ff" stroke-width="2"/>')
    # 左の手元
    g.append('<circle cx="38" cy="52" r="15" fill="#33384d" stroke-width="3"/>')
    for (bx, by) in [(38, 110), (38, 140), (24, 125), (52, 125)]:
        g.append(f'<circle cx="{bx}" cy="{by}" r="7" fill="#33384d" stroke-width="2"/>')
    # 右の手元
    for (bx, by) in [(342, 38), (342, 72), (326, 55), (358, 55)]:
        g.append(f'<circle cx="{bx}" cy="{by}" r="8" fill="#33384d" stroke-width="2"/>')
    g.append('<circle cx="342" cy="128" r="15" fill="#33384d" stroke-width="3"/>')
    g.append('</g>')
    return "".join(g)


def screen_scene(x, y, w, h):
    """画面の中の簡単な景色（空と地面と人形）"""
    return (f'<rect x="{x}" y="{y}" width="{w}" height="{h*0.68}" fill="#bfe6ff"/>'
            f'<rect x="{x}" y="{y+h*0.68}" width="{w}" height="{h*0.32}" fill="#8fd18f"/>'
            f'<circle cx="{x+w*0.8}" cy="{y+h*0.2}" r="{h*0.09}" fill="#ffd166"/>')


def stick_fighter(x, y, color, face=1, s=1.0, kick=False):
    """丸い頭の人形。face=1 で右向き"""
    f = face
    g = [f'<g transform="translate({x},{y}) scale({s})" stroke="{color}" stroke-width="7" stroke-linecap="round" fill="none">']
    g.append(f'<circle cx="0" cy="-58" r="14" fill="{color}" stroke="none"/>')
    g.append('<line x1="0" y1="-44" x2="0" y2="-8"/>')
    # 腕：前へ突き出す
    g.append(f'<line x1="0" y1="-34" x2="{28*f}" y2="-38"/>')
    g.append(f'<line x1="0" y1="-34" x2="{-16*f}" y2="-20"/>')
    if kick:
        g.append(f'<line x1="0" y1="-8" x2="{30*f}" y2="-22"/>')
    else:
        g.append(f'<line x1="0" y1="-8" x2="{14*f}" y2="22"/>')
    g.append(f'<line x1="0" y1="-8" x2="{-14*f}" y2="22"/>')
    g.append('</g>')
    return "".join(g)


def laptop(x, y, s=1.0):
    """ノートPC（画面 280x170、台 340x34）"""
    g = [f'<g transform="translate({x},{y}) scale({s})" stroke="{INK}" stroke-width="4" stroke-linejoin="round">']
    g.append('<rect x="30" y="0" width="280" height="176" rx="10" fill="#33384d"/>')
    g.append('</g>')
    g.append(f'<g transform="translate({x},{y}) scale({s})">' + screen_scene(44, 14, 252, 148) + '</g>')
    g.append(f'<g transform="translate({x},{y}) scale({s})" stroke="{INK}" stroke-width="4" stroke-linejoin="round">')
    g.append('<rect x="44" y="14" width="252" height="148" fill="none" stroke-width="2"/>')
    g.append('<path d="M14 176 L326 176 L340 204 Q340 210 332 210 L8 210 Q0 210 0 204 Z" fill="#9aa5bd"/>')
    g.append('<rect x="140" y="182" width="60" height="8" rx="4" fill="#7a849b" stroke="none"/>')
    g.append('</g>')
    return "".join(g)


def rpi_board(x, y, w, h, usb_ys, lan_y=None, usbc_y=None):
    """Raspberry Pi 4 の基板。左の辺に LAN と USB-A（2段×2）、右の辺に USB-C"""
    g = [f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="22" fill="{BOARD}" stroke="{BOARD_D}" stroke-width="5"/>']
    # 取り付け穴
    for (hx, hy) in [(x + 34, y + 34), (x + w - 34, y + 34), (x + 34, y + h - 34), (x + w - 34, y + h - 34)]:
        g.append(f'<circle cx="{hx}" cy="{hy}" r="11" fill="#f6e7b0" stroke="{BOARD_D}" stroke-width="3"/>'
                 f'<circle cx="{hx}" cy="{hy}" r="5" fill="#fffdf7"/>')
    # GPIO の端子（上の辺）
    gx0, gx1 = x + 80, x + w - 80
    g.append(f'<rect x="{gx0}" y="{y+16}" width="{gx1-gx0}" height="26" rx="3" fill="#23263a"/>')
    n = int((gx1 - gx0 - 8) / 16)
    for i in range(n):
        for j in range(2):
            g.append(f'<rect x="{gx0+8+i*16}" y="{y+20+j*12}" width="6" height="6" fill="#e9c46a"/>')
    # LAN の口
    if lan_y is not None:
        g.append(f'<rect x="{x-56}" y="{lan_y-44}" width="86" height="88" rx="5" fill="#c3c9d2" stroke="{INK}" stroke-width="4"/>'
                 f'<rect x="{x-50}" y="{lan_y-26}" width="40" height="52" rx="3" fill="#23263a"/>'
                 f'<rect x="{x-44}" y="{lan_y-40}" width="10" height="8" fill="#7bd389"/><rect x="{x-26}" y="{lan_y-40}" width="10" height="8" fill="#ffd166"/>')
    # USB-A の口（2段の箱を2つ）
    for uy in usb_ys:
        g.append(f'<rect x="{x-56}" y="{uy-62}" width="86" height="124" rx="5" fill="#c3c9d2" stroke="{INK}" stroke-width="4"/>')
        for dy in (-30, 30):
            g.append(f'<rect x="{x-50}" y="{uy+dy-17}" width="44" height="34" rx="3" fill="#23263a"/>'
                     f'<rect x="{x-46}" y="{uy+dy-4}" width="32" height="10" fill="#4a90e2"/>')
    # USB-C の口
    if usbc_y is not None:
        g.append(f'<rect x="{x+w-22}" y="{usbc_y-20}" width="40" height="40" rx="5" fill="#c3c9d2" stroke="{INK}" stroke-width="4"/>'
                 f'<rect x="{x+w-10}" y="{usbc_y-12}" width="22" height="24" rx="8" fill="#23263a"/>')
    return "".join(g)


def inner_box(x, y, w, h, title, sub, stroke="#1d6e40", fill="#ffffff", tsize=27, ssize=22, tcol=INK, scol="#4a5068"):
    g = [f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="{fill}" stroke="{stroke}" stroke-width="4"/>']
    if sub:
        g.append(T(x + w / 2, y + h / 2 - 16, title, tsize, 700, tcol))
        g.append(T(x + w / 2, y + h / 2 + 18, sub, ssize, 500, scol))
    else:
        g.append(T(x + w / 2, y + h / 2, title, tsize, 700, tcol))
    return "".join(g)


# ---------- 図1：全体像 ----------

def fig_overview():
    W, H = 1600, 1000
    s = [head(W, H, "#fffdf7")]
    BX, BY, BW, BH = 620, 300, 420, 480
    LAN_Y = 360
    USB_YS = [500, 666]
    USBC_Y = 660
    BT_Y = 410

    # 左：LLM
    s.append(cloud(200, 118, 1.0))
    s.append(T(200, 88, "LLM", 22, 800, LLMC))
    s.append(T(200, 122, "Claude", 34, 800, LLMC))
    s.append(bubble(330, 10, 230, 58, 312, 86, ["ボタンを入れ替えよう"], size=20, stroke=LLMC))
    s.append(T(200, 196, "LLM（Claude）", 28, 800, INK))
    s.append(T(40, 236, "MCP で指示を出す", 21, 600, LLMC, "start"))
    s.append(T(40, 266, "（Wi-Fi・LAN、Tailscale 経由）", 21, 500, LLMC, "start"))
    # LLM → LAN の口（点線と Wi-Fi の記号）
    s.append(f'<path d="M332 140 C 450 150, 470 330, {BX-62} {LAN_Y-14}" fill="none" stroke="{LLMC}" stroke-width="6" '
             f'stroke-dasharray="2 12" stroke-linecap="round" marker-end="url(#arP)"/>')
    s.append(f'<circle cx="452" cy="222" r="30" fill="#fffdf7"/>' + wifi(452, 222, 1.0))

    # 左：スマホ
    s.append(phone(330, 300, 0.95))
    s.append(T(305, 388, "スマホのブラウザ", 26, 800, INK, "end"))
    s.append(T(305, 424, "ウェブ版コントローラ", 22, 500, "#4a5068", "end"))
    s.append(f'<path d="M448 400 C 500 400, 520 {LAN_Y+20}, {BX-62} {LAN_Y+20}" fill="none" stroke="{LLMC}" stroke-width="6" '
             f'stroke-dasharray="2 12" stroke-linecap="round" marker-end="url(#arP)"/>')
    s.append(T(500, 424, "Wi-Fi", 21, 600, LLMC))

    # 左：物理のゲームパッド
    s.append(f'<path d="M180 606 C 240 560, 420 {USB_YS[0]+30}, {BX-80} {USB_YS[0]+30}" fill="none" stroke="#555b70" stroke-width="7" stroke-linecap="round"/>')
    s.append(plug(BX - 86, USB_YS[0] + 30))
    s.append(f'<path d="M500 680 C 530 680, 520 {USB_YS[1]+30}, {BX-80} {USB_YS[1]+30}" fill="none" stroke="#555b70" stroke-width="7" stroke-linecap="round"/>')
    s.append(plug(BX - 86, USB_YS[1] + 30))
    s.append(pro_pad(36, 600, 1.0))
    s.append(arcade(262, 594, 0.92))
    s.append(T(268, 770, "物理のゲームパッド", 26, 800, INK))
    s.append(T(268, 806, "（USB-A の口に挿す）", 22, 500, "#4a5068"))

    # 中央：基板
    s.append(T(BX + BW / 2, 228, "LLMCon Hub", 46, 900, BOARD_D))
    s.append(T(BX + BW / 2, 274, "Raspberry Pi 4 の上で動く", 22, 600, "#4a5068"))
    s.append(rpi_board(BX, BY, BW, BH, USB_YS, LAN_Y, USBC_Y))
    bx0, bw = BX + 50, BW - 100
    s.append(inner_box(bx0, 370, bw, 100, "LLMCon のコア", "（改変の規則）"))
    s.append(inner_box(bx0, 490, bw, 100, "MCP サーバ", "（ウェブ版も配信）"))
    s.append(inner_box(bx0, 610, bw, 100, "Pro コントローラ", "として名乗る"))
    # 小さな部品
    s.append(f'<rect x="{BX+120}" y="{BY+BH-52}" width="54" height="30" rx="3" fill="#23263a"/>'
             f'<rect x="{BX+200}" y="{BY+BH-52}" width="90" height="30" rx="3" fill="#23263a"/>')

    # 右：出力先
    HX, HY = 1176, 220
    s.append(handheld(HX, HY, 0.98))
    s.append(screen_scene(HX + 78 * 0.98, HY + 14 * 0.98, 224 * 0.98, 152 * 0.98))
    s.append(stick_fighter(HX + 190, HY + 140, "#33384d", 1, 0.8))
    s.append(T(HX + 186, 436, "Nintendo Switch 2", 28, 800, INK))
    LX, LY = 1196, 548
    s.append(laptop(LX, LY, 0.98))
    s.append(stick_fighter(LX + 150, LY + 128, "#33384d", 1, 0.8))
    s.append(T(LX + 166, 790, "PC の Steam", 28, 800, INK))

    # 線（Bluetooth は実線、USB-C は破線）
    ex = BX + BW
    s.append(f'<path d="M{ex+4} {BT_Y} C 1110 {BT_Y}, 1120 330, {HX-8} 320" fill="none" stroke="{BT}" stroke-width="6" stroke-linecap="round" marker-end="url(#arB)"/>')
    s.append(f'<path d="M{ex+4} {BT_Y} C 1110 {BT_Y}, 1110 620, {LX+18} 620" fill="none" stroke="{BT}" stroke-width="6" stroke-linecap="round" marker-end="url(#arB)"/>')
    s.append(f'<path d="M{ex+20} {USBC_Y} C 1110 {USBC_Y}, 1120 380, {HX-8} 372" fill="none" stroke="#fffdf7" stroke-width="16"/>')
    s.append(f'<path d="M{ex+20} {USBC_Y} C 1110 {USBC_Y}, 1120 380, {HX-8} 372" fill="none" stroke="{USBC}" stroke-width="6" stroke-dasharray="16 10" marker-end="url(#arO)"/>')
    s.append(f'<path d="M{ex+20} {USBC_Y} C 1120 {USBC_Y}, 1130 680, {LX+18} 680" fill="none" stroke="{USBC}" stroke-width="6" stroke-dasharray="16 10" marker-end="url(#arO)"/>')
    s.append(radio(ex + 2, BT_Y, 1.0))

    # 凡例
    LGY = 868
    s.append(f'<rect x="300" y="{LGY-34}" width="1000" height="68" rx="34" fill="#ffffff" stroke="#d6d9e3" stroke-width="3"/>')
    s.append(f'<line x1="350" y1="{LGY}" x2="450" y2="{LGY}" stroke="{BT}" stroke-width="6" stroke-linecap="round"/>' + radio(400, LGY, 0.8))
    s.append(T(472, LGY, "Bluetooth（無線）", 24, 700, INK, "start"))
    s.append(f'<line x1="790" y1="{LGY}" x2="890" y2="{LGY}" stroke="{USBC}" stroke-width="6" stroke-dasharray="16 10"/>')
    s.append(T(912, LGY, "USB-C ケーブル（有線）", 24, 700, INK, "start"))

    s.append(T(W / 2, 950, "ゲームの側からは、ふつうの Pro コントローラに見える", 34, 800, BOARD_D))
    s.append('</svg>\n')
    return "".join(s)


# ---------- 図2：1台の Hub で2人 ----------

def fig_dual():
    W, H = 1600, 1000
    s = [head(W, H, "#fffdf7")]
    BX, BY, BW, BH = 600, 300, 440, 500
    USB_YS = [430, 670]
    USBC_Y = 430
    BT_Y = 670

    # 上：LLM と吹き出し
    s.append(cloud(800, 120, 1.0))
    s.append(T(800, 90, "LLM", 22, 800, LLMC))
    s.append(T(800, 124, "Claude", 34, 800, LLMC))
    s.append(bubble(236, 44, 390, 96, 676, 124, ["1P は強いので、", "一部のボタンを封じる"], size=24, stroke=P1, color="#9a4a0a"))
    s.append(bubble(1004, 56, 300, 72, 936, 110, ["2P はそのまま"], size=24, stroke=P2, color="#1a4f99"))

    # 中央：基板
    s.append(rpi_board(BX, BY, BW, BH, USB_YS, None, USBC_Y))
    s.append(T(BX + BW / 2, BY + BH - 32, "LLMCon Hub", 26, 800, "#ffffff"))
    b1 = (BX + 50, 370, 250, 120)
    b2 = (BX + 50, 610, 330, 120)
    s.append(inner_box(*b1, "LLMCon（1P）", "MCP 8777番", stroke=P1, fill="#fff1e3", tsize=26, ssize=24, scol="#9a4a0a"))
    s.append(inner_box(*b2, "LLMCon（2P）", "MCP 8778番", stroke=P2, fill="#e8f1ff", tsize=26, ssize=24, scol="#1a4f99"))
    # 1P の箱の中の「封じたボタン」の印
    s.append(f'<g transform="translate({b1[0]+b1[2]-6},{b1[1]+6})"><circle r="20" fill="#ffffff" stroke="{P1}" stroke-width="4"/>'
             f'<path d="M-9 -9L9 9M9 -9L-9 9" stroke="#d62828" stroke-width="5" stroke-linecap="round"/></g>')
    # 口から箱へ（基板の中の短い線）
    s.append(f'<line x1="{BX+30}" y1="{USB_YS[0]}" x2="{b1[0]}" y2="{USB_YS[0]}" stroke="{P1}" stroke-width="6"/>')
    s.append(f'<line x1="{BX+30}" y1="{USB_YS[1]}" x2="{b2[0]}" y2="{USB_YS[1]}" stroke="{P2}" stroke-width="6"/>')
    s.append(f'<line x1="{b1[0]+b1[2]}" y1="{USBC_Y}" x2="{BX+BW-22}" y2="{USBC_Y}" stroke="{P1}" stroke-width="6"/>')
    # MCP の矢印（雲から各箱へ）
    s.append(f'<path d="M760 168 C 740 240, 726 300, 726 {b1[1]-8}" fill="none" stroke="{P1}" stroke-width="7" stroke-linecap="round" marker-end="url(#arO)"/>')
    s.append(f'<path d="M860 172 C 900 240, 940 280, 940 400 L 940 {b2[1]-8}" fill="none" stroke="#fffdf7" stroke-width="18"/>')
    s.append(f'<path d="M860 172 C 900 240, 940 280, 940 400 L 940 {b2[1]-8}" fill="none" stroke="{P2}" stroke-width="7" stroke-linecap="round" marker-end="url(#arB)"/>')
    s.append(f'<rect x="672" y="236" width="76" height="34" rx="17" fill="#ffffff" stroke="{P1}" stroke-width="3"/>' + T(710, 253, "MCP", 20, 800, P1))
    s.append(f'<rect x="902" y="236" width="76" height="34" rx="17" fill="#ffffff" stroke="{P2}" stroke-width="3"/>' + T(940, 253, "MCP", 20, 800, P2))

    # 左：コントローラ2つ
    s.append(f'<path d="M352 420 C 450 420, 470 {USB_YS[0]-30}, {BX-80} {USB_YS[0]-30}" fill="none" stroke="{P1}" stroke-width="7" stroke-linecap="round"/>')
    s.append(plug(BX - 86, USB_YS[0] - 30))
    s.append(f'<path d="M372 690 C 450 690, 470 {USB_YS[1]+30}, {BX-80} {USB_YS[1]+30}" fill="none" stroke="{P2}" stroke-width="7" stroke-linecap="round"/>')
    s.append(plug(BX - 86, USB_YS[1] + 30))
    s.append(arcade(80, 340, 1.05, accent=P1))
    s.append(f'<circle cx="70" cy="330" r="30" fill="{P1}" stroke="{INK}" stroke-width="4"/>' + T(70, 331, "1P", 26, 900, "#ffffff"))
    s.append(T(216, 520, "アーケードコントローラ", 24, 800, INK))
    s.append(sfc_pad(92, 630, 1.0))
    s.append(f'<circle cx="92" cy="630" r="30" fill="{P2}" stroke="{INK}" stroke-width="4"/>' + T(92, 631, "2P", 26, 900, "#ffffff"))
    s.append(T(232, 790, "パッド", 24, 800, INK))

    # 右：ゲーム機と対戦の画面
    HX, HY, HS = 1166, 420, 1.06
    s.append(handheld(HX, HY, HS))
    sx, sy, sw, sh = HX + 78 * HS, HY + 14 * HS, 224 * HS, 152 * HS
    s.append(f'<rect x="{sx}" y="{sy}" width="{sw}" height="{sh}" fill="#ffe8cc"/>'
             f'<rect x="{sx}" y="{sy+sh*0.75}" width="{sw}" height="{sh*0.25}" fill="#d9b48f"/>')
    # 体力の帯
    s.append(f'<rect x="{sx+10}" y="{sy+10}" width="86" height="12" rx="3" fill="{P1}" stroke="{INK}" stroke-width="2"/>'
             f'<rect x="{sx+sw-96}" y="{sy+10}" width="86" height="12" rx="3" fill="{P2}" stroke="{INK}" stroke-width="2"/>')
    s.append(T(sx + sw / 2, sy + 18, "VS", 18, 900, "#d62828"))
    s.append(stick_fighter(sx + sw * 0.34, sy + sh * 0.76, P1, 1, 0.95))
    s.append(stick_fighter(sx + sw * 0.66, sy + sh * 0.76, P2, -1, 0.95, kick=True))
    s.append(T(HX + 190 * HS, 640, "Nintendo Switch 2（または PC）", 24, 800, INK))

    ex = BX + BW
    s.append(f'<path d="M{ex+20} {USBC_Y} C 1110 {USBC_Y}, 1110 490, {HX-8} 490" fill="none" stroke="{P1}" stroke-width="7" stroke-dasharray="18 10" marker-end="url(#arO)"/>')
    s.append(f'<path d="M{ex+4} {BT_Y} C 1110 {BT_Y}, 1110 570, {HX-8} 570" fill="none" stroke="{P2}" stroke-width="7" stroke-linecap="round" marker-end="url(#arB)"/>')
    s.append(radio(ex + 2, BT_Y, 1.0, P2))
    s.append(T(1112, 374, "1P：USB-C", 22, 800, P1))
    s.append(T(1150, 722, "2P：Bluetooth", 22, 800, P2))

    # 凡例
    LGY = 872
    s.append(f'<rect x="300" y="{LGY-34}" width="1000" height="68" rx="34" fill="#ffffff" stroke="#d6d9e3" stroke-width="3"/>')
    s.append(f'<line x1="350" y1="{LGY}" x2="450" y2="{LGY}" stroke="{BT}" stroke-width="6" stroke-linecap="round"/>' + radio(400, LGY, 0.8))
    s.append(T(472, LGY, "Bluetooth（無線）", 24, 700, INK, "start"))
    s.append(f'<line x1="790" y1="{LGY}" x2="890" y2="{LGY}" stroke="{USBC}" stroke-width="6" stroke-dasharray="16 10"/>')
    s.append(T(912, LGY, "USB-C ケーブル（有線）", 24, 700, INK, "start"))

    s.append(T(W / 2, 950, "2人の対戦を、Hub 1台で LLM がさばく（格闘ゲームのハンデマッチなど）", 32, 800, BOARD_D))
    s.append('</svg>\n')
    return "".join(s)


with open(os.path.join(OUT, "llmconhub_overview_v2.svg"), "w") as f:
    f.write(fig_overview())
with open(os.path.join(OUT, "llmconhub_dual.svg"), "w") as f:
    f.write(fig_dual())
print("ok")
