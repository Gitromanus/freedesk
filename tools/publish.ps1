param(
    [string]$OutputDir = "$PSScriptRoot\..\agent\publish"
)

# Две сборки из одного проекта:
#   FreeDesk.exe        — WinExe (GUI-подсистема): двойной клик открывает только окно,
#                        без терминала; консольные флаги из терминала работают (AttachConsole).
#   freedesk-agent.exe  — консольная сборка для скриптов и тестов.
$ErrorActionPreference = 'Stop'

$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\agent\FreeDesk.Agent\FreeDesk.Agent.csproj'))
$output = [IO.Path]::GetFullPath($OutputDir)
$temp = Join-Path ([IO.Path]::GetTempPath()) ("freedesk-publish-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

$common = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:PublishTrimmed=false'
)

New-Item -ItemType Directory -Force -Path $output | Out-Null

try {
    Write-Host 'publishing FreeDesk.exe (GUI, no console) ...'
    dotnet publish $project @common -p:OutputType=WinExe -p:AssemblyName=FreeDesk -o (Join-Path $temp 'gui')
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed' }
    Copy-Item (Join-Path $temp 'gui\FreeDesk.exe') $output -Force

    Write-Host 'publishing freedesk-agent.exe (console) ...'
    dotnet publish $project @common -o (Join-Path $temp 'console')
    if ($LASTEXITCODE -ne 0) { throw 'console publish failed' }
    Copy-Item (Join-Path $temp 'console\freedesk-agent.exe') $output -Force

    # openh264.dll лежит рядом с exe, а не внутри бандла
    foreach ($dir in @('gui', 'console')) {
        $dll = Join-Path $temp "$dir\openh264.dll"
        if (Test-Path $dll) { Copy-Item $dll $output -Force }
        else { Write-Warning "openh264.dll не попал в publish ($dir)" }
    }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Get-ChildItem $output -Filter *.exe | Format-Table Name, @{L='SizeMB';E={ '{0:F1}' -f ($_.Length / 1MB) }}
