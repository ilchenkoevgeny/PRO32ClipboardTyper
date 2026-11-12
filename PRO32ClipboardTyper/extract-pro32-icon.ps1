param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$icon = $null
$stream = $null
$tempPath = $null

try {
    $pro32Path = Join-Path $env:LOCALAPPDATA 'Programs\pro32connect.dashboard.electron\PRO32 Connect Dashboard.exe'

    if (-not (Test-Path -LiteralPath $pro32Path -PathType Leaf)) {
        Write-Host 'PRO32 Dashboard not installed in the default location.'
        exit 2
    }

    Add-Type -AssemblyName System.Drawing
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($pro32Path)

    if ($null -eq $icon) {
        throw 'Unable to extract the PRO32 icon.'
    }

    $directory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    # Write to a temporary file first so a failed extraction cannot leave
    # an incomplete icon for the compiler.
    $tempPath = Join-Path $directory ([Guid]::NewGuid().ToString('N') + '.tmp')
    $stream = [System.IO.File]::Open(
        $tempPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)

    $icon.Save($stream)
    $stream.Dispose()
    $stream = $null

    Move-Item -LiteralPath $tempPath -Destination $OutputPath -Force
    $tempPath = $null
    exit 0
}
catch {
    Write-Host ('PRO32 icon extraction failed: ' + $_.Exception.Message)
    exit 1
}
finally {
    if ($null -ne $stream) {
        $stream.Dispose()
    }

    if ($null -ne $icon) {
        $icon.Dispose()
    }

    if ($null -ne $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }
}
