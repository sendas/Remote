# Compila servidor e agente para Windows (x64). Requer o .NET 8 SDK.
#   .\build.ps1            -> cria .\publish\server e .\publish\agent
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'publish'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$extra = @()
if ($env:GITHUB_SHA) { $extra += "-p:SourceRevisionId=$($env:GITHUB_SHA.Substring(0,7))" }
# cada compilação no GitHub tem um número novo (2.1.37, 2.1.38, ...), para se ver logo que mudou
if ($env:GITHUB_RUN_NUMBER) { $extra += "-p:Version=2.1.$($env:GITHUB_RUN_NUMBER)" }
dotnet publish "$root\server\ClassroomServer.csproj" -c Release -r win-x64 --self-contained @extra `
    -p:PublishSingleFile=true -p:DebugType=none -o "$out\server"
dotnet publish "$root\agent\ClassroomAgent.csproj" -c Release -f net8.0-windows @extra -o "$out\agent"
dotnet publish "$root\agent\ClassroomAgent.csproj" -c Release -f net462 @extra -o "$out\agent\leve"

Copy-Item "$root\deploy\install-server.ps1" "$out\server\"
Copy-Item "$root\deploy\install-agent.ps1", "$root\deploy\uninstall-agent.ps1", "$root\deploy\install-remote.ps1", "$root\deploy\preparar-partilha.ps1", "$root\deploy\instalar-agora.ps1", "$root\deploy\criar-gpo.ps1", "$root\deploy\criar-tarefa-instalar.ps1", "$root\deploy\instalar-sem-reiniciar.ps1" "$out\agent\"
Write-Host "`nPronto: $out" -ForegroundColor Green
