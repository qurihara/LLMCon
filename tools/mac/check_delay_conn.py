"""反応遅延（delay）の精度と、単体でのリアクティブな自己改変を確かめる。"""
import re, statistics, time
from llmcon import Mcp, VCon

H = "100.119.199.18"
m = Mcp(H)
v = VCon(H)


def has(btn):
    s = m.state()
    b = re.search(r"buttons=\[([^\]]*)\]", s).group(1)
    return btn in b.split(",")


def clear():
    m.call("set_mapping", rules=[])
    time.sleep(0.3)


def measure(delay_ms, trials=5):
    """A を押してから、出力に現れるまでの時間を測る。"""
    out = []
    for _ in range(trials):
        clear()
        if delay_ms:
            m.call("set_mapping", rules=[{"op": "delay", "delayMs": delay_ms}])
        time.sleep(0.4)
        t0 = time.perf_counter()
        v.press("A")
        while True:
            if has("A"):
                out.append((time.perf_counter() - t0) * 1000)
                break
            if time.perf_counter() - t0 > 3.0:
                out.append(float("nan"))
                break
        v.release("A")
        time.sleep(0.4)
    return out


print("=" * 64)
print("反応遅延（delay）の実測")
print("押してから出力に現れるまで。観測はネットワーク越しの取得なので、")
print("基準（改変なし）に含まれる往復のぶんを差し引いて考える。")
print()
base = measure(0)
print(f"  改変なし   : 中央値 {statistics.median(base):6.1f} ミリ秒   "
      f"（{', '.join(f'{x:.0f}' for x in base)}）")
b = statistics.median(base)
for d in (100, 300):
    r = measure(d)
    med = statistics.median(r)
    print(f"  delay {d:3d}ms: 中央値 {med:6.1f} ミリ秒   "
          f"（{', '.join(f'{x:.0f}' for x in r)}）")
    print(f"               基準との差 {med - b:6.1f} ミリ秒   （設定値 {d} に近いほど正確）")
clear()

print()
print("=" * 64)
print("単体でのリアクティブな自己改変（自分自身を相手にした接続）")
print("cosense に「設計上は可能だが、まだ実機では確認していない」とある項目。")
print()
for c in re.findall(r"^(c\d+):", m.call("list_connections"), re.M):
    m.call("remove_connection", id=c)

r = m.call("add_connection",
           event={"type": "press", "button": "LB"},
           target={"host": "127.0.0.1", "port": 8777},
           action={"kind": "mapping", "durationSec": 1.5,
                   "rules": [{"op": "disable", "button": "A"},
                             {"op": "disable", "button": "B"}]})
print(f"  接続を登録: {r}")
print(f"  一覧      : {m.call('list_connections')}")
print()

print("  まず LB を押さずに A を押す")
v.press("A"); time.sleep(0.3)
print(f"    → A は出ているか: {has('A')}   （True が正しい）")
v.release("A"); time.sleep(0.3)

print("  LB を押して自分に作用させ、その直後に A を押す")
v.press("LB"); time.sleep(0.15); v.release("LB")
time.sleep(0.2)
v.press("A"); time.sleep(0.3)
print(f"    → A は出ているか: {has('A')}   （False なら自己改変が効いている）")
print(f"    → 状態: {m.state()}")
v.release("A")

print("  2秒待って作用が切れるのを確かめる")
time.sleep(2.0)
v.press("A"); time.sleep(0.3)
print(f"    → A は出ているか: {has('A')}   （True に戻るのが正しい）")
v.release("A")

print()
print("  事象の記録（get_events）")
print("   ", m.call("get_events", count=6).replace("\n", "\n    "))

for c in re.findall(r"^(c\d+):", m.call("list_connections"), re.M):
    m.call("remove_connection", id=c)
clear()
v.close()
print()
print("後始末: 接続と改変ルールを消した")
