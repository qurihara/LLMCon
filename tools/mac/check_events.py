"""接続の事象のうち、実機で未確認だったもの（解放のエッジ、入力の並び）を確かめる。"""
import re, time
from llmcon import Mcp, VCon

H = "100.119.199.18"
m = Mcp(H)
v = VCon(H)


def has(btn):
    b = re.search(r"buttons=\[([^\]]*)\]", m.state()).group(1)
    return btn in b.split(",")


def clean():
    for c in re.findall(r"^(c\d+):", m.call("list_connections"), re.M):
        m.call("remove_connection", id=c)
    m.call("set_mapping", rules=[])
    time.sleep(0.3)


print("=" * 64)
print("1 解放のエッジを事象にした接続")
print("  cosense に「実装したがまだ実機では確認していない」とある項目。")
print()
clean()
r = m.call("add_connection",
           event={"type": "release", "button": "RB"},
           target={"host": "127.0.0.1", "port": 8777},
           action={"kind": "mapping", "durationSec": 1.5,
                   "rules": [{"op": "disable", "button": "Y"}]})
print(f"  登録: {r}  （RB を離したら Y を1.5秒無効にする）")
print()

v.press("RB"); time.sleep(0.4)
v.press("Y"); time.sleep(0.3)
print(f"  RB を押している最中に Y を押す → Y は出ているか: {has('Y')}   （True が正しい。まだ離していない）")
v.release("Y"); time.sleep(0.2)

v.release("RB"); time.sleep(0.4)
v.press("Y"); time.sleep(0.3)
print(f"  RB を離した直後に Y を押す     → Y は出ているか: {has('Y')}   （False なら解放のエッジが効いた）")
v.release("Y")
time.sleep(2.0)
v.press("Y"); time.sleep(0.3)
print(f"  2秒後にもう一度 Y を押す       → Y は出ているか: {has('Y')}   （True に戻るのが正しい）")
v.release("Y")

print()
print("=" * 64)
print("2 入力の並び（コマンド）を事象にした接続")
print("  波動拳のようなコマンドを検出する。研究の中心の例にあたる。")
print()
clean()
r = m.call("add_connection",
           event={"type": "sequence", "buttons": ["DDown", "DRight", "A"], "windowMs": 800},
           target={"host": "127.0.0.1", "port": 8777},
           action={"kind": "mapping", "durationSec": 1.5,
                   "rules": [{"op": "disable", "button": "X"}]})
print(f"  登録: {r}  （下、右、A の順に押したら X を1.5秒無効にする）")
print()


def tap(b, hold=0.08, gap=0.12):
    v.press(b); time.sleep(hold); v.release(b); time.sleep(gap)


print("  まちがった順（右、下、A）を入力する")
tap("DRight"); tap("DDown"); tap("A")
time.sleep(0.3)
v.press("X"); time.sleep(0.3)
print(f"    → X は出ているか: {has('X')}   （True が正しい。並びが違うので発火しない）")
v.release("X"); time.sleep(0.5)

print("  正しい順（下、右、A）を入力する")
tap("DDown"); tap("DRight"); tap("A")
time.sleep(0.3)
v.press("X"); time.sleep(0.3)
print(f"    → X は出ているか: {has('X')}   （False なら並びの検出が効いた）")
v.release("X")

print()
print("  接続の発火回数")
print("   ", m.call("list_connections"))
print()
print("  事象の記録")
print("   ", m.call("get_events", count=8).replace("\n", "\n    "))

clean()
v.close()
print()
print("後始末: 接続と改変ルールを消した")
