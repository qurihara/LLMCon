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
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();

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

    /// <summary>
    /// 1回の送信がこの時間を超えて終わらない接続は、止まったものと判断して切る（提案E1、2026-11-09）。
    /// 受信を読み出さない接続があると、相手と自分の送信の溜まり場が満杯になり、送信が終わらなくなるからである。
    /// </summary>
    public static TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>順に送る合図（reload など）を、1つの接続あたり何件まで溜めるか。超えたら、その接続を切る。</summary>
    public const int MaxQueuedMessages = 256;

    public Guid Register(WebSocket s)
    {
        var id = Guid.NewGuid();
        var c = new Client(s);
        _clients[id] = c;
        c.Start();
        return id;
    }

    public void Unregister(Guid id)
    {
        if (_clients.TryRemove(id, out var c)) c.Stop();
    }

    /// <summary>いまつながっている接続の数（試験と状態の表示に使う）。</summary>
    public int ClientCount => _clients.Count;

    public async Task BroadcastReloadAsync() => await BroadcastTextAsync("reload");

    /// <summary>
    /// つながっている全ページへテキストを送る。捨ててはならない合図（reload など）に使う。
    /// 接続ごとの待ち行列に入れるだけで、送信の完了は待たない。1つの接続が詰まっても、ほかの接続と呼び出し側は止まらない。
    /// </summary>
    public Task BroadcastTextAsync(string text)
    {
        foreach (var kv in _clients) kv.Value.Enqueue(text);
        return Task.CompletedTask;
    }

    /// <summary>
    /// つながっている全ページへ、種別 key の最新の値だけを送る。パッドの出力やマイクのレベルのように、
    /// 新しい値が届けば古い値が不要になるものに使う。送れていない古い値は、新しい値で置き換えて捨てる。
    /// </summary>
    public void BroadcastLatest(string key, string text)
    {
        foreach (var kv in _clients) kv.Value.SetLatest(key, text);
    }

    /// <summary>
    /// 1つの接続への送信を受け持つ。接続ごとに専用の送信の処理を1つ立て、送信を直列にする
    /// （WebSocket は同時に1つの送信しか受け付けない）。
    /// </summary>
    private sealed class Client
    {
        private readonly WebSocket _ws;
        private readonly object _gate = new();
        private readonly System.Collections.Generic.Queue<string> _queue = new();
        private readonly System.Collections.Generic.Dictionary<string, string> _latest = new();
        private readonly System.Collections.Generic.List<string> _latestOrder = new();
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
        private readonly CancellationTokenSource _stop = new();
        private int _pending;   // 0 = 合図を出していない、1 = 出した

        public Client(WebSocket ws) => _ws = ws;

        public void Start() => _ = Task.Run(LoopAsync);

        public void Stop()
        {
            try { _stop.Cancel(); } catch { }
            Kick();
        }

        public void Enqueue(string text)
        {
            bool overflow;
            lock (_gate)
            {
                overflow = _queue.Count >= MaxQueuedMessages;
                if (!overflow) _queue.Enqueue(text);
            }
            if (overflow) { Abort(); return; }
            Kick();
        }

        public void SetLatest(string key, string text)
        {
            lock (_gate)
            {
                if (!_latest.ContainsKey(key)) _latestOrder.Add(key);
                _latest[key] = text;
            }
            Kick();
        }

        private void Kick()
        {
            if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0) _signal.Release();
        }

        private bool TryTake(out string? text)
        {
            lock (_gate)
            {
                if (_queue.Count > 0) { text = _queue.Dequeue(); return true; }
                if (_latestOrder.Count > 0)
                {
                    var key = _latestOrder[0];
                    _latestOrder.RemoveAt(0);
                    text = _latest[key];
                    _latest.Remove(key);
                    return true;
                }
            }
            text = null;
            return false;
        }

        private async Task LoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);
                    Volatile.Write(ref _pending, 0);
                    while (TryTake(out var text))
                    {
                        if (_ws.State != WebSocketState.Open) return;
                        var data = Encoding.UTF8.GetBytes(text!);
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        cts.CancelAfter(SendTimeout);
                        // 時間切れで取り消すと、WebSocket は中断の状態になる。受信の側の処理が例外で抜け、Unregister が呼ばれる。
                        await _ws.SendAsync(data, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch { Abort(); }
        }

        private void Abort()
        {
            try { _ws.Abort(); } catch { }
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
