# アイコン生成。描画は tools\IconMaker.cs（C#）にあり、それを Add-Type でコンパイルして呼ぶ。
# 出力: CntlLevelConnection\app.ico（汎用・琥珀）, installer\LLMCon.ico（同じ）, installer\KuriCon.ico（赤）。
$repo = "C:\Users\user\Desktop\claude_work\CntlLevelConnection"
$cs = Get-Content (Join-Path $repo "tools\IconMaker.cs") -Raw
Add-Type -TypeDefinition $cs -ReferencedAssemblies System.Drawing
[IconMaker]::Write((Join-Path $repo "CntlLevelConnection\app.ico"), "#f4a340")
[IconMaker]::Write((Join-Path $repo "installer\LLMCon.ico"), "#f4a340")
[IconMaker]::Write((Join-Path $repo "installer\KuriCon.ico"), "#d64545")
foreach ($f in @("CntlLevelConnection\app.ico", "installer\LLMCon.ico", "installer\KuriCon.ico")) {
  Write-Output ("wrote {0} ({1} bytes)" -f $f, (Get-Item (Join-Path $repo $f)).Length)
}
