# FIEMGPlayBot — auto-liker para o feed do FIEMG Play (Cluube)

Bot em **C# / .NET Framework 4.x** que autentica no FIEMG Play e reage ("Amei") aos
posts **ainda não avaliados** do feed, com scroll, espaçamento de requisições e
gerenciamento de memória.

## Zero dependências externas

- **Navegador:** usa o **Microsoft Edge** que já vem no Windows (Chromium).
- **Driver:** nenhum. Fala direto com o Edge via **Chrome DevTools Protocol (CDP)** por WebSocket.
- **Bibliotecas:** apenas assemblies in-box do .NET Framework (`ClientWebSocket`, `HttpClient`,
  `JavaScriptSerializer` e `System.Management` p/ encerrar Edge órfão). **Nenhum pacote NuGet**,
  nenhum `chromedriver`.
- **Compilador:** o `csc.exe` que já existe no Windows
  (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). Nada a instalar.

## Como rodar

1. Crie o `config.json` a partir do modelo (`copy config.example.json config.json`) e
   preencha credenciais/parâmetros — veja abaixo. O `config.json` é **ignorado pelo Git**
   (contém a senha) — versione apenas o `config.example.json`.
2. Dê **duplo-clique em `run.bat`** (ou rode no terminal). Ele compila `FIEMGPlayBot.exe`
   e executa. Recompilar só é necessário se você editar o `.cs`.
3. Para reexecutar depois de compilado, rode `FIEMGPlayBot.exe` direto.
4. O bot roda **continuamente** buscando posts mais antigos; **pare-o fechando a janela** (ou
   `Ctrl+C`) quando quiser. Ao terminar (por interrupção, erro ou um limite opcional), a aplicação
   **aguarda você pressionar ENTER** antes de fechar a janela — assim os logs não somem.

> O Edge sobe em modo headless (sem janela) com um perfil próprio em
> `%TEMP%\FIEMGPlayBot_profile`, que **persiste o login** entre execuções.

## config.json

| Campo | Significado |
|---|---|
| `email` / `password` | Credenciais do FIEMG Play. **Mantenha o arquivo privado.** |
| `feedTab` | **(padrão `"Social"`)** Aba aberta antes de coletar os posts. A aba **Social** carrega **mais postagens** que a **Início**. Deixe **vazio** (`""`) para usar a aba atual (Início). |
| `headless` | `true` = sem janela (produção). `false` ajuda a depurar visualmente. |
| `verbose` | `true` = logs de diagnóstico detalhados (login/feed). |
| `blockImages` | **(padrão `true`)** Bloqueia imagens → feed mais leve (menos RAM e menos travadas do renderer). **Seguro:** a reação usa `rateClick` via `.click()` no elemento, sem depender da imagem carregada. |
| `minDelayMs` / `maxDelayMs` | Intervalo aleatório entre reações (anti-bloqueio). |
| `longPauseEveryLikes` / `longPauseMs` | Pausa longa a cada N reações. |
| `reloadEveryLikes` | Recarrega a página a cada N reações para liberar memória (DOM acumulado). |
| `maxLikes` | Limite total por execução (`0` = sem limite). |
| `maxEmptyScrollRounds` | Tentativas vazias seguidas (sem posts novos e sem "Carregar mais") antes de encerrar. **`0` (padrão) = nunca desiste sozinho** — o bot só para por interrupção do usuário ou erro crítico. |
| `skipPages` | Antes de analisar, **pula N páginas** clicando direto no "Carregar mais" (sem avaliar) e marca o que ficou acima como já processado — então a análise **começa a partir da página pulada** (mais fundo no histórico), e **não do topo**. `0` (padrão) = não pula. Se o feed acabar antes de N, para. |
| `maxHearts` | **(padrão `true`)** Sempre avalia com o **máximo de corações** (o de maior valor = "Amei"), robusto a qualquer quantidade. Quando `true`, ignora `heartIndex`. |
| `heartIndex` | Usado **só quando `maxHearts: false`**. Qual reação clicar (1=":-(", 2=":-|", 3=":-)", 4="Uhuu", **5="Amei"**). |
| `doneFile` | Arquivo com os IDs das curtidas que **renderam pontos** (dedup entre execuções). |
| `lastFile` | Arquivo que guarda **apenas o ID do último post analisado** (sobrescrito a cada post, com ou sem ganho). |
| `skipDelayMs` | **(padrão `300`)** Delay em ms entre cliques de "Carregar mais" durante o skip. Muito menor que o delay normal (2500ms) porque não está curtindo — só navegando. Resultado: 3000 páginas em ~15min em vez de ~2h. |
| `pruneEverySkips` | **(padrão `50`)** A cada N páginas puladas, **remove os posts antigos do DOM** e força garbage collection. **Essa é a melhoria crítica:** impede o acúmulo de milhares de nós DOM que causava o crash. `0` = nunca podar (não recomendado para skips grandes). |
| `maxSkipRetries` | **(padrão `3`)** Tentativas de retry se o Edge perder conexão durante o skip. |
| `skipProgressFile` | **(padrão `"skip_progress.txt"`)** Arquivo que salva o progresso do skip. Se o bot crashar na página 2500 de 3000, ao re-executar ele **retoma de onde parou** em vez de recomeçar do zero. |

## Como funciona (resumo técnico)

1. **Sobe o Edge** com `--remote-debugging-port` + `--remote-allow-origins=*` e perfil persistente.
   Antes, **encerra instâncias órfãs** do Edge deste mesmo perfil (de execuções anteriores
   interrompidas) — senão o novo Edge só encaminha para a instância velha e o bot trava em
   *"Conectando via CDP..."*. O filtro é pela linha de comando do perfil dedicado, então **nunca**
   fecha um Edge pessoal seu.
2. **Conecta via CDP** (descobre o WebSocket em `http://127.0.0.1:<porta>/json`).
3. **Login (2 etapas):** e-mail → "Entrar" → modal de senha → "OK". Detecta sucesso pela home.
4. **Abre a aba `feedTab` (padrão "Social"):** após o login (e após cada recarga de página) o bot
   clica na aba **Social**, que carrega **mais postagens** que a aba **Início**. É uma navegação
   ZK por AJAX (a URL não muda), por isso o clique é "trusted" via CDP.
5. **Loop do feed (roda continuamente, buscando posts mais antigos):**
   - Coleta os posts **não avaliados** — sinais robustos: wrapper com classe `post_rating`
     (vs `post_rated` = já avaliado) **ou** o coração `.beforeRated` visível. O ID vem do
     token numérico de `.rating_options`.
   - Para cada ID novo: clica no coração `heartIndex` (revela os corações via `.beforeRated` se preciso).
   - **Confere os pontos depois da curtida:** só grava o post no `done.txt` se a curtida **rendeu
     pontos**. Se não rendeu (post já contabilizado — a plataforma não regravou a curtida), o post
     fica **fora** do `done.txt` para ser tentado de novo numa execução futura. Dentro da mesma
     execução ele é deduplicado **em memória** (não repete/loopa).
   - Espaça com delay aleatório; pausa longa periódica.
   - Quando não há mais avaliáveis na tela, **clica no botão "Carregar mais"** para paginar
     (carrega posts **mais antigos**) e repete. **Não encerra** quando o botão some por um instante —
     segue rolando e tentando, pois ele costuma reaparecer. O bot **só para** por interrupção do
     usuário (fechar a janela / `Ctrl+C`), por erro crítico, ou por um limite **opcional**
     (`maxLikes` / `maxEmptyScrollRounds`, ambos `0` por padrão = sem limite).
6. **Deduplicação:** `done.txt` guarda só os posts cuja curtida **rendeu pontos** (entre execuções,
   não repete esses). Posts sem ganho ficam de fora de propósito — serão re-tentados depois.

## Notas importantes (descobertas no mapeamento)

- A plataforma é **ZK Framework** (estado no servidor; toda ação vai para `/zkau`). Por isso
  exige um navegador real — não dá para replicar via HTTP puro de forma confiável.
- O feed marca cada post como **`post_rating`** (não avaliado) ou **`post_rated`** (já avaliado).
  O bot só age nos **não avaliados**. Reagir a um post já avaliado **não** concede pontos.
- Após reagir, o widget volta a exibir o coração vazio — por isso a deduplicação é por **ID**,
  não pela imagem.
- O mecanismo de reação foi validado no mapeamento (clique → **+pontos** confirmado ao vivo).
  Como a aba **Social** sempre oferece o "Carregar mais", o bot vai fundo no histórico reagindo
  aos não avaliados; pare-o você mesmo (feche a janela) quando quiser.
- **Carregamento:** o feed NÃO usa scroll infinito automático — o "Carregar mais" é um **ícone
  de seta-círculo** (`i.fa-chevron-circle-down`) no fim da página (às vezes sem texto). Ele só
  aparece quando há mais posts. O bot clica nele para paginar até esgotar. ✔ Validado ao vivo:
  na aba Social o feed cresceu 10 → 220 (lotes de 5), reagindo aos não avaliados de cada lote.
  - ⚠️ **Múltiplos ícones por página:** a aba **Social** mantém no DOM as seções de outras abas
    (Início/Social compartilham `id="newsfeed"` duplicado) e ainda há um ícone igual na seção
    *"Atividades de Todos"*. Os de seções inativas ficam **ocultos** (`offsetParent===null`). Por
    isso o bot não usa `querySelector` (pegava o primeiro do DOM, um oculto, e achava que o feed
    tinha acabado): varre **todos**, fica com os **visíveis** e prioriza o que está dentro do
    **feed de posts** (`#newsfeed`), pegando o mais ao fim da página.
- **Pontos não somam em alguns posts (NÃO é teto):** a plataforma **não tem** teto diário de
  curtidas. Quando uma avaliação não aumenta os pontos, é porque **aquele post já foi contabilizado
  antes** — por um bug da plataforma a curtida não fica gravada, então o post **reaparece como não
  avaliado**, mas reagir de novo **não credita** (já contou). Isso é normal e esperado.
  - Por isso o bot **NÃO encerra** quando os pontos não sobem: ele apenas registra
    *"sem ganho: já contabilizado; NÃO gravado em done.txt"* e **segue buscando posts mais antigos**.
  - **`done.txt` só recebe curtidas que renderam pontos.** As sem ganho ficam de fora — assim, numa
    execução futura, o bot tenta de novo (caso a plataforma finalmente grave). Para evitar loop na
    **mesma** execução, esses posts são deduplicados em memória durante a sessão.
  - O bot roda **continuamente**; só para por **interrupção do usuário** (fechar a janela / `Ctrl+C`),
    **erro crítico**, ou um limite **opcional** (`maxLikes` / `maxEmptyScrollRounds`, `0` = sem limite).
- **Filtro "O que quero ver":** o feed tem os filtros *"Acompanhe as atividades"* (posts da galera —
  os que rendem pontos) e *"Meus posts"*. O bot opera sobre o feed padrão (galera). Se você tiver
  deixado "Meus posts" selecionado e o bot não achar nada, troque para "Acompanhe as atividades"
  no menu ☰ e rode de novo.

## Uso responsável

Automatiza ações na **sua própria conta**. Os delays evitam sobrecarregar o servidor e reduzem
o risco de bloqueio. Automação pode violar os Termos de Uso da plataforma — use por sua conta e risco.

## Como ir fundo no feed (skip de 3000+ páginas)

O feed do FIEMG Play carrega **5 posts por "Carregar mais"**. Para chegar em posts de 5-6 meses
atrás, são ~3000 cliques. Nas versões anteriores o bot **crashava** no meio porque o DOM acumulava
milhares de nós e o Edge ficava sem memória.

### Solução: novas configs de skip otimizado

No seu `config.json`, use estas configurações:

```json
{
  "skipPages": 3000,
  "skipDelayMs": 300,
  "pruneEverySkips": 50,
  "maxSkipRetries": 3,
  "skipProgressFile": "skip_progress.txt",
  "blockImages": true,
  "headless": true
}
```

### O que cada flag faz nesse cenário

| Flag | Valor | Efeito |
|---|---|---|
| `skipPages` | `3000` | Pula 3000 páginas antes de começar a curtir (chega ~5 meses atrás) |
| `skipDelayMs` | `300` | Delay de 300ms entre cliques (vs 2500ms normal). Total: ~15min para 3000 páginas |
| `pruneEverySkips` | `50` | **A cada 50 páginas, remove os posts antigos do DOM.** Isso é o que impede o crash — o Edge nunca acumula mais que ~270 posts na memória (50 páginas × 5 posts + buffer de 20) |
| `maxSkipRetries` | `3` | Se algo der errado, tenta 3 vezes antes de desistir |
| `skipProgressFile` | `"skip_progress.txt"` | Se crashar na página 2500, ao rodar de novo **continua da 2500** (não recomeça) |
| `blockImages` | `true` | Sem imagens = menos RAM consumida durante o skip |
| `headless` | `true` | Sem interface = menos RAM do renderer |

### Execução

```bat
rem Primeira vez (compila e roda):
run.bat

rem Vezes seguintes (ja compilado):
FIEMGPlayBot.exe
```

### Se o bot crashar no meio do skip

Basta **rodar de novo** — ele lê o `skip_progress.txt` e retoma do ponto salvo.
Para forçar recomeçar do zero, delete o `skip_progress.txt`.

### Dicas para skips muito grandes (5000+)

- Use `pruneEverySkips: 30` (poda mais frequente = menos memória)
- Se mesmo assim crashar, reduza para `pruneEverySkips: 20`
- O `verbose: true` mostra a cada poda quantos nós foram removidos — útil para monitorar

## Arquivos

- `FIEMGPlayBot.cs` — código-fonte (único arquivo). **Versionado.**
- `config.example.json` — modelo de configuração (sem senha). **Versionado.**
- `config.json` — sua configuração real com credenciais. **Ignorado pelo Git** (`.gitignore`).
- `run.bat` — compila com o `csc` in-box e executa. **Versionado.**
- `README.md` / `LICENSE` — documentação e licença. **Versionados.**
- `FIEMGPlayBot.exe` — gerado pela compilação. **Ignorado pelo Git.**
- `done.txt` — IDs das curtidas que renderam pontos (gerado em runtime). **Ignorado pelo Git.**
- `last.txt` — ID do último post analisado (gerado em runtime). **Ignorado pelo Git.**
- `skip_progress.txt` — Progresso do skip de páginas; permite retomar após crash (gerado em runtime). **Ignorado pelo Git.**
