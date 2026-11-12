param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$pro32Path = Join-Path $env:LOCALAPPDATA 'Programs\pro32connect.dashboard.electron\PRO32 Connect Dashboard.exe'

Add-Type -AssemblyName System.Drawing

if (Test-Path -LiteralPath $pro32Path) {
    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($pro32Path)
}
else {
    $icon = [System.Drawing.SystemIcons]::Application
}

if ($null -eq $icon) {
    throw 'Не удалось получить иконку приложения.'
}

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$stream = [System.IO.File]::Open(
    $OutputPath,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)

try {
    $icon.Save($stream)
}
finally {
    $stream.Dispose()

    if ((Test-Path -LiteralPath $pro32Path) -and $icon -is [System.IDisposable]) {
        $icon.Dispose()
    }
}
