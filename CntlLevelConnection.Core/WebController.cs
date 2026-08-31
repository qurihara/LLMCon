using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CntlLevelConnection;

/// <summary>
/// ウェブ版の仮想コントローラを支える部品。
/// 固定の土台ページ（HarnessHtml）と、差し替え可能なデザイン（DefaultDesign を初期値とする）を持つ。
/// 土台ページが WebSocket でつながり、入力をエンジンへ送る。デザインを差し替えたときは、
/// つながっているページへ「再読み込み」を送って反映する。
/// </summary>
public sealed class WebController
{
    private readonly object _lock = new();
    private string _current;
    private string _previous;
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();

    public WebController()
    {
        _current = DefaultDesign;
        _previous = DefaultDesign;
    }

    /// <summary>
    /// ページの題に出す名前。製品の名前を入れておくと、ブラウザのタブに内部の名前が出ない（Issue #20）。
    /// 起動時に設定する。設定しなければ、これまでどおり LLMCon と出る。
    /// </summary>
    public string PageTitle { get; set; } = "LLMCon";

    public string Harness => HarnessHtml.Replace("LLMCon Virtual Controller", $"{PageTitle} Virtual Controller");
    public string GetDesign() { lock (_lock) return _current; }
    public void SetUi(string html) { lock (_lock) { _previous = _current; _current = html ?? ""; } }
    public void Reset() { lock (_lock) { _previous = _current; _current = DefaultDesign; } }
    public void RevertPrevious() { lock (_lock) { (_current, _previous) = (_previous, _current); } }

    public Guid Register(WebSocket s) { var id = Guid.NewGuid(); _sockets[id] = s; return id; }
    public void Unregister(Guid id) { _sockets.TryRemove(id, out _); }

    public async Task BroadcastReloadAsync() => await BroadcastTextAsync("reload");

    /// <summary>つながっている全ページへテキストを送る（マイクのレベル表示などの状態配信に使う）。</summary>
    public async Task BroadcastTextAsync(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        foreach (var kv in _sockets)
        {
            var s = kv.Value;
            if (s.State == WebSocketState.Open)
            {
                try { await s.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None); }
                catch { /* ignore broken sockets */ }
            }
        }
    }

    // ── 固定の土台ページ ──────────────────────────────────
    // WebSocket接続・入力配線(data-btn / data-stick)・エラー捕捉・既定への復旧 を持つ。
    // デザインのスクリプトが壊れても、ボタンの入力配線は土台側にあるので動き続ける。
    public const string HarnessHtml =
"""
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>LLMCon Virtual Controller</title>
<style>
  html,body{margin:0;height:100%;background:#1a1a2e;color:#eee;font-family:sans-serif;overflow:hidden;-webkit-user-select:none;user-select:none;touch-action:none;}
  #vcon-bar{position:fixed;top:0;left:0;right:0;height:28px;display:flex;align-items:center;gap:8px;padding:0 8px;background:#0d0d1f;font-size:12px;z-index:9999;box-sizing:border-box;}
  #vcon-status{color:#888;}
  #vcon-reset{margin-left:auto;cursor:pointer;background:#333;border:0;color:#eee;padding:2px 10px;border-radius:4px;font-size:12px;}
  #vcon-error{display:none;position:fixed;top:28px;left:0;right:0;background:#5a1a1a;color:#fff;padding:6px 10px;font-size:12px;z-index:9998;white-space:pre-wrap;}
  #design{position:absolute;top:28px;left:0;right:0;bottom:0;}
</style>
</head>
<body>
<div id="vcon-bar">
  <span id="vcon-status">接続中...</span>
  <button id="vcon-reset">既定のデザインに戻す</button>
</div>
<div id="vcon-error"></div>
<div id="design"></div>
<script>
(function(){
  var statusEl=document.getElementById('vcon-status');
  var errEl=document.getElementById('vcon-error');
  var ws=null, wsReady=false;
  function setStatus(t,c){ statusEl.textContent=t; statusEl.style.color=c||'#888'; }
  function showError(msg){ errEl.style.display='block'; errEl.textContent='デザインのスクリプトでエラーが発生しました: '+msg+'\n（ボタンは引き続き使えます。「既定のデザインに戻す」で復旧できます）'; }
  function connect(){
    try{
      ws=new WebSocket('ws://'+location.host+'/vcon/ws');
      ws.onopen=function(){ wsReady=true; setStatus('接続済み','#7CFC7C'); };
      ws.onclose=function(){ wsReady=false; setStatus('切断しました。再接続します...','#ff6b6b'); setTimeout(connect,1000); };
      ws.onmessage=function(e){
        if(e.data==='reload'){ location.reload(); return; }
        try{ var m=JSON.parse(e.data); document.dispatchEvent(new CustomEvent('vcon-msg',{detail:m})); }catch(ex){}
      };
    }catch(ex){ setStatus('接続に失敗しました','#ff6b6b'); setTimeout(connect,1500); }
  }
  function send(obj){ if(wsReady){ try{ ws.send(JSON.stringify(obj)); }catch(ex){} } }
  window.vcon={
    press:function(b){ send({t:'btn',b:b,d:true}); },
    release:function(b){ send({t:'btn',b:b,d:false}); },
    stick:function(s,x,y){ send({t:'stick',s:s,x:x,y:y}); },
    send:function(o){ send(o); }
  };
  var heldStick={};
  var heldBtn={};
  function closestAttr(el,attr){ return (el && el.closest) ? el.closest('['+attr+']') : null; }
  document.addEventListener('pointerdown',function(e){
    var bEl=closestAttr(e.target,'data-btn');
    if(bEl){ e.preventDefault(); try{ bEl.setPointerCapture(e.pointerId); }catch(ex){} var tag=bEl.getAttribute('data-btn'); heldBtn[e.pointerId]={el:bEl,tag:tag}; window.vcon.press(tag); bEl.style.opacity='0.6'; return; }
    var sEl=closestAttr(e.target,'data-stick');
    if(sEl){ e.preventDefault(); try{ sEl.setPointerCapture(e.pointerId); }catch(ex){} heldStick[e.pointerId]=sEl; updateStick(sEl,e); }
  },{passive:false});
  document.addEventListener('pointermove',function(e){ var s=heldStick[e.pointerId]; if(s){ e.preventDefault(); updateStick(s,e); } },{passive:false});
  // 押したボタンをポインタごとに覚え、そのボタンを離す。ボタンが動いても取りこぼさない。
  function endPointer(e){
    var hb=heldBtn[e.pointerId];
    if(hb){ window.vcon.release(hb.tag); try{ hb.el.style.opacity=''; }catch(ex){} delete heldBtn[e.pointerId]; }
    var s=heldStick[e.pointerId];
    if(s){ window.vcon.stick(s.getAttribute('data-stick'),0,0); delete heldStick[e.pointerId]; }
  }
  document.addEventListener('pointerup',endPointer);
  document.addEventListener('pointercancel',endPointer);
  function updateStick(el,e){
    var r=el.getBoundingClientRect();
    var nx=((e.clientX-r.left)/r.width)*2-1;
    var ny=((e.clientY-r.top)/r.height)*2-1;
    nx=Math.max(-1,Math.min(1,nx)); ny=Math.max(-1,Math.min(1,ny));
    window.vcon.stick(el.getAttribute('data-stick'),nx,-ny);
  }
  window.addEventListener('error',function(e){ showError((e&&e.message)?e.message:'unknown'); });
  fetch('/vcon/design').then(function(r){return r.text();}).then(function(html){
    var c=document.getElementById('design');
    c.innerHTML=html;
    var scripts=c.querySelectorAll('script');
    for(var i=0;i<scripts.length;i++){
      try{
        var old=scripts[i];
        var s=document.createElement('script');
        if(old.src){ s.src=old.src; } else { s.textContent=old.textContent; }
        document.body.appendChild(s);
      }catch(ex){ showError(ex.message); }
    }
  }).catch(function(ex){ showError('デザインの取得に失敗しました: '+ex); });
  document.getElementById('vcon-reset').addEventListener('click',function(){
    fetch('/vcon/reset',{method:'POST'}).then(function(){ location.reload(); }).catch(function(){ location.reload(); });
  });
  connect();
})();
</script>
</body>
</html>
""";

    // ── 既定のデザイン（差し替え可能な部分）──────────────────
    // data-btn と data-stick を付けてあるので、土台側の配線だけで動く（スクリプト不要）。
    public const string DefaultDesign =
"""
<style>
  #gp{display:flex;height:100%;align-items:center;justify-content:space-between;padding:0 4%;box-sizing:border-box;}
  #gp .col{display:flex;flex-direction:column;gap:12px;align-items:center;}
  #gp .row{display:flex;gap:8px;}
  #gp .b{display:flex;align-items:center;justify-content:center;color:#fff;font-weight:bold;background:#3a3a6a;border-radius:10px;font-size:16px;cursor:pointer;}
  #gp .b.s{width:54px;height:34px;font-size:13px;background:#5a5a8a;border-radius:8px;}
  #gp .b.rc{border-radius:50%;width:56px;height:56px;}
  #gp .dpad{display:grid;grid-template-columns:repeat(3,48px);grid-template-rows:repeat(3,48px);gap:4px;}
  #gp .face{display:grid;grid-template-columns:repeat(3,58px);grid-template-rows:repeat(3,58px);gap:4px;}
  #gp .cell{}
  #gp .stick{width:110px;height:110px;border-radius:50%;background:#22223a;border:2px solid #444;cursor:pointer;}
</style>
<div id="gp">
  <div class="col">
    <div class="row"><div class="b s" data-btn="LT">LT</div><div class="b s" data-btn="LB">LB</div></div>
    <div class="dpad">
      <span class="cell"></span><div class="b" data-btn="DUp">▲</div><span class="cell"></span>
      <div class="b" data-btn="DLeft">◀</div><span class="cell"></span><div class="b" data-btn="DRight">▶</div>
      <span class="cell"></span><div class="b" data-btn="DDown">▼</div><span class="cell"></span>
    </div>
    <div class="stick" data-stick="left"></div>
    <div class="b s" data-btn="LS">LS</div>
  </div>
  <div class="col">
    <!-- 実物の Xbox のコントローラに合わせて View と Menu と書く。data-btn は XInput と
         同じ Back と Start のままである（表示だけの読み替え）。 -->
    <div class="b s" data-btn="Back">View</div>
    <div class="b rc" data-btn="Guide" style="width:50px;height:50px;background:#1a6a1a;">⏺</div>
    <div class="b s" data-btn="Start">Menu</div>
  </div>
  <div class="col">
    <div class="row"><div class="b s" data-btn="RB">RB</div><div class="b s" data-btn="RT">RT</div></div>
    <div class="face">
      <span class="cell"></span><div class="b rc" data-btn="Y" style="background:#b8860b;">Y</div><span class="cell"></span>
      <div class="b rc" data-btn="X" style="background:#1a3a9a;">X</div><span class="cell"></span><div class="b rc" data-btn="B" style="background:#9a1a1a;">B</div>
      <span class="cell"></span><div class="b rc" data-btn="A" style="background:#1a7a1a;">A</div><span class="cell"></span>
    </div>
    <div class="stick" data-stick="right"></div>
    <div class="b s" data-btn="RS">RS</div>
  </div>
</div>
""";
}
