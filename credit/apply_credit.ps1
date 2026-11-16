param([string]$Game, [string]$Rpcs3 = 'D:\D-games\rpcs3-v0.0.43-20313-26da24b3_win64_msvc', [switch]$Restore)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $here 'Credit.cs'), [Text.Encoding]::UTF8))

function Find-UsrDir([string]$p) {
    if (-not $p -or -not (Test-Path -LiteralPath $p)) { return $null }
    $c = Join-Path $p 'PS3_GAME\USRDIR'
    if (Test-Path -LiteralPath $c) { return $c }
    if (Test-Path -LiteralPath (Join-Path $p 'Data_SC')) { return $p }
    foreach ($d in Get-ChildItem -LiteralPath $p -Directory) {
        $c = Join-Path $d.FullName 'PS3_GAME\USRDIR'
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

$roots = @()
if ($Game) { $u = Find-UsrDir $Game; if ($u) { $roots += $u } }
else {
    $p = $here
    for ($i = 0; $i -lt 3 -and $p; $i++) { $u = Find-UsrDir $p; if ($u) { $roots += $u; break }; $p = Split-Path -Parent $p }
}
if ($Rpcs3) {
    $h = Join-Path $Rpcs3 'dev_hdd0\game\BLUS30761DATA\USRDIR'
    if (Test-Path -LiteralPath $h) { $roots += $h }
}
if ($roots.Count -eq 0) { Write-Host '게임 폴더를 찾지 못했습니다. -Game <폴더> 로 지정하세요.'; exit 1 }

$targets = @(
    @('Data_COMMON\textures\bg_green.gtf', 'collection'),
    @('Data_SC\System\LoadingScreens\int\avi169.dds', 'sc1_169'),
    @('Data_SC\System\LoadingScreens\int\avi43.dds', 'sc1_43'),
    @('Data_SCPT\System\LoadingScreens\English\avi169.dds', 'sc2_169'),
    @('Data_SCPT\System\LoadingScreens\English\avi43.dds', 'sc2_43'),
    @('Data_SCCT\Magma\DataPC\Textures\GenLoadings\avi169.dds', 'sc3_169'),
    @('Data_SCCT\Magma\DataPC\Textures\GenLoadings\avi43.dds', 'sc3_43')
)
$log = @()
foreach ($r in $roots) {
    $log += "[$r]"
    foreach ($t in $targets) {
        $f = Join-Path $r $t[0]
        try {
            if ($Restore) { $res = [SctCredit]::Restore($f) }
            else { $res = [SctCredit]::Apply($f, (Join-Path $here ('ov\' + $t[1] + '.ovl'))) }
        } catch { $res = '오류: ' + $_.Exception.Message }
        $log += ('  {0,-58} {1}' -f $t[0], $res)
    }
}
$log | ForEach-Object { Write-Host $_ }
[IO.File]::WriteAllLines((Join-Path $here 'credit_log.txt'), $log, [Text.Encoding]::UTF8)
