# FIEMGPlayBot — auto-liker para o feed do FIEMG Play (Cluube)

Bot em **C# / .NET Framework 4.x** que autentica no FIEMG Play e reage ("Amei") aos
posts **ainda não avaliados** do feed, com scroll, espaçamento de requisições e
gerenciamento de memória.

## Zero dependências externas

- **Navegador:** usa o **Microsoft Edge** que já vem no Windows (Chromium).
- **Driver:** nenhum. Fala direto com o Edge via **Chrome DevTools Protocol (CDP)** por WebSocket.
- **Bibliotecas:** apenas a Base Class Library do .NET (`ClientWebSocket`, `HttpClient`,
  `JavaScriptSerializer`). **Nenhum pacote NuGet**, nenhum `chromedriver`.
- **Compilador:** o `csc.exe` que já existe no Windows
  (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). Nada a instalar.

## Como rodar

1. Crie o `config.json` a partir do modelo (`copy config.example.json config.json`) e
   preencha credenciais/parâmetros — veja abaixo. O `config.json` é **ignorado pelo Git**
   (contém a senha) — versione apenas o `config.example.json`.
2. Dê **duplo-clique em `run.bat`** (ou rode no terminal). Ele compila `FIEMGPlayBot.exe`
   e executa. Recompilar só é necessário se você editar o `.cs`.
3. Para reexecutar depois de compilado, rode `FIEMGPlayBot.exe` direto.

> O Edge sobe em modo headless (sem janela) com um perfil próprio em
> `%TEMP%\FIEMGPlayBot_profile`, que **persiste o login** entre execuções.

## config.json

| Campo | Significado |
|---|---|
| `email` / `password` | Credenciais do FIEMG Play. **Mantenha o arquivo privado.** |
| `headless` | `true` = sem janela (produção). `false` ajuda a depurar visualmente. |
| `verbose` | `true` = logs de diagnóstico detalhados (login/feed). |
| `blockImages` | `true` bloqueia imagens (menos RAM), mas pode atrapalhar o clique nos corações. Padrão `false`. |
| `minDelayMs` / `maxDelayMs` | Intervalo aleatório entre reações (anti-bloqueio). |
| `longPauseEveryLikes` / `longPauseMs` | Pausa longa a cada N reações. |
| `reloadEveryLikes` | Recarrega a página a cada N reações para liberar memória (DOM acumulado). |
| `maxLikes` | Limite total por execução (`0` = sem limite). |
| `maxEmptyScrollRounds` | Rodadas seguidas sem posts novos → encerra (fim do feed). |
| `maxHearts` | **(padrão `true`)** Sempre avalia com o **máximo de corações** (o de maior valor = "Amei"), robusto a qualquer quantidade. Quando `true`, ignora `heartIndex`. |
| `heartIndex` | Usado **só quando `maxHearts: false`**. Qual reação clicar (1=":-(", 2=":-|", 3=":-)", 4="Uhuu", **5="Amei"**). |
| `stopAfterNoGain` | Se os pontos não subirem em N avaliações seguidas, encerra (provável teto diário). `0` desliga. |
| `doneFile` | Arquivo com os IDs já processados (dedup entre execuções). |

## Como funciona (resumo técnico)

1. **Sobe o Edge** com `--remote-debugging-port` + `--remote-allow-origins=*` e perfil persistente.
2. **Conecta via CDP** (descobre o WebSocket em `http://127.0.0.1:<porta>/json`).
3. **Login (2 etapas):** e-mail → "Entrar" → modal de senha → "OK". Detecta sucesso pela home.
4. **Loop do feed (avalia tudo até esgotar):**
   - Coleta os posts **não avaliados** — sinais robustos: wrapper com classe `post_rating`
     (vs `post_rated` = já avaliado) **ou** o coração `.beforeRated` visível. O ID vem do
     token numérico de `.rating_options`.
   - Para cada ID novo: clica no coração `heartIndex` (revela os corações via `.beforeRated` se preciso).
   - Espaça com delay aleatório; pausa longa periódica.
   - Quando não há mais avaliáveis na tela, **clica no botão "Carregar mais"** para paginar
     e repete. Quando **não existe mais "Carregar mais"** e não há avaliáveis, **encerra** (feed esgotado).
5. **Deduplicação** por ID do post em `done.txt` (não reage duas vezes ao mesmo post).

## Notas importantes (descobertas no mapeamento)

- A plataforma é **ZK Framework** (estado no servidor; toda ação vai para `/zkau`). Por isso
  exige um navegador real — não dá para replicar via HTTP puro de forma confiável.
- O feed marca cada post como **`post_rating`** (não avaliado) ou **`post_rated`** (já avaliado).
  O bot só age nos **não avaliados**. Reagir a um post já avaliado **não** concede pontos.
- Após reagir, o widget volta a exibir o coração vazio — por isso a deduplicação é por **ID**,
  não pela imagem.
- O mecanismo de reação foi validado no mapeamento (clique → **+pontos** confirmado ao vivo).
  Se ao rodar aparecer "0 avaliados" + "sem Carregar mais", significa que a conta **já avaliou
  todos os posts disponíveis** no momento (rode novamente quando houver posts novos da galera).
- **Carregamento:** o feed NÃO usa scroll infinito automático — o "Carregar mais" é um **ícone
  de seta-círculo** (`i.fa-chevron-circle-down`) no fim da página (às vezes sem texto). Ele só
  aparece quando há mais posts. O bot clica nele para paginar até esgotar. ✔ Validado ao vivo:
  o bot clicou e o feed cresceu 5 → 10 → 15, reagindo aos não avaliados de cada lote.
- **Pontos / TETO DIÁRIO (importante):** a plataforma tem um **limite diário de pontos por curtidas**.
  Comprovado ao vivo: ao atingir o teto, o clique de avaliação é enviado (mesmo `/zkau` de um clique
  humano) e o post mostra os 5 corações + estado `post_rate_waiting`, **mas o servidor NÃO confirma** —
  ao recarregar a página o post **volta a não avaliado** e **nenhum ponto é creditado**. Não é falha do
  bot (cliques manuais reais também revertem após o teto). No início do dia as avaliações persistem e
  dão pontos; depois do teto, revertem.
  - Por isso o bot tem `stopAfterNoGain`: se os pontos não sobem em N avaliações, ele conclui que o
    teto foi atingido e **encerra com aviso**, em vez de clicar inutilmente.
  - **Recomendação:** rodar **1× por dia** (no dia seguinte o teto reseta e as avaliações voltam a
    valer pontos, até o limite).
- **Filtro "O que quero ver":** o feed tem os filtros *"Acompanhe as atividades"* (posts da galera —
  os que rendem pontos) e *"Meus posts"*. O bot opera sobre o feed padrão (galera). Se você tiver
  deixado "Meus posts" selecionado e o bot não achar nada, troque para "Acompanhe as atividades"
  no menu ☰ e rode de novo.

## Uso responsável

Automatiza ações na **sua própria conta**. Os delays evitam sobrecarregar o servidor e reduzem
o risco de bloqueio. Automação pode violar os Termos de Uso da plataforma — use por sua conta e risco.

## Arquivos

- `FIEMGPlayBot.cs` — código-fonte (único arquivo). **Versionado.**
- `config.example.json` — modelo de configuração (sem senha). **Versionado.**
- `config.json` — sua configuração real com credenciais. **Ignorado pelo Git** (`.gitignore`).
- `run.bat` — compila com o `csc` in-box e executa. **Versionado.**
- `README.md` / `LICENSE` — documentação e licença. **Versionados.**
- `FIEMGPlayBot.exe` — gerado pela compilação. **Ignorado pelo Git.**
- `done.txt` — IDs já processados (gerado em runtime). **Ignorado pelo Git.**
