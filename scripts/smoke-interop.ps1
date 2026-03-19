[CmdletBinding()]
param(
    [string]$MappingDirectory,
    [string]$ClientId = "sample-client",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDirectory "..")).Path
$serverProject = Join-Path $repoRoot "csharp/TigaIpc.Server/TigaIpc.Server.csproj"
$nodeDirectory = Join-Path $repoRoot "nodejs/mmap-napi"
$nodeExample = Join-Path $nodeDirectory "examples/tiga_invoke.js"

if ([string]::IsNullOrWhiteSpace($MappingDirectory)) {
    $MappingDirectory = Join-Path $env:TEMP ("tiga-ipc-smoke-" + [Guid]::NewGuid().ToString("N"))
}

$MappingDirectory = [System.IO.Path]::GetFullPath($MappingDirectory)
New-Item -ItemType Directory -Path $MappingDirectory -Force | Out-Null

$serverOut = Join-Path $MappingDirectory "server.out.log"
$serverErr = Join-Path $MappingDirectory "server.err.log"

Write-Host "Repo Root        : $repoRoot"
Write-Host "Mapping Directory: $MappingDirectory"
Write-Host "Client Id        : $ClientId"
Write-Host "Configuration    : $Configuration"
Write-Host "Skip Build       : $($SkipBuild.IsPresent)"

Push-Location $repoRoot

$originalMappingDir = $env:TIGA_IPC_DIR
$originalClientId = $env:TIGA_IPC_CLIENT_ID
$server = $null

try {
    if (-not $SkipBuild) {
        Write-Host ""
        Write-Host "== Building C# server =="
        & dotnet build $serverProject -c $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE."
        }

        Write-Host ""
        Write-Host "== Building Node addon =="
        Push-Location $nodeDirectory
        try {
            & cargo build --release
            if ($LASTEXITCODE -ne 0) {
                throw "cargo build --release failed with exit code $LASTEXITCODE."
            }

            Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
        }
        finally {
            Pop-Location
        }
    }

    $env:TIGA_IPC_DIR = $MappingDirectory
    $env:TIGA_IPC_CLIENT_ID = $ClientId

    Write-Host ""
    Write-Host "== Starting C# server =="
    $server = Start-Process dotnet `
        -ArgumentList "run", "--project", $serverProject, "-c", $Configuration, "--no-build" `
        -WorkingDirectory $repoRoot `
        -PassThru `
        -RedirectStandardOutput $serverOut `
        -RedirectStandardError $serverErr

    $ready = $false
    for ($i = 0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 200
        if ($server.HasExited) {
            break
        }

        if ((Test-Path $serverOut) -and ((Get-Content $serverOut -Raw) -match "Server ready\. Press Ctrl\+C to exit\.")) {
            $ready = $true
            break
        }
    }

    if (-not $ready) {
        $stdout = if (Test-Path $serverOut) { Get-Content $serverOut -Raw } else { "" }
        $stderr = if (Test-Path $serverErr) { Get-Content $serverErr -Raw } else { "" }
        throw "Server did not become ready.`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
    }

    Write-Host ""
    Write-Host "== Invoking Node example =="
    Push-Location $nodeDirectory
    try {
        & node $nodeExample
        if ($LASTEXITCODE -ne 0) {
            throw "Node example failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    Write-Host ""
    Write-Host "Smoke interop completed successfully."
    Write-Host "Server stdout: $serverOut"
    if (Test-Path $serverErr) {
        Write-Host "Server stderr: $serverErr"
    }
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }

    if ($null -eq $originalMappingDir) {
        Remove-Item Env:TIGA_IPC_DIR -ErrorAction SilentlyContinue
    } else {
        $env:TIGA_IPC_DIR = $originalMappingDir
    }

    if ($null -eq $originalClientId) {
        Remove-Item Env:TIGA_IPC_CLIENT_ID -ErrorAction SilentlyContinue
    } else {
        $env:TIGA_IPC_CLIENT_ID = $originalClientId
    }

    Pop-Location
}
