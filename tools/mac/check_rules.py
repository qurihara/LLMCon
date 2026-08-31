"""改変の語彙が、遠隔の LLMCon で人間入力に対して効くかを確かめる。"""
import re, time
from llmcon import Mcp, VCon

H = "100.119.199.18"
m = Mcp(H)
v = VCon(H)


def btns():
    s = m.state()
    b = re.search(r"buttons=\[([^\]]*)\]", s).group(1)
    return "なし" if b == "-" else b


def axes():
    s = m.state()
    d = dict(re.findall(r"(LT|RT|LX|LY|RX|RY)=(-?\d+)", s))
    return d


def loop_hz():
    return re.search(r"loop=(\d+)Hz", m.state()).group(1)


def clear():
    m.call("set_mapping", rules=[])
    v.stick("left", 0, 0)
    v.stick("right", 0, 0)
    time.sleep(0.2)


print("=" * 60)
print("1 無効化（disable）")
clear()
m.call("set_mapping", rules=[{"op": "disable", "button": "A"}])
v.press("A"); v.press("B"); time.sleep(0.3)
print(f"   A と B を押した → 出力 {btns()}   （A だけ消えるのが正しい）")
v.release("A"); v.release("B")

print()
print("2 差し替え（remap。A と B を入れ替える）")
clear()
m.call("set_mapping", rules=[{"op": "remap", "from": "A", "to": "B"},
                             {"op": "remap", "from": "B", "to": "A"}])
v.press("A"); time.sleep(0.3)
print(f"   A を押した → 出力 {btns()}   （B になるのが正しい）")
v.release("A"); time.sleep(0.2)
v.press("B"); time.sleep(0.3)
print(f"   B を押した → 出力 {btns()}   （A になるのが正しい）")
v.release("B")

print()
print("3 反転（invert。押していないときに On）")
clear()
m.call("set_mapping", rules=[{"op": "invert", "button": "X"}])
time.sleep(0.3)
print(f"   何も押していない → 出力 {btns()}   （X が出るのが正しい）")
v.press("X"); time.sleep(0.3)
print(f"   X を押した       → 出力 {btns()}   （X が消えるのが正しい）")
v.release("X")

print()
print("4 連打（turbo。5Hz で B を点滅させ、10回見る）")
clear()
m.call("set_mapping", rules=[{"op": "turbo", "button": "B", "hz": 5}])
v.press("B")
seen = []
for _ in range(10):
    seen.append("B" in btns())
    time.sleep(0.06)
v.release("B")
on = sum(seen)
print(f"   押しっぱなしで10回観測 → 出ていた回数 {on}   （0でも10でもなければ点滅している）")

print()
print("5 アナログ変換（左スティックを右いっぱいに倒す）")
clear()
v.stick("left", 1.0, 0.0); time.sleep(0.3)
print(f"   改変なし          → LX={axes()['LX']}   （32767 前後が正しい）")
m.call("set_mapping", rules=[{"op": "gain", "axis": "LS", "amount": 0.5}])
time.sleep(0.3)
print(f"   感度 0.5          → LX={axes()['LX']}   （半分になるのが正しい）")
m.call("set_mapping", rules=[{"op": "invert", "axis": "LX"}])
time.sleep(0.3)
print(f"   反転              → LX={axes()['LX']}   （負になるのが正しい）")
m.call("set_mapping", rules=[{"op": "rotate", "axis": "LS", "amount": 90}])
time.sleep(0.3)
a = axes()
print(f"   90度回転          → LX={a['LX']} LY={a['LY']}   （X が 0 に、Y が正になるのが正しい）")
clear()

print()
print(f"ループの速度: {loop_hz()}Hz")
v.close()
