# Compila servidor e agente para Windows (x64). Requer o .NET 8 SDK.
#   .\build.ps1            -> cria .\publish\server e .\publish\agent
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish "$root\server\ClassroomServer.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -o "$out\server"
dotnet publish "$root\agent\ClassroomAgent.csproj" -c Release -o "$out\agent"

Copy-Item "$root\deploy\install-server.ps1" "$out\server\"
Copy-Item "$root\deploy\install-agent.ps1", "$root\deploy\uninstall-agent.ps1" "$out\agent\"
Write-Host "`nPronto: $out" -ForegroundColor Green
