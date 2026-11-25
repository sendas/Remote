<#
  Cria (uma vez) a tarefa agendada "SalaVivaInstalar", que o botão "Instalar agora" da consola usa.
  Corre com uma conta de administrador do domínio (pede a palavra-passe; o Windows guarda-a de forma segura).
  A consola não guarda nenhuma palavra-passe: só pede ao Windows para correr esta tarefa.

    .\criar-tarefa-instalar.ps1                          # usa a conta com que tens sessão iniciada
    .\criar-tarefa-instalar.ps1 -Conta 'QUEIROZ\outra.conta'
#>
param(
    [string]$Conta = "$env:USERDOMAIN\$env:USERNAME",
    [string]$Pasta = 'C:\Deploy\SalaDeAula'
)
$ErrorActionPreference = 'Stop'
$script = Join-Path $Pasta 'instalar-agora.ps1'
if (-not (Test-Path $script)) { throw "Não encontro $script. Corre primeiro .\preparar-partilha.ps1 (versão nova)." }

Write-Host "A tarefa vai correr como: $Conta" -ForegroundColor Cyan
Write-Host "Escreve a palavra-passe dessa conta quando for pedida (não aparece no ecrã)." -ForegroundColor Cyan
$tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`""
schtasks.exe /create /tn SalaVivaInstalar /ru $Conta /rp * /sc once /st 00:00 /rl HIGHEST /tr $tr /f
if ($LASTEXITCODE -ne 0) { throw "Não foi possível criar a tarefa." }
Write-Host ''
Write-Host 'Tarefa criada. Na consola: menu do teu nome > Sistema e instalação > Instalar agora.' -ForegroundColor Green
