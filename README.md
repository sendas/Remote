# Sala de Aula — controlo dos computadores da sala

Permite ao professor, a partir de um browser (Mac ou Windows):

- **ver todos os ecrãs** em miniatura, em tempo real, filtrados por sala;
- **abrir um ecrã em grande** e, se quiser, **controlá-lo** (rato e teclado);
- **bloquear / desbloquear** os PCs (ecrã preto com aviso, teclado e rato bloqueados);
- **enviar mensagens** a um, vários ou todos os alunos, com **resposta** do aluno para a consola;
- **partilhar o ecrã do professor** em ecrã inteiro nos PCs escolhidos;
- **abrir um link** no browser dos alunos;
- **terminar sessão, reiniciar ou desligar** os PCs.

Tudo fica registado num **log de auditoria** (quem fez o quê e quando).

---

## Arquitetura (e porque funciona com VLANs e hotspot)

```
 VLAN alunos                      Servidor Windows                VLAN professores / hotspot
┌──────────────┐  wss:8443   ┌──────────────────────────┐  https:8443  ┌────────────────────┐
│ PC aluno     │────────────▶│ ClassroomServer          │◀─────────────│ Browser (Mac/Win)  │
│ ClassroomAgent│ (saída)    │ - consola web            │              │ consola do professor│
│ (serviço)    │             │ - relay de imagens/cmds  │              └────────────────────┘
└──────────────┘             │ - auditoria              │
        ...                  └──────────────────────────┘
```

- **Os PCs dos alunos ligam-se ao servidor** (ligação de saída). O professor também se liga ao servidor.
  Não é preciso tráfego direto entre a VLAN dos alunos e a dos professores, nem descoberta por broadcast
  (que não atravessa VLANs). Só é preciso que **as duas VLANs cheguem ao servidor na porta 8443/TCP**.
- A **partilha de ecrã do professor** é enviada uma vez para o servidor, que a distribui pelos PCs.
  Num hotspot, o professor só faz *upload* de um fluxo, não de 30.
- O servidor guarda a última miniatura de cada PC e descarta imagens antigas quando a rede é lenta,
  por isso um professor em 4G não fica com atraso acumulado.

### Regras de firewall a pedir à equipa de rede

| Origem              | Destino            | Porta     | Para quê                          |
|---------------------|--------------------|-----------|-----------------------------------|
| VLAN alunos         | Servidor           | TCP 8443  | Agentes                           |
| VLAN professores    | Servidor           | TCP 8443  | Consola web                       |
| (opcional) Internet | Servidor (túnel)   | —         | Usar a partir do hotspot (abaixo) |

### Usar a partir do hotspot do telemóvel

No hotspot, o portátil do professor está fora da rede da escola e não chega ao servidor. Há três opções,
da mais simples à que exige mais da equipa de rede:

1. **Tailscale (recomendado para começar)** — instala o Tailscale no servidor e no portátil do professor
   (Mac/Windows). O professor abre `https://<nome-do-servidor-no-tailscale>:8443/`. Não exige abrir portas
   na firewall da escola; só é preciso que o servidor tenha saída para a Internet.
2. **Cloudflare Tunnel** — o `cloudflared` no servidor publica a consola num endereço tipo
   `https://sala.escola.pt` com certificado válido, sem abrir portas. Põe **Cloudflare Access** à frente
   (login com o e-mail institucional) para não deixar a consola exposta só com palavra-passe.
3. **Reencaminhamento de porta** na firewall da escola — funciona, mas expõe o servidor à Internet;
   só com certificado válido e palavras-passe fortes.

Os agentes **continuam a ligar-se pelo endereço interno**; só o professor usa o endereço externo.

**Consumo de dados no hotspot** (estimativa, 30 PCs): modo *Normal* ≈ 0,5–1 GB/hora em miniaturas;
modo *Poupança (hotspot)* (uma miniatura por PC a cada 10 s) ≈ 0,1–0,2 GB/hora. Ecrãs parados não são
reenviados. A partilha do ecrã em modo poupança envia ~3 imagens/s a 1280 px.

---

## Instalação

### 1. Compilar

Num PC com o [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```powershell
.\build.ps1        # Windows
./build.sh         # macOS / Linux
```

Resultado em `publish\server` e `publish\agent` (executáveis autónomos — os PCs **não** precisam de .NET instalado).

### 2. Servidor (Windows Server)

Copia `publish\server` para o servidor e, numa PowerShell como Administrador:

```powershell
.\install-server.ps1            # porta 8443 por omissão
```

O script instala o serviço `ClassroomServer`, abre a porta na firewall do Windows e mostra:

- a **palavra-passe inicial** do utilizador `admin`;
- a **EnrollmentKey** (chave que os agentes têm de apresentar);
- o **CertificatePin** (impressão digital do certificado autoassinado, para os agentes confiarem nele).

Gerir professores (em `C:\ClassroomServer`):

```powershell
.\ClassroomServer.exe add-teacher maria.silva 'PalavraPasseForte' 'SALA12,INFO2'   # só estas salas
.\ClassroomServer.exe add-teacher joao.costa  'OutraPalavraPasse'                   # todas as salas
.\ClassroomServer.exe set-password admin 'NovaPalavraPasse'
.\ClassroomServer.exe list-teachers
Restart-Service ClassroomServer
```

Configuração avançada em `C:\ClassroomServer\data\classroom-server.json` (intervalo das miniaturas,
FPS do ecrã em grande, certificado próprio em `CertificatePath`, etc.). Logs de auditoria em
`C:\ClassroomServer\data\auditoria\AAAA-MM.log`.

> **Windows Server 2008 R2 / 2012:** o HTTPS do Windows (SChannel) nestas versões não tem as cifras que
> os browsers atuais exigem (erro `ERR_SSL_VERSION_OR_CIPHER_MISMATCH`). O servidor deteta isto e usa
> automaticamente um motor HTTPS próprio (BouncyCastle, TLS 1.2/1.3), sem precisar de reiniciar nem mexer
> no registo. Pode forçar-se com `"TlsEngine": "managed"` ou `"windows"` em `classroom-server.json`.

> **Certificado:** por omissão é gerado um autoassinado. O browser mostra um aviso na primeira vez
> (aceita-o, ou instala o certificado nos portáteis dos professores). Se a escola tiver uma CA no
> domínio, emite um certificado para o servidor e indica-o em `CertificatePath`; nesse caso podes deixar
> `CertificatePin` vazio nos agentes.

### 3. Agentes (PCs dos alunos, Windows 10/11)

Manualmente (PowerShell como Administrador, na pasta `publish\agent`):

```powershell
.\install-agent.ps1 -ServerUrl 'wss://SRV-ESCOLA:8443/ws/agent' `
                    -EnrollmentKey '<EnrollmentKey>' -CertificatePin '<CertificatePin>' `
                    -Room 'SALA12'
```

**Em massa por GPO:** copia `publish\agent` para uma partilha (ex.: `\\SRV-ESCOLA\deploy$\agent`, leitura
para "Domain Computers") e cria uma GPO na OU dos PCs dos alunos:
*Configuração do Computador → Políticas → Definições do Windows → Scripts → Arranque → PowerShell*,
com o script `install-agent.ps1` e os parâmetros acima mais `-Source \\SRV-ESCOLA\deploy$\agent`.
O script só copia o executável quando ele muda, por isso serve também para atualizações.

Se não indicares `-Room`, a sala é deduzida do nome do PC (`SALA12-PC05` → `SALA12`).

O agente corre como **serviço Windows (SYSTEM)**, que lança um processo na sessão do aluno.
Um aluno sem privilégios de administrador **não o consegue fechar nem parar o serviço**.
No PC do aluno aparece um ícone na barra de tarefas e, quando o professor abre o ecrã dele em grande,
um aviso vermelho "O professor está a ver o teu ecrã" (desativável com `-HideViewIndicator`).

### 4. Professor

Abre `https://SRV-ESCOLA:8443/` em Chrome, Edge, Firefox ou Safari (Mac ou Windows).

- Clique seleciona um PC; Ctrl/Cmd/Shift + clique seleciona vários; duplo clique abre em grande.
- Em grande, ativa **Controlo remoto** para usar rato e teclado (no Mac, Cmd funciona como Ctrl).
- **Partilhar o meu ecrã** pede ao browser para escolher ecrã/janela e mostra-o nos PCs selecionados.
- O seletor **Rede → Poupança (hotspot)** reduz os dados recebidos.

---

## Segurança e RGPD

- Ver o ecrã de alunos é tratamento de dados pessoais (muitas vezes de menores). Antes de usar:
  informa o **Encarregado de Proteção de Dados** do agrupamento, inclui a monitorização no regulamento
  interno / informação aos encarregados de educação e define quem tem acesso.
- Não há gravação de ecrãs: as imagens só passam pelo servidor em memória.
- O log de auditoria regista logins, bloqueios, mensagens, links, partilhas e quem abriu que ecrã.
- Professores podem ficar limitados a certas salas (`add-teacher ... 'SALA12,INFO2'`).
- Proteção contra força bruta no login; tokens de sessão expiram (12 h por omissão).
- A chave dos agentes fica num ficheiro só legível por SYSTEM/Administradores.
- Os agentes verificam o certificado do servidor (pin SHA-256), por isso um PC falso na rede não
  consegue fazer-se passar pelo servidor para controlar os alunos.
- **Failsafe:** se o servidor ficar inacessível mais de 10 minutos, os PCs bloqueados desbloqueiam sozinhos.

## Limitações conhecidas

- O bloqueio não impede **Ctrl+Alt+Del** (o Windows não o permite); o bloqueio volta a aparecer logo a seguir.
- Durante prompts do UAC / ecrã de segurança, a imagem desse PC não atualiza.
- A partilha de ecrã envia imagens JPEG (não vídeo), adequada a apresentações/demonstrações, não a vídeo
  fluido. Não há som.
- Sem "lista branca" de sites/aplicações; para isso, usar as políticas do Windows/firewall da escola.

## Testar sem PCs Windows

```bash
cd server && dotnet run                    # gera data/ com credenciais na primeira execução
cd tools && NODE_TLS_REJECT_UNAUTHORIZED=0 node fake-agent.mjs wss://localhost:8443/ws/agent <EnrollmentKey> SALA12-PC01 SALA12
```

O `fake-agent.mjs` simula um PC: envia miniaturas, responde a mensagens e mostra no terminal os comandos recebidos.

Num PC Windows, para testar o agente sem o instalar como serviço: `ClassroomAgent.exe --console`
(com `C:\ProgramData\ClassroomAgent\agent.json` criado à mão).

## Estrutura

```
server/   ClassroomServer (ASP.NET Core 8): relay WebSocket, login, auditoria, consola web (wwwroot/)
agent/    ClassroomAgent (.NET 8 WinForms): serviço + processo de sessão (captura, bloqueio, mensagens, input)
deploy/   scripts de instalação (servidor, agentes/GPO)
tools/    agente simulado para testes
```
