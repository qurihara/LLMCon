#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""6.6節・6.7節・6.8節の事例を、実際に遊んで確かめる。

  6.7 一対多の分配   親1人の押下で，子2人の移動が1秒間だけ効かなくなる（shooter.html）
  6.8 自分自身への接続 攻撃のボタンを押すと，自分の回避が1秒間だけ効かなくなる（rpg.html）
  6.6 片手のコントローラ 片手用のデザインに差し替えて，実際に遊ぶ（platformer.html）

3つのゲームはいずれも ?ws=ホスト:ポート で、どの LLMCon から状態を受け取るかを選べる。
ゲームのコードには手を加えない。

使い方: python3 play_three_cases.py [67|68|66|all]
"""
import json, os, re, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from browser_gamepad import Browser, Page
from llmcon import Mcp, VCon

HOST = '127.0.0.1'
PARENT, CHILD1, CHILD2 = 8777, 8778, 8779
BASE = 'http://127.0.0.1:8080'
HERE = os.path.dirname(os.path.abspath(__file__))


def clean(ports):
    for p in ports:
        m = Mcp(HOST, p)
        for line in m.call('list_connections').splitlines():
            mm = re.match(r'\s*(c\d+):', line)
            if mm:
                m.call('remove_connection', id=mm.group(1))
        m.call('set_mapping', rules=[])


def fires(m):
    mm = re.search(r'fired\s+(\d+)x', m.call('list_connections'))
    return int(mm.group(1)) if mm else 0


def wait_probe(p, sec=12):
    for _ in range(int(sec / 0.3)):
        if p.eval('!!window.__probe') is True:
            return True
        time.sleep(0.3)
    return False


# ---------------------------------------------------------------- 6.7
def case_67():
    """一人の行動を全員へ配る。親の押下で、子2人の移動が同時に止まる。"""
    print('== 6.7 一対多の分配 ==')
    clean([PARENT, CHILD1, CHILD2])
    br = Browser(); br.close_all(); time.sleep(0.6)

    pages = {}
    for name, port in (('親', PARENT), ('子1', CHILD1), ('子2', CHILD2)):
        pg = br.open(f'{BASE}/shooter.html?ws={HOST}:{port}',
                     llmcon=f'{HOST}:{port}', wait=False)
        time.sleep(1.2); pg.keep_awake()
        pages[name] = pg
    time.sleep(1.5)
    for name, pg in pages.items():
        print(f'  {name}: 状態を読める = {wait_probe(pg)}')

    m = Mcp(HOST, PARENT)
    ids = []
    for port in (CHILD1, CHILD2):
        r = m.call('add_connection',
                   event={'type': 'press', 'button': 'RB'},
                   target={'host': HOST, 'port': port},
                   action={'kind': 'mapping', 'durationSec': 1.0,
                           'rules': [{'op': 'disable', 'button': 'DLeft'},
                                     {'op': 'disable', 'button': 'DRight'}]})
        ids.append(r.strip())
    print('  接続:', ' / '.join(ids))

    vs = {n: VCon(HOST, p) for n, p in (('親', PARENT), ('子1', CHILD1), ('子2', CHILD2))}
    for n, pg in pages.items():                      # ゲームを始める
        vs[n].press('Start'); time.sleep(0.1); vs[n].release('Start'); time.sleep(0.6)
    time.sleep(0.8)
    for n, pg in pages.items():
        print(f'  {n} の遊技: {pg.eval("window.__probe.state")}')

    def move_test(n, direction, sec=0.8):
        """指定した向きへ倒して、自機がどれだけ動いたかを返す。

        向きを毎回替えるのは、同じ向きに倒し続けると自機が画面の端に張り付き、
        改変とは関係なく動かなくなってしまうためである。
        """
        pg = pages[n]
        x0 = pg.eval('window.__probe.view.px')
        vs[n].press(direction); time.sleep(sec); vs[n].release(direction)
        time.sleep(0.05)
        x1 = pg.eval('window.__probe.view.px')
        return None if x0 is None or x1 is None else round(abs(x1 - x0), 1)

    def ensure_playing():
        """どの画面も遊技中にしておく。やられて終わっていれば始め直す。"""
        for _ in range(12):
            st = {n: pages[n].eval('window.__probe.state') for n in pages}
            if all(x == 'PLAY' for x in st.values()):
                return st
            for n in pages:
                if st[n] != 'PLAY':
                    vs[n].press('Start'); time.sleep(0.08)
                    vs[n].release('Start')
            time.sleep(0.7)
        return st

    rows = []
    n0 = fires(m)
    for i in range(10):
        st = ensure_playing()
        if any(x != 'PLAY' for x in st.values()):
            print(f'  {i+1:2d} 遊技中にできなかった: {st}')
            continue
        d1 = 'DRight' if i % 2 == 0 else 'DLeft'      # 端に張り付かないよう毎回替える
        d2 = 'DLeft' if i % 2 == 0 else 'DRight'
        before = {n: move_test(n, d1) for n in ('子1', '子2')}
        vs['親'].press('RB'); time.sleep(0.06); vs['親'].release('RB')
        during = {n: move_test(n, d2, 0.35) for n in ('子1', '子2')}
        time.sleep(1.2)
        after = {n: move_test(n, d1) for n in ('子1', '子2')}
        rows.append({'before': before, 'during': during, 'after': after})
        print(f'  {i+1:2d} 掛ける前 子1={before["子1"]} 子2={before["子2"]}  '
              f'窓の中 子1={during["子1"]} 子2={during["子2"]}  '
              f'窓のあと 子1={after["子1"]} 子2={after["子2"]}')
    print(f'  発火回数 {n0} -> {fires(m)}')

    for n, pg in pages.items():
        pg.screenshot(os.path.join(HERE, f'shot_67_{ "parent" if n=="親" else n }.png'))
    json.dump(rows, open(os.path.join(HERE, 'play_67_result.json'), 'w'), ensure_ascii=False)
    for v in vs.values():
        v.close()
    return rows


# ---------------------------------------------------------------- 6.8
def case_68():
    """自分の入力が自分の操作を変える。攻撃を出すと回避が1秒間できなくなる。"""
    print('\n== 6.8 自分自身への接続 ==')
    clean([PARENT])
    br = Browser(); br.close_all(); time.sleep(0.6)
    pg = br.open(f'{BASE}/rpg.html?ws={HOST}:{PARENT}', llmcon=f'{HOST}:{PARENT}', wait=False)
    time.sleep(2.0); pg.keep_awake()

    m = Mcp(HOST, PARENT)
    cid = m.call('add_connection',
                 event={'type': 'press', 'button': 'A'},          # 攻撃
                 target={'host': HOST, 'port': PARENT},            # 相手は自分自身
                 action={'kind': 'mapping', 'durationSec': 1.0,
                         'rules': [{'op': 'disable', 'button': 'B'}]})  # 回避
    print('  接続（相手は自分自身）:', cid.strip())

    v = VCon(HOST, PARENT)
    rows = []
    n0 = fires(m)
    for i in range(10):
        v.press('B'); time.sleep(0.25)
        st_before = m.call('get_state')
        v.release('B'); time.sleep(0.15)
        v.press('A'); time.sleep(0.08); v.release('A'); time.sleep(0.15)   # 攻撃を出す
        v.press('B'); time.sleep(0.25)
        st_during = m.call('get_state')
        v.release('B')
        time.sleep(1.1)
        v.press('B'); time.sleep(0.25)
        st_after = m.call('get_state')
        v.release('B'); time.sleep(0.2)
        got = lambda s: 'B' in (re.search(r'buttons=\[([^\]]*)\]', s).group(1) or '')
        rows.append({'before': got(st_before), 'during': got(st_during), 'after': got(st_after)})
        print(f'  {i+1:2d} 攻撃の前に回避={got(st_before)}  '
              f'攻撃の直後に回避={got(st_during)}  1秒後に回避={got(st_after)}')
    print(f'  発火回数 {n0} -> {fires(m)}')

    # 作用が作用を呼ぶ連鎖が起きないことも確かめる
    print('  注入した入力が次の事象を引き起こさないこと（暴走しないこと）を確かめる')
    n1 = fires(m)
    m.call('tap', buttons=['A'], frames=6)             # 大規模言語モデルによる注入
    time.sleep(0.8)
    print(f'    注入による A の押下では発火 {n1} -> {fires(m)}（増えなければ設計どおり）')

    pg.screenshot(os.path.join(HERE, 'shot_68_rpg.png'))
    json.dump(rows, open(os.path.join(HERE, 'play_68_result.json'), 'w'), ensure_ascii=False)
    v.close()
    return rows


# ---------------------------------------------------------------- 6.6
def case_66():
    """片手用のデザインに差し替えて、実際に遊ぶ。"""
    print('\n== 6.6 片手のコントローラ ==')
    clean([CHILD2])
    m = Mcp(HOST, CHILD2)
    html = open(os.path.join(HERE, 'onehanded_ui.html'), encoding='utf-8').read()
    print('  デザインの差し替え:', m.call('set_controller_ui', html=html).strip()[:80])

    br = Browser(); br.close_all(); time.sleep(0.6)
    pg = br.open(f'{BASE}/platformer.html?ws={HOST}:{CHILD2}',
                 llmcon=f'{HOST}:{CHILD2}', wait=False)
    time.sleep(2.0); pg.keep_awake()
    # 差し替えた片手用のコントローラそのものも開く（人が触る画面である）
    ui = br.open(f'http://{HOST}:{CHILD2}/vcon.html', llmcon=f'{HOST}:{CHILD2}', wait=False)
    time.sleep(1.5); ui.keep_awake()

    v = VCon(HOST, CHILD2)
    for _ in range(8):                       # 遊技中になるまで開始のボタンを押す
        st = pg.eval('window.__probe ? window.__probe.state : null')
        if st == 'play':
            break
        v.press('Start'); time.sleep(0.1); v.release('Start'); time.sleep(0.7)
    print('  遊技の状態:', pg.eval('window.__probe ? window.__probe.state : null'))

    def snap():
        return pg.eval('(() => { const p = window.__probe; if (!p) return null;'
                       ' return JSON.stringify({state: p.state, score: p.score,'
                       ' lives: p.lives, x: Math.round(p.view.x), y: Math.round(p.view.y),'
                       ' onGround: p.view.onGround}); })()')

    print('  始めた直後:', snap())
    moved = []
    for i in range(10):
        # やられて止まっていたら始め直す
        for _ in range(8):
            if pg.eval('window.__probe.state') == 'play':
                break
            v.press('Start'); time.sleep(0.1); v.release('Start'); time.sleep(0.7)
        a = json.loads(snap() or '{}')
        v.press('DRight'); time.sleep(0.5)
        v.press('A'); time.sleep(0.14); v.release('A')      # 跳ぶ
        time.sleep(0.5); v.release('DRight'); time.sleep(0.4)
        b = json.loads(snap() or '{}')
        dx = (b.get('x') or 0) - (a.get('x') or 0)
        jumped = (a.get('onGround') is True) and (b.get('y') != a.get('y'))
        moved.append({'a': a, 'b': b, 'dx': round(dx, 1), 'jumped': jumped})
        print(f'  {i+1:2d} 右へ {dx:6.1f} 進んだ  跳んだ={jumped}  '
              f'状態={b.get("state")} 得点={b.get("score")} 残機={b.get("lives")}')

    ui.screenshot(os.path.join(HERE, 'shot_66_controller.png'))
    pg.screenshot(os.path.join(HERE, 'shot_66_platformer.png'))
    json.dump(moved, open(os.path.join(HERE, 'play_66_result.json'), 'w'), ensure_ascii=False)
    v.close()
    return moved


if __name__ == '__main__':
    which = sys.argv[1] if len(sys.argv) > 1 else 'all'
    if which in ('67', 'all'):
        case_67()
    if which in ('68', 'all'):
        case_68()
    if which in ('66', 'all'):
        case_66()
