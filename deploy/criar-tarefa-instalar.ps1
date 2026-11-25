<#
  Cria (uma vez) a tarefa agendada "SalaVivaInstalar", que o botão "Instalar agora" da consola usa.
  Corre com uma conta de administrador do domínio (abre uma janela a pedir a palavra-passe; o Windows guarda-a de forma segura).
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
Write-Host "Vai abrir uma janela a pedir a palavra-passe dessa conta." -ForegroundColor Cyan
$cred = Get-Credential $Conta
if (-not $cred) { throw 'Cancelado.' }
$pw = $cred.GetNetworkCredential().Password
$tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`""
$ErrorActionPreference = 'Continue'   # o aviso "/ST é anterior" vai para stderr e não é erro
schtasks.exe /create /tn SalaVivaInstalar /ru $cred.UserName /rp $pw /sc once /st 00:00 /rl HIGHEST /tr $tr /f 2>&1 | Where-Object { "$_" -notmatch '/ST' } | ForEach-Object { "$_" }
$pw = $null
if ($LASTEXITCODE -ne 0) { throw "Não foi possível criar a tarefa." }
Write-Host ''
Write-Host 'Tarefa criada. Na consola: menu do teu nome > Sistema e instalação > Instalar agora.' -ForegroundColor Green
