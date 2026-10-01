# B-PCG 개발 환경 설치 스크립트 (Windows). macOS, Linux 는 scripts/setup.sh 를 씁니다.
#
# 저장소 폴더의 PowerShell 에서:
#   powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Help
# 여러 번 실행해도 됩니다. 이미 있는 것은 건너뛰고, 이미 설치한 선택 묶음은 지우지 않습니다.
#
# Windows PowerShell 5.1 과 PowerShell 7 에서 모두 돌도록 썼습니다.
# (삼항 연산자, ??, &&, ||, Join-Path 의 세 번째 인자처럼 7 에만 있는 문법을 쓰지 않습니다.)
# 이 파일은 UTF-8 (BOM 있음), CRLF 로 저장합니다. BOM 이 없으면 5.1 이 한글을 깨뜨립니다.
[CmdletBinding()]
param(
    [switch]$Analysis,
    [switch]$Gpl,
    [switch]$Notebook,
    [switch]$Godot,
    [switch]$Data,
    [switch]$All,
    [switch]$Check,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # 진행 막대를 끄면 Invoke-WebRequest, Expand-Archive 가 훨씬 빨라집니다.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$UvVersion = '0.12.17'
$GodotVersion = '4.7.2'
$GodotTag = "$GodotVersion-stable"
$GodotUrlBase = "https://github.com/godotengine/godot/releases/download/$GodotTag"
# 아래 값은 위 주소의 SHA512-SUMS.txt 에서 가져왔습니다. Godot 버전을 바꾸면 함께 바꿉니다.
$ShaWin64 = '83decd58fdf67b9d657958a1ae6bf1929c20785315a81effe245874cdc57acb709bf868e00778a96984338c1b29dafdb453c6847747694621c6ecf5da2259993'
$ShaWinArm64 = '683f8dd9fb087db79dfbbc52d5b2209df98218a4fef0d10d8478ec2230ae8db6032a36929677479b9f9c5a6aa0c51ee359d7e57eb5abbcb6cef4998526dec5a6'

$Root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$ToolsDir = Join-Path $Root '.tools'
$GodotDir = Join-Path $ToolsDir 'godot'
$Started = Get-Date

function Say([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Warn([string]$Message) { Write-Host "[주의] $Message" -ForegroundColor Yellow }
function Leave([int]$Code) {
    Pop-Location
    exit $Code
}
function Die([string]$Message) {
    Write-Host "[실패] $Message" -ForegroundColor Red
    Leave 1
}
function Show-Usage {
    Write-Host @'
B-PCG 개발 환경 설치 (Windows)

쓰는 법 (저장소 폴더에서): powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 [옵션...]

  (옵션 없음)  uv, 파이썬 3.13, 기본 패키지 묶음(dev, data, mesh)을 설치합니다.
  -Analysis    비교·분석 도구(pyflwdir, landlab)도 설치합니다.
  -Gpl         GPL 비교 도구(fastscapelib, TopoToolbox)도 설치합니다.
  -Notebook    JupyterLab 도 설치합니다.
  -Godot       Godot 4.7.2 를 .tools\godot\ 에 받습니다 (약 85 MB).
  -Data        파일럿 데이터를 받습니다 (약 4.5 GB, 오래 걸립니다. 끊겨도 다시 실행하면 이어 받습니다).
  -All         -Analysis -Gpl -Notebook -Godot 를 한꺼번에 (데이터는 빼고).
  -Check       아무것도 설치하지 않고 환경 점검(tools\doctor.py)만 합니다.
  -Help        이 도움말을 봅니다.

여러 번 실행해도 됩니다. 이미 있는 것은 건너뛰고, 이미 설치한 선택 묶음은 지우지 않습니다.
'@
}
function Step-Time([datetime]$Since) {
    $s = [int]((Get-Date) - $Since).TotalSeconds
    Write-Host "    ($s 초 걸림)"
}
function Test-Tool([string]$Name) {
    return [bool](Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue)
}

if ($Help) {
    Show-Usage
    exit 0
}
if ($All) { $Analysis = $true; $Gpl = $true; $Notebook = $true; $Godot = $true }

Push-Location -LiteralPath $Root

# ---------------------------------------------------------------- 1. 운영체제와 CPU
if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    Die 'Windows 용 스크립트입니다. macOS, Linux 에서는 ./scripts/setup.sh 를 쓰세요.'
}
# 32비트·에뮬레이션 PowerShell 에서도 실제 CPU 를 알도록 레지스트리 값을 먼저 봅니다.
$NativeArch = $null
try {
    $NativeArch = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name PROCESSOR_ARCHITECTURE -ErrorAction Stop).PROCESSOR_ARCHITECTURE
} catch { }
if (-not $NativeArch) {
    $NativeArch = $env:PROCESSOR_ARCHITECTURE
    if ($env:PROCESSOR_ARCHITEW6432) { $NativeArch = $env:PROCESSOR_ARCHITEW6432 }
}
switch ($NativeArch.ToUpper()) {
    'AMD64' { $Arch = 'x86_64' }
    'ARM64' { $Arch = 'arm64' }
    default { Die "지원하지 않는 CPU 입니다: $NativeArch (x64, ARM64 만 지원)" }
}
$WinVer = [System.Environment]::OSVersion.Version
Say "운영체제: Windows $($WinVer.Major).$($WinVer.Minor) build $($WinVer.Build) ($Arch), PowerShell $($PSVersionTable.PSVersion)"

# ---------------------------------------------------------------- uv 찾기
# 공식 설치 스크립트와 같은 순서로 정합니다: UV_INSTALL_DIR, XDG_BIN_HOME, 그다음 %USERPROFILE%\.local\bin
$UvBin = Join-Path $env:USERPROFILE '.local\bin'
if ($env:XDG_BIN_HOME) { $UvBin = $env:XDG_BIN_HOME }
if ($env:UV_INSTALL_DIR) { $UvBin = $env:UV_INSTALL_DIR }
$UvOnUserPath = $true
function Find-Uv {
    if (Test-Tool 'uv') { return $true }
    if (Test-Path -LiteralPath (Join-Path $UvBin 'uv.exe')) {
        $env:Path = "$UvBin;$env:Path"   # 이번 실행 동안만 PATH 에 더합니다.
        $script:UvOnUserPath = $false
        return $true
    }
    return $false
}

# ---------------------------------------------------------------- -Check: 점검만
if ($Check) {
    Say '환경 점검만 합니다 (아무것도 설치하지 않음).'
    if (Find-Uv) {
        if (Test-Path -LiteralPath (Join-Path $Root '.venv')) {
            & uv run --no-sync python tools/doctor.py
        } else {
            # .venv 가 없을 때 uv run 은 빈 .venv 를 만들어 버리므로, 프로젝트 밖 파이썬으로 점검합니다.
            Warn '.venv 가 아직 없습니다. 설치하려면: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1'
            & uv run --no-project python tools/doctor.py
        }
        Leave $LASTEXITCODE
    }
    if (Test-Tool 'py') {
        # uv 가 없어도 doctor 는 표준 라이브러리만으로 돌아 무엇이 빠졌는지 알려 줍니다.
        Warn 'uv 가 없어 py 런처의 파이썬으로 점검합니다. 설치하려면: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1'
        & py -3 tools/doctor.py
        Leave $LASTEXITCODE
    }
    Die 'uv 가 없습니다. 먼저 설치하세요: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1'
}

# ARM64 Windows 에는 pyogrio, shapely, pyarrow 의 ARM64 wheel 이 없습니다.
# 그래서 x64 파이썬을 받아 에뮬레이션으로 돌립니다 (Windows 11 ARM 은 x64 프로그램을 그대로 돌립니다).
$PythonRequest = $null
if ($Arch -eq 'arm64') {
    $PyVer = (Get-Content -LiteralPath (Join-Path $Root '.python-version') -Raw).Trim()
    $PythonRequest = "cpython-$PyVer-windows-x86_64-none"
    Warn "ARM64 PC 입니다. 일부 패키지의 ARM64 wheel 이 없어 x64 파이썬($PythonRequest)을 에뮬레이션으로 씁니다."
}

# 긴 경로(260자 넘음)가 꺼져 있으면 깊은 패키지 폴더(jupyterlab 등)에서 파이썬이 파일을 못 엽니다.
$LongPaths = 0
try {
    $LongPaths = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -ErrorAction Stop).LongPathsEnabled
} catch { }
if ($LongPaths -ne 1) {
    Warn '긴 경로(260자 넘음) 지원이 꺼져 있습니다. 저장소를 C:\src\b-pcg 처럼 짧은 곳에 두거나,'
    Write-Host '       관리자 PowerShell 에서 한 번 실행하세요 (재부팅 필요할 수 있음):'
    Write-Host '       New-ItemProperty -Path HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem -Name LongPathsEnabled -Value 1 -PropertyType DWORD -Force'
}

# ---------------------------------------------------------------- 2. 시스템 도구 (설치는 안내만)
if (-not (Test-Tool 'git')) {
    Die 'git 이 없습니다. 먼저 설치하세요: winget install --id Git.Git -e --source winget   (설치 뒤 새 터미널을 여세요)'
}
# PowerShell 5.1 에서 curl 은 Invoke-WebRequest 의 별명이라, 진짜 curl 인 curl.exe 를 따로 찾습니다.
if (-not (Test-Tool 'curl.exe')) {
    Die 'curl.exe 가 없습니다. Windows 10 1803 이상에는 기본으로 있습니다. Windows 를 업데이트하거나 winget install --id cURL.cURL -e 로 설치하세요.'
}
Say "$(& git --version), curl.exe 확인"

# ---------------------------------------------------------------- 3. uv (파이썬과 패키지 관리자)
$UvInstalledNow = $false
if (-not (Find-Uv)) {
    $t = Get-Date
    Say "uv $UvVersion 설치 (공식 설치 스크립트, $UvBin). 사용자 PATH 는 건드리지 않습니다."
    # 설치 스크립트는 새 Windows PowerShell 프로세스에서 돌립니다. TLS 1.2 를 켜야 오래된 5.1 에서도 받습니다.
    $env:UV_NO_MODIFY_PATH = '1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "`$ProgressPreference = 'SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072; irm https://astral.sh/uv/$UvVersion/install.ps1 | iex"
    $rc = $LASTEXITCODE
    Remove-Item Env:\UV_NO_MODIFY_PATH -ErrorAction SilentlyContinue
    if ($rc -ne 0) { Die 'uv 설치에 실패했습니다. 인터넷 연결을 확인하고 다시 실행하세요.' }
    if (-not (Find-Uv)) { Die "uv 를 설치했지만 $UvBin 에서 찾지 못했습니다. 새 터미널을 열고 다시 실행하세요." }
    $UvInstalledNow = $true
    Step-Time $t
}
$UvHave = ((& uv --version) -split ' ')[1]
$UvTooOld = $false
try { $UvTooOld = ([version]($UvHave -replace '[^0-9.].*$', '')) -lt [version]$UvVersion } catch { }
if ($UvTooOld) {
    Die "uv $UvHave 는 너무 오래됐습니다. $UvVersion 이상이 필요합니다. 올리기: uv self update   (winget 으로 깔았다면 winget upgrade astral-sh.uv)"
}
Say "uv $UvHave ($((Get-Command uv -CommandType Application | Select-Object -First 1).Source))"

# ---------------------------------------------------------------- 4. 파이썬 + 패키지
# 저장소 폴더를 옮기거나 이름을 바꾸면 .venv 안의 경로(편집 설치 .pth, 실행 파일)가 옛 위치를 가리킵니다.
# 그런 .venv 는 다시 만드는 편이 확실합니다. .venv 는 uv 가 언제든 다시 만들 수 있습니다.
function Test-VenvStale {
    $sp = Join-Path $Root '.venv\Lib\site-packages'
    if (-not (Test-Path -LiteralPath $sp)) { return $false }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    foreach ($pth in Get-ChildItem -LiteralPath $sp -Filter '*.pth' -File -ErrorAction SilentlyContinue) {
        foreach ($line in Get-Content -LiteralPath $pth.FullName -Encoding UTF8 -ErrorAction SilentlyContinue) {
            $line = $line.Trim()
            if ($line -match '^[A-Za-z]:\\' -or $line -match '^\\\\') {
                if (-not (Test-Path -LiteralPath $line)) { return $true }
                $full = [System.IO.Path]::GetFullPath($line).TrimEnd('\') + '\'
                if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
            }
        }
    }
    return $false
}
if (Test-VenvStale) {
    Warn '저장소 폴더가 옮겨져 .venv 가 옛 경로를 가리킵니다. .venv 를 지우고 새로 만듭니다.'
    Remove-Item -LiteralPath (Join-Path $Root '.venv') -Recurse -Force
}

$SyncArgs = @('sync', '--locked', '--inexact')
$GroupNote = '기본(dev, data, mesh)'
if ($Analysis) { $SyncArgs += @('--group', 'analysis'); $GroupNote += ' + analysis' }
if ($Gpl) { $SyncArgs += @('--group', 'gpl'); $GroupNote += ' + gpl' }
if ($Notebook) { $SyncArgs += @('--group', 'notebook'); $GroupNote += ' + notebook' }
if ($PythonRequest) { $SyncArgs += @('--python', $PythonRequest) }

# 파이썬 버전은 .python-version, 패키지 버전은 uv.lock 이 정합니다. 파이썬도 uv 가 직접 받습니다.
$t = Get-Date
Say "파이썬과 패키지 설치: $GroupNote"
Say "  uv $($SyncArgs -join ' ')"
& uv @SyncArgs
if ($LASTEXITCODE -ne 0) {
    Die "uv sync 가 실패했습니다. 위 오류를 확인하세요. 'lockfile needs to be updated' 라면 pyproject.toml 을 바꾼 사람이 uv lock 으로 uv.lock 도 갱신해 올려야 합니다."
}
Step-Time $t

# ---------------------------------------------------------------- 5. Godot (선택)
# Godot 은 언제나 임시 HOME, APPDATA 로 실행합니다. 그래야 사용자의 Godot 편집기 설정을 건드리지 않습니다.
# 창 모드 exe 는 콘솔에 아무것도 찍지 않으므로 옆의 _console.exe 로 버전을 봅니다.
function Get-GodotVersion([string]$Exe) {
    if (-not $Exe) { return $null }
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { return $null }
    if ($Exe -like '*.exe' -and $Exe -notlike '*_console.exe') {
        $consoleExe = $Exe -replace '\.exe$', '_console.exe'
        if (Test-Path -LiteralPath $consoleExe -PathType Leaf) { $Exe = $consoleExe }
    }
    $tmpHome = Join-Path ([System.IO.Path]::GetTempPath()) ('bpcg-godot-home-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmpHome | Out-Null
    $saved = @{ HOME = $env:HOME; APPDATA = $env:APPDATA; LOCALAPPDATA = $env:LOCALAPPDATA }
    $oldPref = $ErrorActionPreference
    $out = $null
    try {
        $env:HOME = $tmpHome
        $env:APPDATA = Join-Path $tmpHome 'Roaming'
        $env:LOCALAPPDATA = Join-Path $tmpHome 'Local'
        $ErrorActionPreference = 'Continue'   # 5.1 에서 stderr 출력이 오류로 바뀌어 멈추지 않게
        $out = & $Exe --headless --version 2>$null
    } catch {
        $out = $null
    } finally {
        $ErrorActionPreference = $oldPref
        foreach ($k in $saved.Keys) {
            # 값이 $null 이면 변수를 지웁니다 (원래 없던 HOME 등).
            [System.Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process')
        }
        Remove-Item -LiteralPath $tmpHome -Recurse -Force -ErrorAction SilentlyContinue
    }
    $line = $out | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match '^\d+\.\d+' } | Select-Object -Last 1
    if ($line) { return [string]$line }
    return $null
}
function Find-Godot {
    $cands = @()
    if ($env:GODOT) { $cands += $env:GODOT }
    $cands += (Join-Path $GodotDir "Godot_v${GodotTag}_win64_console.exe")
    $cands += (Join-Path $GodotDir "Godot_v${GodotTag}_windows_arm64_console.exe")
    foreach ($name in @('godot', 'godot4')) {
        $cmd = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd) { $cands += $cmd.Source }
    }
    foreach ($c in $cands) {
        $v = Get-GodotVersion $c
        if (-not $v) { continue }
        if ($v -like "$GodotVersion.stable*") { return $c }
        Warn "Godot $v 이 있지만 $GodotVersion 이 아니라 쓰지 않습니다: $c"
    }
    return $null
}
function Get-Sha512([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash.ToLowerInvariant()
}
function Install-Godot {
    if ($Arch -eq 'arm64') {
        $zip = "Godot_v${GodotTag}_windows_arm64.exe.zip"; $sha = $ShaWinArm64
    } else {
        $zip = "Godot_v${GodotTag}_win64.exe.zip"; $sha = $ShaWin64
    }
    $url = "$GodotUrlBase/$zip"
    $zipPath = Join-Path $ToolsDir $zip
    $part = "$zipPath.part"
    New-Item -ItemType Directory -Force -Path $GodotDir | Out-Null
    if ((Test-Path -LiteralPath $part) -and ((Get-Sha512 $part) -eq $sha)) {
        Say "이미 받아 둔 $zip 을 씁니다."
    } else {
        Say "Godot $GodotVersion 받기: $url"
        # 끊긴 파일이 있으면 이어 받고, 이어받기가 안 되면 처음부터 받습니다.
        & curl.exe -fL --retry 3 --retry-delay 2 --progress-bar -C - -o $part $url
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
            & curl.exe -fL --retry 3 --retry-delay 2 --progress-bar -o $part $url
            if ($LASTEXITCODE -ne 0) {
                Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
                Die 'Godot 내려받기에 실패했습니다. 인터넷 연결을 확인하고 다시 실행하세요.'
            }
        }
    }
    if ((Get-Sha512 $part) -ne $sha) {
        Remove-Item -LiteralPath $part -Force
        Die '받은 Godot 파일의 SHA-512 가 공식 값과 다릅니다. 지웠으니 다시 실행하세요. 계속 다르면 팀에 알려 주세요.'
    }
    Say "SHA-512 확인 완료. 압축을 풉니다: $GodotDir"
    # 5.1 의 Expand-Archive 는 .zip 확장자만 받으므로 이름을 바꾼 뒤, 임시 폴더에 풀고 옮깁니다.
    Move-Item -LiteralPath $part -Destination $zipPath -Force
    $extract = Join-Path $ToolsDir 'godot.extract'
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extract -Force
    foreach ($item in Get-ChildItem -LiteralPath $extract -Force) {
        $dest = Join-Path $GodotDir $item.Name
        if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
        Move-Item -LiteralPath $item.FullName -Destination $dest
    }
    Remove-Item -LiteralPath $extract -Recurse -Force
    Remove-Item -LiteralPath $zipPath -Force
}

$GodotFound = $null
if ($Godot) {
    $t = Get-Date
    $GodotFound = Find-Godot
    if ($GodotFound) {
        Say "Godot $GodotVersion 이 이미 있습니다: $GodotFound"
    } else {
        Install-Godot
        $GodotFound = Find-Godot
        if (-not $GodotFound) { Die "Godot 을 풀었지만 실행되지 않습니다. $GodotDir 을 지우고 다시 실행하세요." }
        Say "Godot 설치 완료: $GodotFound"
    }
    Step-Time $t
}

# ---------------------------------------------------------------- 6. 파일럿 데이터 (선택)
if ($Data) {
    $t = Get-Date
    Say '파일럿 데이터 받기 (약 4.5 GB). 중간에 끊겨도 다시 실행하면 이어 받습니다.'
    & uv run --no-sync python tools/download_pilot.py
    if ($LASTEXITCODE -ne 0) { Die '파일럿 데이터 받기에 실패했습니다. 다시 실행하면 이어 받습니다.' }
    Step-Time $t
}

# ---------------------------------------------------------------- 7. 점검
Say '환경 점검 (tools\doctor.py)'
& uv run --no-sync python tools/doctor.py
$DoctorRc = $LASTEXITCODE
$Total = [int]((Get-Date) - $Started).TotalSeconds

Write-Host ''
if ($DoctorRc -ne 0) {
    Die "점검에서 필수 항목이 실패했습니다. 위 [실패] 줄을 확인하고 고친 뒤 다시 실행하세요. (전체 $Total 초)"
}
Say "설치가 끝났습니다. (전체 $Total 초)"
Write-Host ''
Write-Host '다음 단계'
Write-Host '  - 가상환경을 켤(activate) 필요가 없습니다. 명령 앞에 uv run 을 붙이면 저장소의 .venv 로 돌아갑니다.'
Write-Host '  - 빠른 테스트:      uv run pytest -m "not slow"'
Write-Host '  - 린트와 서식:      uv run ruff check .   그리고   uv run ruff format .'
Write-Host '  - 점검만 다시:      powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Check'
if ($GodotFound) {
    Write-Host "  - Godot:            $GodotFound"
    Write-Host '                      (다른 위치의 Godot 을 쓰려면 환경 변수 GODOT 에 _console.exe 경로를 넣습니다)'
} else {
    Write-Host '  - Godot 4.7.2 받기: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Godot   (엔진 담당은 필수)'
}
if (-not $Data) {
    Write-Host '  - 파일럿 데이터:    powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Data   (약 4.5 GB)'
}
if ($UvInstalledNow -or -not $UvOnUserPath) {
    Write-Host ''
    Write-Host "[중요] uv 가 있는 $UvBin 이 아직 사용자 PATH 에 없습니다. 이번 실행에서만 임시로 더했습니다." -ForegroundColor Yellow
    Write-Host '  새 터미널에서도 uv 를 쓰려면 한 번만 실행한 뒤 터미널을 새로 여세요 (사용자 PATH 에 더합니다):'
    Write-Host "      & `"$UvBin\uv.exe`" tool update-shell"
}
Leave 0
