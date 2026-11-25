# Compila servidor e agente para Windows (x64). Requer o .NET 8 SDK.
#   .\build.ps1            -> cria .\publish\server e .\publish\agent
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$extra = @()
if ($env:GITHUB_SHA) { $extra += "-p:SourceRevisionId=$($env:GITHUB_SHA.Substring(0,7))" }
dotnet publish "$root\server\ClassroomServer.csproj" -c Release -r win-x64 --self-contained @extra `
    -p:PublishSingleFile=true -p:DebugType=none -o "$out\server"
dotnet publish "$root\agent\ClassroomAgent.csproj" -c Release -f net8.0-windows -o "$out\agent"
dotnet publish "$root\agent\ClassroomAgent.csproj" -c Release -f net462 -o "$out\agent\leve"

Copy-Item "$root\deploy\install-server.ps1" "$out\server\"
Copy-Item "$root\deploy\install-agent.ps1", "$root\deploy\uninstall-agent.ps1", "$root\deploy\install-remote.ps1", "$root\deploy\preparar-partilha.ps1", "$root\deploy\instalar-agora.ps1", "$root\deploy\criar-gpo.ps1", "$root\deploy\criar-tarefa-instalar.ps1" "$out\agent\"
Write-Host "`nPronto: $out" -ForegroundColor Green
