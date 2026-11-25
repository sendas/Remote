#!/usr/bin/env bash
# Compila servidor e agente para Windows (x64) a partir de Linux/macOS. Requer o .NET 8 SDK.
set -euo pipefail
root="$(cd "$(dirname "$0")" && pwd)"
out="$root/publish"
rm -rf "$out"
dotnet publish "$root/server/ClassroomServer.csproj" -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -o "$out/server"
dotnet publish "$root/agent/ClassroomAgent.csproj" -c Release -f net8.0-windows -o "$out/agent"
dotnet publish "$root/agent/ClassroomAgent.csproj" -c Release -f net462 -o "$out/agent/leve"
cp "$root/deploy/install-server.ps1" "$out/server/"
cp "$root/deploy/install-agent.ps1" "$root/deploy/uninstall-agent.ps1" "$root/deploy/install-remote.ps1" "$root/deploy/preparar-partilha.ps1" "$root/deploy/instalar-agora.ps1" "$root/deploy/criar-gpo.ps1" "$root/deploy/criar-tarefa-instalar.ps1" "$out/agent/"
echo "Pronto: $out"
