# SalaViva — Guia de instalação e utilização

*A sala inteira, num só ecrã.* — EB Vasco da Gama · ES Eça de Queirós · EB Parque das Nações

Este guia parte da situação atual da escola: o servidor já está instalado em `ESEQ-SERVER`
(`C:\ClassroomServer`), a partilha `\\ESEQ-SERVER\SalaDeAula$` e a GPO **Sala de Aula - Agente** já existem.
Se estiveres a instalar do zero, vê o `README.md`.

---

## 1. Descarregar a versão nova

1. No Mac, abre **https://github.com/sendas/Remote/releases/tag/auto** (com a tua conta GitHub).
2. Descarrega **SalaViva-Servidor.zip** e **SalaViva-Agente.zip**.
3. Descompacta os dois no Mac (duplo clique) e passa as pastas para o servidor pelo Ambiente de Trabalho
   Remoto (pasta partilhada do Mac). No servidor, deixa-as assim:
   - `C:\SalaViva-Servidor`
   - `C:\SalaViva-Agente` — tem de conter a pasta **`leve`** e o ficheiro **`preparar-partilha.ps1`**.

> Daqui para a frente o **servidor atualiza-se sozinho** (ver ponto 8). Os passos 1 e 2 só são precisos
> desta vez — e sempre que a atualização automática não funcionar.

## 2. Atualizar o servidor

No servidor, **PowerShell como Administrador**, cola tudo de uma vez:

```powershell
Set-Service ClassroomServer -StartupType Disabled
taskkill /F /IM ClassroomServer.exe
for ($i = 0; $i -lt 20 -and (Get-Process ClassroomServer -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
Get-ChildItem C:\SalaViva-Servidor -Recurse | Unblock-File
Copy-Item C:\SalaViva-Servidor\ClassroomServer.exe C:\ClassroomServer\ -Force
Copy-Item C:\SalaViva-Servidor\wwwroot\* C:\ClassroomServer\wwwroot\ -Recurse -Force
Set-Service ClassroomServer -StartupType Automatic
Start-Service ClassroomServer
Start-Sleep 5
"Servidor: " + (Get-Service ClassroomServer).Status
```

Tem de aparecer **Servidor: Running**. (O erro "processo não encontrado" do `taskkill` não tem importância.)
As contas, a chave, o certificado e as salas ficam como estavam.

## 3. Atualizar o agente dos PCs

```powershell
cd C:\SalaViva-Agente
Get-ChildItem -Recurse | Unblock-File
Set-ExecutionPolicy -Scope Process Bypass -Force
.\preparar-partilha.ps1 -ServerUrl 'wss://10.210.118.246:8443/ws/agent'
```

Termina com **"Partilha pronta"**. A partir daí:
- os PCs **atualizam-se sozinhos no próximo arranque** (a GPO corre o instalador, que vê que há versão nova);
- para atualizar já os PCs que estão ligados: `.\instalar-agora.ps1 -Filtro 'EBVG*'`.

## 4. Primeira configuração na consola

Abre **https://10.210.118.246:8443/** no Mac (Chrome ou Safari) e entra como administrador.

**Escolas e salas** (menu com o teu nome → 🏫 Escolas e salas)
1. Escolhe a escola no separador (EBVG, ESEQ, EBPN).
2. Para cada pasta do AD:
   - **Nome na consola** — como a sala aparece (pastas com o mesmo nome ficam no mesmo grupo; ex.: `Sala 13` → `Sala TIC 2`);
   - **Instalar** — marca para os PCs dessa pasta receberem o agente no próximo arranque.
3. **Guardar.** Não é preciso mexer na GPO.

**Utilizadores** (menu com o teu nome → 👥 Utilizadores)
1. **＋ Novo colega** → utilizador, nome, palavra-passe (mín. 8).
2. Escolhe o acesso: **todas as escolas**, **uma escola inteira**, ou **salas soltas**.
   "Administrador" vê tudo e gere utilizadores e salas.
3. Cada colega pode mudar a própria palavra-passe (menu com o nome → 🔑).

## 5. Teste num PC antes de usar numa aula

Num PC de alunos, com uma conta de aluno com sessão iniciada, confirma na consola:

| Teste | Resultado esperado |
|---|---|
| O cartão do PC | Mostra o **nome do aluno** (ex.: "Maria Silva") e, por baixo, o PC e a sala |
| Duplo clique | Abre o ecrã em grande; o aluno vê o aviso vermelho "O professor está a ver" |
| 🔒 Bloquear → durante 5 min | Ecrã inteiro com a cor escolhida e contagem; desbloqueia sozinho no fim |
| 📣 Aviso → Notificação num canto | Popup colorido no canto escolhido |
| 📣 Aviso → 2 min + "Bloquear o PC" no fim | Contagem; ao chegar a zero, o PC bloqueia |
| 🚫 Sem Internet / 🌐 Com Internet | Sites deixam de abrir / voltam a abrir (a rede da escola continua a funcionar) |
| 📁 Enviar ficheiro | Abre a pasta "Recebidos do professor" no PC com o ficheiro |
| 📥 Recolher trabalhos | Um ZIP por aluno em ⋯ Mais → 📦 Ver trabalhos recolhidos |
| 🗳 Pergunta | Janela com botões no PC; resultados ao vivo na consola |
| Botão direito → 🎬 Mostrar este ecrã à turma | Os outros PCs da sala veem o ecrã desse aluno |
| No PC: ícone SalaViva → ✋ Levantar a mão | ✋ no cartão e na coluna de mensagens |

Se alguma coisa falhar, envia-me as últimas linhas de
`\\NOME-DO-PC\C$\ProgramData\ClassroomAgent\agent.log`.

## 6. Numa aula — referência rápida

| Quero… | Faço… |
|---|---|
| Ver só a minha sala | Menu **Sala** no topo (fica memorizado) |
| Chamar a atenção | 🔒 **Bloquear** (cor + texto), ou 📣 **Aviso** em ecrã inteiro |
| "Faltam 5 minutos" e depois parar | 📣 Aviso, 5 min, "Quando acabar: Bloquear o PC" |
| Pausa de 10 minutos | 🔒 Bloquear → Duração 10 min (desbloqueia sozinho) |
| Teste sem Internet | Seleciona todos → 🚫 **Sem Internet** (no fim: 🌐 Com Internet) |
| Dar a ficha | Seleciona todos → 📁 **Enviar ficheiro** |
| Recolher os trabalhos | Os alunos gravam em **Entregar ao professor** (ícone SalaViva junto ao relógio) → 📥 **Recolher** |
| Saber se perceberam | 🗳 **Pergunta** → 👍 🤔 👎 |
| Mostrar o meu ecrã | 📺 **Partilhar o meu ecrã** |
| Mostrar o trabalho de um aluno | Botão direito no aluno → 🎬 **Mostrar este ecrã à turma** |
| Falar com um aluno | Botão direito → 💬 **Conversar** (ou responder na coluna da direita) |
| Começar com os PCs limpos | ⋯ Mais → ✖ **Fechar todos os programas** |
| Registar presenças | ⋯ Mais → 📋 **Exportar lista** (abre no Excel) |
| Fim da aula | ⋯ Mais → ↩ Terminar sessão / ⏻ Desligar |

Atalhos: clique seleciona; **Ctrl/Cmd + clique** seleciona vários; **duplo clique** abre em grande;
**botão direito** num PC abre o menu desse PC.

## 7. Resolução de problemas

| Problema | O que fazer |
|---|---|
| A consola não abre | No servidor: `(Get-Service ClassroomServer).Status` tem de ser Running; senão `Start-Service ClassroomServer` |
| Um PC não aparece | Está ligado e na sala ativa? No servidor: `.\instalar-agora.ps1 -Computadores NOME-DO-PC` e lê o resultado |
| "Sem sala: nenhum MAC…" | O MAC desse PC não está em nenhuma pasta de sala do AD — acrescenta a conta MAC na pasta certa |
| "falta o .NET Framework 4.8" | Windows 7 sem .NET 4.8: instala-o nesse PC (Windows Update ou instalador offline) |
| Aparece a conta em vez do nome | A conta não tem "Nome a apresentar" no AD — preenche-o em Utilizadores e Computadores do AD |
| "Sem Internet" não corta | A escola usa um proxy interno? Nesse caso o corte tem de ser feito no proxy/firewall da escola |
| Uma turma ficou presa num ecrã partilhado | Basta o PC voltar a ligar-se ao servidor (ou reiniciar o serviço do servidor): o ecrã fecha-se sozinho |
| Um PC ficou sem Internet depois de tirar o agente | No servidor: `.\install-remote.ps1 -Computer NOME-DO-PC -Uninstall` (remove também a regra) |
| O serviço não para | `Set-Service ClassroomServer -StartupType Disabled` e depois `taskkill /F /IM ClassroomServer.exe` |

## 7b. Faltam PCs numa sala

1. Abre o último relatório: `C:\ClassroomServer\data\instalar-agora-*.csv` (Excel). Para cada PC:
   | Resultado | O que fazer |
   |---|---|
   | desligado ou inacessível | Liga-o e repete `.\instalar-agora.ps1 -Computadores 'NOME'` |
   | Sem sala: nenhum MAC… | O MAC do PC não está em nenhuma pasta de sala do AD → instala à mão (abaixo) ou acrescenta a conta MAC na pasta certa |
   | falta o .NET Framework | Instala o .NET Framework 4.8 nesse PC (Windows Update ou instalador offline) e repete |
   | ERRO… | Envia as últimas linhas de `\\NOME\C$\ProgramData\ClassroomAgent\agent.log` |
2. Se não sabes o nome de um PC: no próprio PC, *Definições → Sistema → Acerca de* (ou escreve `hostname` na linha de comandos).
3. **Instalação à mão** (indicas tu a escola e a pasta da sala no AD):
   ```powershell
   cd C:\SalaViva\Agente
   .\install-remote.ps1 -Computer 'NOME-DO-PC' -School EBVG -Room 'Sala TIC1' -ServerUrl 'wss://10.210.118.246:8443/ws/agent'
   ```
   Estes PCs não se atualizam sozinhos pela GPO (a GPO não os reconhece pelo MAC): repete o comando quando houver versão nova.

## 8. Atualizações e gestão pela consola (⚙️ Sistema e instalação)

Menu com o teu nome → **⚙️ Sistema e instalação** (só administradores). No topo da consola vês sempre a
versão e a data, ex.: `v2.1.0 · 25/11/2026 20:34`.

**Como funcionam as atualizações**
1. Sempre que há versão nova no GitHub, o **servidor** descarrega-a e instala-a sozinho — só dentro do horário
   definido (por omissão **20:00–07:00**) e **sem nenhuma consola aberta**, para nunca interromper uma aula.
2. Ao mesmo tempo renova o **agente na partilha** de instalação.
3. Cada **PC** compara o seu agente com o da partilha quando se liga ao servidor: se for diferente, atualiza-se
   sozinho em poucos segundos (sem reiniciar; nunca a meio de um bloqueio ou partilha). Os PCs que estavam
   desligados de noite atualizam-se quando forem ligados.

**No painel**
- *Versão, última verificação e resultado* — se aparecer "Falhou a ligação ao GitHub", o servidor não consegue
  sair para a Internet com TLS 1.2 (limite do Windows 2008 R2): nesse caso atualiza à mão com os passos 1 a 4.
- *Agentes ligados* — quantos PCs estão na versão atual e quantos estão a atualizar (⟳ no cartão).
- **Atualizar automaticamente / horário** — liga, desliga ou muda o horário e carrega em *Guardar*.
- **Verificar agora** — só verifica (instala à noite). **Atualizar já** — instala logo (o servidor reinicia ~1 min).

**Instalar nos PCs pela consola**
- Uma única vez, no servidor (PowerShell como Administrador, na pasta do agente):
  ```powershell
  .\criar-tarefa-instalar.ps1
  ```
  Pede a palavra-passe da tua conta de administrador; o Windows guarda-a (a consola nunca a vê).
- Depois, no painel: **▶ Instalar agora nos PCs ligados**. O relatório aparece por baixo, agrupado
  (instalado, já tinha, desligado, falta .NET…) — clica num grupo para ver os PCs.
- Em **🏫 Escolas e salas**, a coluna *Ligados · Com agente* e o botão **👁 PCs** mostram, para cada sala, que
  PCs estão ligados, quais já têm o agente e quais **nunca apareceram** (os que faltam).

## 9. Privacidade (RGPD)

- Antes de usar com turmas, informa o Encarregado de Proteção de Dados e inclui a monitorização no regulamento interno.
- O aluno vê o ícone SalaViva e um aviso vermelho sempre que abres o ecrã dele em grande.
- Não há gravação de ecrãs. O registo de auditoria (`C:\ClassroomServer\data\auditoria`) guarda quem fez o quê e quando.
