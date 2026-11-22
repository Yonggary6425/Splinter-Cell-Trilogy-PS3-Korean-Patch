# 스플린터 셀 트릴로지 한글 패처 빌드 (PowerShell)
#  1) 패처 exe 컴파일  2) 원본과 한글화된 게임 파일을 비교해 패치 데이터(차이만) 생성  3) 패치 데이터를 넣어 최종 exe 2종 컴파일
# 사용법: build.bat 더블클릭, 또는
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Patched "E:\...\(한글화된 게임 폴더)" -Original "D:\원본.iso"
param(
    [string]$Patched = "E:\Tom Clancys Splinter Cell Trilogy (USA Korea) (EnFrDeEsIt)\Tom Clancy's Splinter Cell Trilogy (USA, Korea) (En,Fr,De,Es,It)",
    [string]$Original = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$out = Join-Path $root "build"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe 를 찾을 수 없습니다. .NET Framework 4.x 가 필요합니다." }

function Compile([string]$exe, [string[]]$extra, [string]$define = "") {
    $res = @("elfpatch.bin", "subs.txt.gz", "videosha.txt", "NanumGothicBold.ttf.gz", "rpcs3_patch.yml", "rpcs3_hashes.txt", "sctkosub.exe") + $extra
    $cargs = @("/nologo", "/codepage:65001", "/target:winexe", "/optimize+", "/out:$exe",
        "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:System.Core.dll", "/r:System.IO.Compression.dll")
    if ($define -ne "") { $cargs += "/define:$define" }
    foreach ($r in $res) { $cargs += "/resource:$r,$(Split-Path -Leaf $r)" }
    $cargs += "SCTKoPatcher.cs"
    Push-Location $src
    try { & $csc @cargs; if ($LASTEXITCODE -ne 0) { throw "컴파일 실패" } } finally { Pop-Location }
}

Write-Host "[1/3] 패처 컴파일..." -ForegroundColor Cyan
$stage = Join-Path $out "stage.exe"
Compile $stage @()

if (-not (Test-Path -LiteralPath $Patched)) {
    $Patched = Read-Host "한글화된 게임 폴더 경로 (PS3_GAME 이 들어 있는 폴더)"
}
if ($Original -eq "") {
    # 한글화 폴더 옆에 같은 이름의 원본 ISO가 있으면 그것과 비교한다
    $iso = $Patched.TrimEnd('\') + ".iso"
    if (Test-Path -LiteralPath $iso) { $Original = $iso; Write-Host "원본 ISO: $iso" }
}
if ($Original -eq "") {
    $Original = Read-Host "원본 ISO 또는 원본 게임 폴더 경로 (없으면 Enter: 한글화 폴더의 .orig 백업과 비교)"
}
$pack = Join-Path $out "data.pack"
$log = Join-Path $out "make-pack.log"
Write-Host "[2/3] 패치 데이터 생성 (원본과 비교 중, 몇 분 걸릴 수 있습니다)..." -ForegroundColor Cyan
$a = @("--make-pack", "--patched", "`"$Patched`"", "--out", "`"$pack`"", "--log", "`"$log`"")
if ($Original -ne "") { $a += @("--orig", "`"$Original`"") }
$p = Start-Process -FilePath $stage -ArgumentList $a -Wait -PassThru
Get-Content -LiteralPath $log -Encoding UTF8 | Write-Host
if ($p.ExitCode -ne 0) { throw "패치 데이터 생성 실패 (make-pack.log 확인)" }
if ((Get-Item -LiteralPath $pack).Length -lt 1MB) { throw "패치 데이터가 너무 작습니다. 비교한 폴더가 한글화된 게임 폴더가 맞는지 확인하세요: $Patched" }

Write-Host "[3/3] 최종 패처 컴파일..." -ForegroundColor Cyan
Copy-Item -LiteralPath $pack -Destination (Join-Path $src "data.pack") -Force
try {
    Compile (Join-Path $out "SCT_KO_Patcher.exe") @("data.pack")
} finally { Remove-Item (Join-Path $src "data.pack") -ErrorAction SilentlyContinue }
Remove-Item $stage -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "완료:" -ForegroundColor Green
Write-Host "  $(Join-Path $out 'SCT_KO_Patcher.exe')   (실행 파일 패치 방식: RPCS3로 실행 파일 복호화 필요)"
Write-Host "포함된 파일 목록: $pack.txt"
