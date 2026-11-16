# 洛雪音乐音源 本地下载脚本
# 用途：GitHub 间歇性打不开时，先把音源 js 下载到本地，再在洛雪里"本地导入"
# 用法：右键"使用 PowerShell 运行"，或在终端执行  .\下载洛雪音源.ps1

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 音源清单（key = 保存的文件名，value = 仓库内路径）
$sources = [ordered]@{
    'lx'        = 'lx/latest.js'
    'xinghai'   = 'xinghai/latest.js'
    'qingliang' = 'qingliang/latest.js'
    'suyin'     = 'suyin/latest.js'
    'huanyin'   = 'huanyin/latest.js'
    'changqing' = 'changqing/latest.js'
    'fish'      = 'fish/latest.js'
    'xinglan'   = 'xinglan/latest.js'
}

# 加速站（依次尝试，成功即停）
$proxies = @(
    'https://gh-proxy.org/raw.githubusercontent.com/',
    'https://gh.llkk.cc/raw.githubusercontent.com/',
    'https://github.moeyy.xyz/raw.githubusercontent.com/',
    'https://ghproxy.cn/raw.githubusercontent.com/',
    'https://ghp.ci/raw.githubusercontent.com/',
    'https://gh.api.99988866.xyz/raw.githubusercontent.com/',
    'https://ghproxy.net/raw.githubusercontent.com/',
    'https://raw.githubusercontent.com/'
)

$repo   = 'pdone/lx-music-source/main/'
$outDir = Join-Path $PSScriptRoot 'lx-sources'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "保存目录: $outDir`n" -ForegroundColor Cyan
$ok = @(); $fail = @()

foreach ($name in $sources.Keys) {
    $path  = $sources[$name]
    $file  = Join-Path $outDir "$name.js"
    $done  = $false

    foreach ($p in $proxies) {
        $url  = $p + $repo + $path
        $site = ([uri]$p).Host
        $tmp  = [IO.Path]::GetTempFileName()
        $sw   = [Diagnostics.Stopwatch]::StartNew()
        try {
            Invoke-WebRequest -Uri $url -OutFile $tmp -TimeoutSec 25 -UseBasicParsing
            $sw.Stop()
            $len = (Get-Item $tmp).Length
            # 有效音源脚本至少几十 KB，太小的多半是错误页
            if ($len -gt 5000) {
                Move-Item -Path $tmp -Destination $file -Force
                $kb = [math]::Round($len / 1KB)
                Write-Host ("  [OK]   {0,-11} {1,6} KB  {2,4:N1}s  经 {3}" -f $name, $kb, $sw.Elapsed.TotalSeconds, $site) -ForegroundColor Green
                $ok += $name; $done = $true; break
            }
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        } catch {
            Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not $done) {
        Write-Host ("  [FAIL] {0,-11} 所有加速站都失败" -f $name) -ForegroundColor Red
        $fail += $name
    }
}

Write-Host "`n完成：成功 $($ok.Count) 个，失败 $($fail.Count) 个" -ForegroundColor Cyan
if ($fail.Count) { Write-Host "失败：$($fail -join ', ')" -ForegroundColor Yellow }
Write-Host @"

下一步：
  1. 打开洛雪 → 设置 → 自定义源管理 → 导入 → 本地导入
  2. 选择 $outDir 里的 .js 文件（可按住 Ctrl 多选 / 或逐个导入）
  3. 勾选导入后的音源，回主界面搜索即可
"@ -ForegroundColor Gray
