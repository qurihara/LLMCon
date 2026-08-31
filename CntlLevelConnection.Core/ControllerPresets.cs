using System;
using System.Collections.Generic;
using System.Linq;

namespace CntlLevelConnection;

/// <summary>
/// ウェブ版の仮想コントローラの、あらかじめ用意したデザイン（プリセット）集。
/// それぞれが「このコントローラがあれば、このコンセプトを示せる」という狙いを持つ。
/// 中身は土台ページ（vcon.html）へ流し込む HTML であり、data-btn と data-stick で入力が配線される。
/// 動くボタンや入れ替わるボタンでも、土台側がポインタごとに押したボタンを覚えるので取りこぼさない。
/// </summary>
public static class ControllerPresets
{
    public sealed record Preset(string Name, string Concept, string Html);

    public static IReadOnlyList<Preset> All { get; } = new List<Preset>
    {
        new("famicom",       "レトロなファミコン風。角ばった十字キーと丸い赤い A と B、細長い Select と Start だけの、最小限のデザイン。", Famicom),
        new("retro-analog",  "アナログスティックを備えた、落ち着いた配色のおしゃれな家庭用コントローラ風。左右のスティック、十字、色分けした ABXY、肩ボタン。", RetroAnalog),
        new("inclusive-xl",  "インクルージョンを意識した、とても大きく高コントラストで、最低限のボタン（十字と A と B）だけのデザイン。", InclusiveXl),
        new("hard-tiny",     "とても小さいボタンが画面の隅々に離れて散らばる、押しにくい難しいコントローラ。", HardTiny),
        new("moving",        "ボタンが絶えず動き回る、難しいコントローラ。狙いを定めにくい。", Moving),
        new("shrinking",     "時間とともにボタンが小さくなっていく、だんだん難しくなるコントローラ。動的な難易度調整を示す。", Shrinking),
        new("shuffle",       "一定の間隔でボタンの配置が入れ替わる、覚えた位置が崩れる難しいコントローラ。", Shuffle),
        new("fitts",         "よく使うボタンを大きく手前に、めったに使わないボタンを小さく遠くに置く。フィッツの法則による操作しやすさの勾配を示す。", Fitts),
        new("one-button",    "画面いっぱいの巨大なボタンが1つだけ。極限まで単純にした、限られた入力のコントローラ。", OneButton),
        new("piano",         "ソフトウェアキーボードのように鍵盤を並べたコントローラ。音を並べる操作系を示す。", Piano),
        new("neon-art",      "ネオンで光る芸術的なデザイン。コントローラの見た目そのものを表現の対象にできることを示す。", NeonArt),
        new("rhythm",        "画面下に判定ラインを置いた4レーンの音ゲー風。音ゲーとの接続に向いた操作系を示す。", Rhythm),
        new("hidden",        "ボタンがほとんど見えず、押したときだけ浮かび上がる。知覚しにくさによる難易度を示す。", Hidden),
        new("one-handed",    "すべての操作を片側に寄せてまとめた、片手で扱えるコントローラ。アクセシビリティを示す。", OneHanded),
        new("fighting-mic",  "格闘ゲーム用。十字と、Guide を除くすべてのボタンを名前のまま並べ、全ボタンの反転チェックボックスと、マイクのしきい値でボタンを操作する設定を持つ。", FightingMic),
    };

    public static string? Get(string name)
        => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Html;

    public static string Names() => string.Join(", ", All.Select(p => p.Name));

    public static string List() => string.Join("\n", All.Select(p => $"{p.Name}: {p.Concept}"));

    // ── 以下、各プリセットの HTML ──────────────────────────

    private const string Famicom = """
<style>
#fc{height:100%;display:flex;align-items:center;justify-content:space-between;padding:0 7%;box-sizing:border-box;background:#2b2b2b;font-family:Arial,sans-serif;}
#fc .dpad{display:grid;grid-template-columns:repeat(3,54px);grid-template-rows:repeat(3,54px);}
#fc .dpad .d{background:#141414;color:#777;display:flex;align-items:center;justify-content:center;font-size:20px;}
#fc .dpad .c{background:#141414;}
#fc .mid{display:flex;gap:16px;}
#fc .cap{width:74px;height:26px;border-radius:13px;background:#141414;color:#eee;font-size:11px;display:flex;align-items:center;justify-content:center;letter-spacing:1px;}
#fc .ab{display:flex;gap:22px;}
#fc .rb{width:66px;height:66px;border-radius:50%;background:#c0392b;color:#fff;font-weight:bold;font-size:22px;display:flex;align-items:center;justify-content:center;box-shadow:0 5px 0 #7b241c;}
</style>
<div id="fc">
  <div class="dpad">
    <span></span><div class="d" data-btn="DUp">▲</div><span></span>
    <div class="d" data-btn="DLeft">◀</div><div class="c"></div><div class="d" data-btn="DRight">▶</div>
    <span></span><div class="d" data-btn="DDown">▼</div><span></span>
  </div>
  <div class="mid">
    <div class="cap" data-btn="Back">SELECT</div>
    <div class="cap" data-btn="Start">START</div>
  </div>
  <div class="ab">
    <div class="rb" data-btn="B">B</div>
    <div class="rb" data-btn="A">A</div>
  </div>
</div>
""";

    private const string RetroAnalog = """
<style>
#ra{height:100%;display:flex;align-items:center;justify-content:space-between;padding:0 4%;box-sizing:border-box;background:radial-gradient(circle at 50% 25%,#3a3f5a,#1e2030);font-family:'Segoe UI',Arial,sans-serif;}
#ra .col{display:flex;flex-direction:column;align-items:center;gap:14px;}
#ra .sh{display:flex;gap:10px;}
#ra .p{background:#4a4f6a;color:#dfe3ff;border-radius:8px;display:flex;align-items:center;justify-content:center;box-shadow:0 3px 6px rgba(0,0,0,.4);}
#ra .p.s{width:56px;height:30px;font-size:12px;}
#ra .dpad{display:grid;grid-template-columns:repeat(3,40px);grid-template-rows:repeat(3,40px);gap:3px;}
#ra .dp{background:#4a4f6a;color:#dfe3ff;display:flex;align-items:center;justify-content:center;border-radius:6px;font-size:15px;}
#ra .stick{width:104px;height:104px;border-radius:50%;background:radial-gradient(circle at 40% 35%,#5b6188,#20233a);border:3px solid #2a2e48;box-shadow:inset 0 4px 10px rgba(0,0,0,.5);}
#ra .face{display:grid;grid-template-columns:repeat(3,56px);grid-template-rows:repeat(3,56px);}
#ra .rc{width:52px;height:52px;border-radius:50%;color:#fff;font-weight:600;font-size:18px;display:flex;align-items:center;justify-content:center;box-shadow:0 3px 5px rgba(0,0,0,.4);}
#ra .mid{display:flex;flex-direction:column;gap:12px;align-items:center;}
#ra .cap{width:44px;height:22px;border-radius:11px;background:#3a3f5a;color:#aeb4e0;font-size:12px;display:flex;align-items:center;justify-content:center;}
</style>
<div id="ra">
  <div class="col">
    <div class="sh"><div class="p s" data-btn="LB">LB</div><div class="p s" data-btn="LT">LT</div></div>
    <div class="dpad">
      <span></span><div class="dp" data-btn="DUp">▲</div><span></span>
      <div class="dp" data-btn="DLeft">◀</div><span></span><div class="dp" data-btn="DRight">▶</div>
      <span></span><div class="dp" data-btn="DDown">▼</div><span></span>
    </div>
    <div class="stick" data-stick="left"></div>
  </div>
  <div class="mid">
    <div class="cap" data-btn="Back">≡</div>
    <div class="cap" data-btn="Start">▶</div>
  </div>
  <div class="col">
    <div class="sh"><div class="p s" data-btn="RT">RT</div><div class="p s" data-btn="RB">RB</div></div>
    <div class="face">
      <span></span><div class="rc" data-btn="Y" style="background:#e1b12c;">Y</div><span></span>
      <div class="rc" data-btn="X" style="background:#487eb0;">X</div><span></span><div class="rc" data-btn="B" style="background:#c0392b;">B</div>
      <span></span><div class="rc" data-btn="A" style="background:#44bd32;">A</div><span></span>
    </div>
    <div class="stick" data-stick="right"></div>
  </div>
</div>
""";

    private const string InclusiveXl = """
<style>
#ix{height:100%;display:flex;gap:3%;padding:3%;box-sizing:border-box;background:#000;font-family:Arial,sans-serif;}
#ix .side{flex:1;display:grid;gap:3%;}
#ix .dirs{grid-template-columns:1fr 1fr 1fr;grid-template-rows:1fr 1fr 1fr;}
#ix .big{font-size:44px;font-weight:bold;border-radius:18px;display:flex;align-items:center;justify-content:center;}
#ix .dirs .big{background:#1a1a1a;color:#fff;border:4px solid #fff;}
#ix .acts{grid-template-columns:1fr;grid-template-rows:1fr 1fr;}
#ix .a{background:#00b894;color:#00281f;} #ix .b{background:#0984e3;color:#02233d;}
</style>
<div id="ix">
  <div class="side dirs">
    <span></span><div class="big" data-btn="DUp">↑</div><span></span>
    <div class="big" data-btn="DLeft">←</div><span></span><div class="big" data-btn="DRight">→</div>
    <span></span><div class="big" data-btn="DDown">↓</div><span></span>
  </div>
  <div class="side acts">
    <div class="big a" data-btn="A">A</div>
    <div class="big b" data-btn="B">B</div>
  </div>
</div>
""";

    private const string HardTiny = """
<style>
#ht{height:100%;position:relative;background:#101014;font-family:Arial,sans-serif;}
#ht .t{position:absolute;width:26px;height:26px;border-radius:50%;background:#2d3436;color:#b2bec3;font-size:11px;display:flex;align-items:center;justify-content:center;border:1px solid #444;}
</style>
<div id="ht">
  <div class="t" data-btn="DUp" style="left:6%;top:9%;">↑</div>
  <div class="t" data-btn="DDown" style="left:10%;bottom:8%;">↓</div>
  <div class="t" data-btn="DLeft" style="left:2%;top:48%;">←</div>
  <div class="t" data-btn="DRight" style="left:17%;top:53%;">→</div>
  <div class="t" data-btn="A" style="right:6%;bottom:10%;">A</div>
  <div class="t" data-btn="B" style="right:15%;top:15%;">B</div>
  <div class="t" data-btn="X" style="right:2%;top:47%;">X</div>
  <div class="t" data-btn="Y" style="right:19%;bottom:45%;">Y</div>
  <div class="t" data-btn="Start" style="left:48%;top:4%;">≡</div>
</div>
""";

    private const string Moving = """
<style>
#mv{height:100%;position:relative;overflow:hidden;background:#141018;font-family:Arial,sans-serif;}
#mv .m{position:absolute;width:60px;height:60px;border-radius:50%;color:#fff;font-weight:bold;font-size:20px;display:flex;align-items:center;justify-content:center;transition:left .9s ease-in-out,top .9s ease-in-out;box-shadow:0 3px 8px rgba(0,0,0,.5);}
</style>
<div id="mv">
  <div class="m" data-btn="A" style="background:#e84393;left:20%;top:30%;">A</div>
  <div class="m" data-btn="B" style="background:#0984e3;left:60%;top:52%;">B</div>
  <div class="m" data-btn="X" style="background:#00b894;left:38%;top:70%;">X</div>
  <div class="m" data-btn="Y" style="background:#fdcb6e;color:#333;left:70%;top:18%;">Y</div>
</div>
<script>
(function(){
  var root=document.getElementById('mv'); if(!root) return;
  var els=root.querySelectorAll('.m');
  function move(){ els.forEach(function(e){ e.style.left=(8+Math.random()*78)+'%'; e.style.top=(8+Math.random()*78)+'%'; }); }
  setInterval(move,1400);
})();
</script>
""";

    private const string Shrinking = """
<style>
#sk{height:100%;display:flex;align-items:center;justify-content:center;gap:8%;background:#0d1b2a;font-family:Arial,sans-serif;}
#sk .g{width:130px;height:130px;border-radius:50%;color:#fff;font-weight:bold;font-size:28px;display:flex;align-items:center;justify-content:center;transition:transform .5s linear;}
</style>
<div id="sk">
  <div class="g" data-btn="A" style="background:#1abc9c;">A</div>
  <div class="g" data-btn="B" style="background:#e74c3c;">B</div>
</div>
<script>
(function(){
  var root=document.getElementById('sk'); if(!root) return;
  var els=root.querySelectorAll('.g'); var s=1;
  setInterval(function(){ s=Math.max(0.22,s-0.05); els.forEach(function(e){ e.style.transform='scale('+s+')'; }); },1500);
})();
</script>
""";

    private const string Shuffle = """
<style>
#sf{height:100%;display:grid;grid-template-columns:repeat(2,120px);grid-template-rows:repeat(2,120px);gap:22px;align-content:center;justify-content:center;background:#161616;font-family:Arial,sans-serif;}
#sf .q{border-radius:16px;color:#fff;font-weight:bold;font-size:26px;display:flex;align-items:center;justify-content:center;transition:background .3s;}
</style>
<div id="sf"><div class="q"></div><div class="q"></div><div class="q"></div><div class="q"></div></div>
<script>
(function(){
  var root=document.getElementById('sf'); if(!root) return;
  var qs=root.querySelectorAll('.q');
  var defs=[{t:'A',c:'#27ae60'},{t:'B',c:'#c0392b'},{t:'X',c:'#2980b9'},{t:'Y',c:'#f39c12'}];
  function apply(order){ order.forEach(function(v,idx){ var q=qs[idx],d=defs[v]; q.textContent=d.t; q.setAttribute('data-btn',d.t); q.style.background=d.c; }); }
  var order=[0,1,2,3];
  function shuffle(){ for(var i=order.length-1;i>0;i--){ var j=Math.floor(Math.random()*(i+1)); var t=order[i];order[i]=order[j];order[j]=t; } apply(order); }
  apply(order); setInterval(shuffle,1600);
})();
</script>
""";

    private const string Fitts = """
<style>
#ft{height:100%;position:relative;background:#1e272e;font-family:Arial,sans-serif;}
#ft .n{position:absolute;border-radius:50%;color:#fff;font-weight:bold;display:flex;align-items:center;justify-content:center;}
</style>
<div id="ft">
  <div class="n" data-btn="A" style="right:8%;bottom:16%;width:120px;height:120px;font-size:30px;background:#00a8ff;">A</div>
  <div class="n" data-btn="B" style="right:33%;bottom:30%;width:74px;height:74px;font-size:22px;background:#4cd137;">B</div>
  <div class="n" data-btn="X" style="right:29%;top:14%;width:46px;height:46px;font-size:15px;background:#9c88ff;">X</div>
  <div class="n" data-btn="Y" style="right:6%;top:8%;width:32px;height:32px;font-size:12px;background:#e1b12c;color:#333;">Y</div>
  <div class="n" data-btn="DLeft" style="left:8%;bottom:22%;width:66px;height:66px;font-size:22px;background:#485460;">◀</div>
  <div class="n" data-btn="DRight" style="left:26%;bottom:26%;width:66px;height:66px;font-size:22px;background:#485460;">▶</div>
  <div class="n" data-btn="DUp" style="left:17%;bottom:44%;width:56px;height:56px;font-size:18px;background:#485460;">▲</div>
  <div class="n" data-btn="DDown" style="left:17%;bottom:6%;width:56px;height:56px;font-size:18px;background:#485460;">▼</div>
</div>
""";

    private const string OneButton = """
<style>
#ob{height:100%;display:flex;align-items:center;justify-content:center;background:#111;font-family:Arial,sans-serif;}
#ob .huge{width:70vmin;height:70vmin;max-width:92%;max-height:92%;border-radius:50%;background:radial-gradient(circle at 45% 38%,#ff7675,#d63031);color:#fff;font-size:52px;font-weight:bold;display:flex;align-items:center;justify-content:center;box-shadow:0 12px 0 #a02020,0 16px 34px rgba(0,0,0,.5);}
</style>
<div id="ob"><div class="huge" data-btn="A">A</div></div>
""";

    private const string Piano = """
<style>
#pn{height:100%;display:flex;align-items:flex-end;justify-content:center;gap:3px;padding-bottom:6%;box-sizing:border-box;background:#20232a;font-family:Arial,sans-serif;}
#pn .k{width:60px;height:72%;background:linear-gradient(#fff,#e4e4e4);border:1px solid #999;border-radius:0 0 6px 6px;color:#555;display:flex;align-items:flex-end;justify-content:center;padding-bottom:12px;font-size:13px;box-shadow:inset 0 -6px 8px rgba(0,0,0,.08);}
</style>
<div id="pn">
  <div class="k" data-btn="DLeft">←</div>
  <div class="k" data-btn="DDown">↓</div>
  <div class="k" data-btn="DUp">↑</div>
  <div class="k" data-btn="DRight">→</div>
  <div class="k" data-btn="A">A</div>
  <div class="k" data-btn="B">B</div>
  <div class="k" data-btn="X">X</div>
  <div class="k" data-btn="Y">Y</div>
</div>
""";

    private const string NeonArt = """
<style>
#na{height:100%;display:flex;align-items:center;justify-content:space-around;background:#0a0014;font-family:'Trebuchet MS',Arial,sans-serif;}
#na .neon{width:84px;height:84px;border-radius:50%;background:transparent;border:3px solid;display:flex;align-items:center;justify-content:center;font-size:26px;font-weight:bold;}
#na .a{color:#0ff;border-color:#0ff;box-shadow:0 0 12px #0ff,inset 0 0 12px #0ff;text-shadow:0 0 8px #0ff;}
#na .b{color:#f0f;border-color:#f0f;box-shadow:0 0 12px #f0f,inset 0 0 12px #f0f;text-shadow:0 0 8px #f0f;}
#na .x{color:#ff0;border-color:#ff0;box-shadow:0 0 12px #ff0,inset 0 0 12px #ff0;text-shadow:0 0 8px #ff0;}
#na .y{color:#5f8;border-color:#5f8;box-shadow:0 0 12px #5f8,inset 0 0 12px #5f8;text-shadow:0 0 8px #5f8;}
#na .stick{width:120px;height:120px;border-radius:50%;border:3px solid #7d5fff;box-shadow:0 0 16px #7d5fff,inset 0 0 16px #7d5fff;}
</style>
<div id="na">
  <div class="stick" data-stick="left"></div>
  <div class="neon a" data-btn="A">A</div>
  <div class="neon b" data-btn="B">B</div>
  <div class="neon x" data-btn="X">X</div>
  <div class="neon y" data-btn="Y">Y</div>
</div>
""";

    private const string Rhythm = """
<style>
#rh{height:100%;display:flex;flex-direction:column;background:#0b1020;font-family:Arial,sans-serif;}
#rh .lanes{flex:1;display:grid;grid-template-columns:repeat(4,1fr);gap:2px;}
#rh .ln{background:linear-gradient(#141b33,#0b1020);}
#rh .judge{height:96px;display:grid;grid-template-columns:repeat(4,1fr);gap:2px;}
#rh .pad{display:flex;align-items:center;justify-content:center;font-size:24px;font-weight:bold;color:#fff;border-top:3px solid #ff5e7e;}
#rh .p0{background:#e84393;} #rh .p1{background:#0984e3;} #rh .p2{background:#00b894;} #rh .p3{background:#fdcb6e;color:#333;}
</style>
<div id="rh">
  <div class="lanes"><div class="ln"></div><div class="ln"></div><div class="ln"></div><div class="ln"></div></div>
  <div class="judge">
    <div class="pad p0" data-btn="DLeft">←</div>
    <div class="pad p1" data-btn="DDown">↓</div>
    <div class="pad p2" data-btn="DUp">↑</div>
    <div class="pad p3" data-btn="DRight">→</div>
  </div>
</div>
""";

    private const string Hidden = """
<style>
#hd{height:100%;position:relative;background:#000;font-family:Arial,sans-serif;}
#hd .g{position:absolute;width:80px;height:80px;border-radius:50%;color:#0d0d0d;background:#0a0a0a;display:flex;align-items:center;justify-content:center;font-size:22px;font-weight:bold;transition:background .15s,color .15s;}
#hd .g:active{background:#3a3a3a;color:#eee;}
</style>
<div id="hd">
  <div class="g" data-btn="A" style="left:20%;top:56%;">A</div>
  <div class="g" data-btn="B" style="left:60%;top:30%;">B</div>
  <div class="g" data-btn="X" style="left:40%;top:20%;">X</div>
  <div class="g" data-btn="Y" style="left:70%;top:64%;">Y</div>
</div>
""";

    // 格闘ゲーム用（反転チェックボックスとマイク設定つき）。
    // 反転は {t:"uirules"} でUI由来のルール層を差し替え、マイクは {t:"miccfg"} で設定する。
    // マイクの音量レベルは、アプリが {t:"miclvl"} を配ってくるのを受けてメーターに表示する。
    private const string FightingMic = """
<style>
#fm{height:100%;display:flex;box-sizing:border-box;background:#14161f;font-family:'Segoe UI',Arial,sans-serif;color:#dde;}
#fm .play{flex:1.6;display:flex;align-items:center;justify-content:space-around;padding:0 2%;}
#fm .dpad{display:grid;grid-template-columns:repeat(3,52px);grid-template-rows:repeat(3,52px);gap:4px;}
#fm .dp{background:#2b2f42;color:#cfd6ff;display:flex;align-items:center;justify-content:center;border-radius:8px;font-size:18px;}
#fm .atk{display:grid;grid-template-columns:repeat(4,74px);grid-template-rows:repeat(3,58px);gap:8px;}
#fm .ab{border-radius:10px;background:#2f3550;color:#dde;font-weight:bold;font-size:15px;display:flex;align-items:center;justify-content:center;box-shadow:0 3px 6px rgba(0,0,0,.4);}
#fm .panel{flex:1;border-left:1px solid #2b2f42;padding:10px 14px;box-sizing:border-box;overflow-y:auto;font-size:12px;}
#fm h4{margin:8px 0 6px;font-size:12px;color:#9fb0ff;letter-spacing:.06em;}
#fm .inv{display:grid;grid-template-columns:repeat(4,1fr);gap:3px 8px;}
#fm .inv label{display:flex;align-items:center;gap:4px;cursor:pointer;}
#fm #meter{width:100%;height:14px;background:#0c0e15;border:1px solid #2b2f42;position:relative;}
#fm #mfill{height:100%;width:0;background:#57c78a;}
#fm #mthr{position:absolute;top:0;bottom:0;width:2px;background:#ffb347;}
#fm .row{display:flex;align-items:center;gap:6px;margin:5px 0;}
#fm select,#fm input[type=range]{background:#1c2030;color:#dde;border:1px solid #2b2f42;}
</style>
<div id="fm">
  <div class="play">
    <div class="dpad">
      <span></span><div class="dp" data-btn="DUp">▲</div><span></span>
      <div class="dp" data-btn="DLeft">◀</div><span></span><div class="dp" data-btn="DRight">▶</div>
      <span></span><div class="dp" data-btn="DDown">▼</div><span></span>
    </div>
    <!-- ボタンには何の技かを書かない。どのボタンをどの技に当てるかは、Steam や
         ゲーム側の設定で決めるものだからである。ここではコントローラの名前だけを
         淡々と並べる。Guide だけは置いていない。押しても Steam や Xbox の
         オーバーレイに飲まれて、ゲームまで届かないためである。 -->
    <div class="atk">
      <div class="ab" data-btn="A">A</div>
      <div class="ab" data-btn="B">B</div>
      <div class="ab" data-btn="X">X</div>
      <div class="ab" data-btn="Y">Y</div>
      <div class="ab" data-btn="LB">LB</div>
      <div class="ab" data-btn="RB">RB</div>
      <div class="ab" data-btn="LT">LT</div>
      <div class="ab" data-btn="RT">RT</div>
      <div class="ab" data-btn="LS">LS</div>
      <div class="ab" data-btn="RS">RS</div>
      <div class="ab" data-btn="Back">View</div>
      <div class="ab" data-btn="Start">Menu</div>
    </div>
  </div>
  <div class="panel">
    <h4>入力の反転（チェックしたボタンは、押していないとき On になります）</h4>
    <div class="inv" id="inv"></div>
    <h4>マイクでボタンを操作</h4>
    <div class="row"><label><input type="checkbox" id="mic-en"> 有効</label>
      <span>ボタン</span><select id="mic-btn"></select>
      <span>モード</span><select id="mic-mode"><option value="hold">超えている間</option><option value="toggle">切り替え</option><option value="tap">短く押す</option></select>
    </div>
    <div id="meter"><div id="mfill"></div><div id="mthr" style="left:20%;"></div></div>
    <div class="row"><span>しきい値</span><input type="range" id="mic-thr" min="0.02" max="0.9" step="0.01" value="0.2" style="flex:1;"><span id="mic-thr-v">0.20</span></div>
  </div>
</div>
<script>
(function(){
  // 反転の対象と、声を割り当てられる先。タスクトレイの TrayButtons と同じものを並べる。
  // LS と RS（スティックの押し込み）を含めてある。理由は App.xaml.cs に書いた。
  var tags=['A','B','X','Y','LB','RB','LT','RT','LS','RS','DUp','DDown','DLeft','DRight','Start','Back'];
  // 画面に出す名前。いまの Xbox のコントローラの呼び名に合わせる。内部のタグは、
  // XInput や ViGEm と同じ Start と Back のままにしてある（設定ファイルや MCP の
  // 呼び出しとの互換のため）。表示だけを読み替える。
  var labels={Start:'Menu',Back:'View'};
  function nameOf(t){ return labels[t]||t; }
  var inv=document.getElementById('inv');
  var boxes={};
  tags.forEach(function(t){
    var l=document.createElement('label');
    var c=document.createElement('input'); c.type='checkbox';
    c.addEventListener('change',sendRules);
    var s=document.createElement('span'); s.textContent=nameOf(t);
    l.appendChild(c); l.appendChild(s); inv.appendChild(l);
    boxes[t]=c;
  });
  function sendRules(){
    var rules=[];
    tags.forEach(function(t){ if(boxes[t].checked){ rules.push({op:'invert',button:t}); } });
    window.vcon.send({t:'uirules',rules:rules});
  }
  var en=document.getElementById('mic-en'), btnSel=document.getElementById('mic-btn'),
      modeSel=document.getElementById('mic-mode'), thr=document.getElementById('mic-thr'),
      thrV=document.getElementById('mic-thr-v'), mfill=document.getElementById('mfill'), mthr=document.getElementById('mthr');
  tags.forEach(function(t){ var o=document.createElement('option'); o.value=t; o.textContent=nameOf(t); btnSel.appendChild(o); });
  btnSel.value='RT';
  function sendMic(){
    thrV.textContent=parseFloat(thr.value).toFixed(2);
    mthr.style.left=(parseFloat(thr.value)*100)+'%';
    window.vcon.send({t:'miccfg',on:en.checked,b:btnSel.value,thr:parseFloat(thr.value),mode:modeSel.value});
  }
  en.addEventListener('change',sendMic);
  btnSel.addEventListener('change',sendMic);
  modeSel.addEventListener('change',sendMic);
  thr.addEventListener('input',sendMic);
  document.addEventListener('vcon-msg',function(e){
    var m=e.detail;
    if(m&&m.t==='miclvl'){ mfill.style.width=Math.min(100,m.v*100)+'%'; }
  });
})();
</script>
""";

    private const string OneHanded = """
<style>
#oh{height:100%;display:flex;align-items:center;justify-content:flex-start;padding-left:9%;box-sizing:border-box;background:#161a2b;font-family:Arial,sans-serif;}
#oh .cluster{display:grid;grid-template-columns:repeat(3,60px);grid-template-rows:repeat(3,60px);gap:8px;}
#oh .b{border-radius:10px;color:#fff;font-weight:bold;font-size:16px;display:flex;align-items:center;justify-content:center;background:#3d4468;}
#oh .a{background:#44bd32;} #oh .r{background:#c0392b;}
</style>
<div id="oh">
  <div class="cluster">
    <div class="b" data-btn="Y">Y</div><div class="b" data-btn="DUp">▲</div><div class="b" data-btn="X">X</div>
    <div class="b" data-btn="DLeft">◀</div><div class="b a" data-btn="A">A</div><div class="b" data-btn="DRight">▶</div>
    <div class="b r" data-btn="B">B</div><div class="b" data-btn="DDown">▼</div><div class="b" data-btn="Start">≡</div>
  </div>
</div>
""";
}
