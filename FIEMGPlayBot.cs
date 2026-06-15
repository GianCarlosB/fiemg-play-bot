// FIEMGPlayBot.cs
// Auto-liker para o feed do FIEMG Play (Cluube), construido sobre ZK Framework.
//
// SEM dependencias externas: pilota o Microsoft Edge (ja instalado no Windows)
// pelo Chrome DevTools Protocol (CDP) via WebSocket, usando APENAS a Base Class
// Library do .NET Framework 4.x (ClientWebSocket, HttpClient, JavaScriptSerializer).
//
// Compilado com o csc.exe in-box do Windows (C# 5). Ver run.bat.
//
// Uso etico: este script automatiza acoes na PROPRIA conta do usuario. Ele espaca
// as requisicoes para nao sobrecarregar o servidor e reduzir risco de bloqueio.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;          // WMI: localizar Edge orfao pela linha de comando (in-box do Windows)
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FIEMGPlayBot
{
    // ---------------------------------------------------------------------
    // Configuracao (lida de config.json)
    // ---------------------------------------------------------------------
    class Config
    {
        public string LoginUrl = "https://www.cluube.com/interactivity.do?a=fiemgplay&desktop=true";
        public string FeedUrl = "https://www.cluube.com/interactivity.do?a=fiemgplay&desktop=true";
        public string FeedTab = "Social";   // aba a abrir antes de coletar posts ("Social" carrega mais que "Inicio"). Vazio = aba atual.
        public string Email = "";
        public string Password = "";
        public int EdgePort = 9222;
        public int CdpTimeoutMs = 30000;     // timeout por chamada CDP; se a pagina parar de responder, lanca erro em vez de travar
        public bool Headless = true;
        public bool BlockImages = true;      // bloquear imagens deixa o feed leve (menos RAM/travadas). Seguro: a reacao usa rateClick(.click()), nao depende da imagem renderizada
        public int MinDelayMs = 2500;
        public int MaxDelayMs = 6000;
        public int LongPauseEveryLikes = 30;
        public int LongPauseMs = 60000;
        public int ReloadEveryLikes = 40;    // recarrega a pagina periodicamente p/ zerar o DOM acumulado (memoria)
        public int MaxLikes = 500;           // limite total por execucao (0 = sem limite)
        public int MaxEmptyScrollRounds = 0; // tentativas vazias seguidas antes de encerrar; 0 = nunca desiste sozinho (so para por interrupcao do usuario ou erro critico)
        public int ScrollWaitMs = 2500;
        public bool MaxHearts = true;        // SEMPRE clicar no coracao de maior valor (ultimo = "Amei"), robusto a qualquer quantidade
        public int HeartIndex = 5;           // usado apenas quando MaxHearts=false. 1..5 (5="Amei")
        public string DoneFile = "done.txt";
        public string LastFile = "last.txt"; // grava (sobrescrevendo) o ID do ULTIMO post analisado, com ou sem ganho
        public bool Verbose = false;         // logs de diagnostico detalhados (login/feed)

        public static Config Load(string path)
        {
            var ser = new JavaScriptSerializer();
            string json = File.ReadAllText(path, Encoding.UTF8);
            var d = ser.DeserializeObject(json) as Dictionary<string, object>;
            var c = new Config();
            if (d == null) return c;
            c.LoginUrl = Str(d, "loginUrl", c.LoginUrl);
            c.FeedUrl = Str(d, "feedUrl", c.FeedUrl);
            c.FeedTab = Str(d, "feedTab", c.FeedTab);
            c.Email = Str(d, "email", c.Email);
            c.Password = Str(d, "password", c.Password);
            c.EdgePort = Int(d, "edgePort", c.EdgePort);
            c.CdpTimeoutMs = Int(d, "cdpTimeoutMs", c.CdpTimeoutMs);
            c.Headless = Bool(d, "headless", c.Headless);
            c.BlockImages = Bool(d, "blockImages", c.BlockImages);
            c.MinDelayMs = Int(d, "minDelayMs", c.MinDelayMs);
            c.MaxDelayMs = Int(d, "maxDelayMs", c.MaxDelayMs);
            c.LongPauseEveryLikes = Int(d, "longPauseEveryLikes", c.LongPauseEveryLikes);
            c.LongPauseMs = Int(d, "longPauseMs", c.LongPauseMs);
            c.ReloadEveryLikes = Int(d, "reloadEveryLikes", c.ReloadEveryLikes);
            c.MaxLikes = Int(d, "maxLikes", c.MaxLikes);
            c.MaxEmptyScrollRounds = Int(d, "maxEmptyScrollRounds", c.MaxEmptyScrollRounds);
            c.ScrollWaitMs = Int(d, "scrollWaitMs", c.ScrollWaitMs);
            c.MaxHearts = Bool(d, "maxHearts", c.MaxHearts);
            c.HeartIndex = Int(d, "heartIndex", c.HeartIndex);
            c.DoneFile = Str(d, "doneFile", c.DoneFile);
            c.LastFile = Str(d, "lastFile", c.LastFile);
            c.Verbose = Bool(d, "verbose", c.Verbose);
            return c;
        }

        static string Str(Dictionary<string, object> d, string k, string def)
        { return d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : def; }
        static int Int(Dictionary<string, object> d, string k, int def)
        { return d.ContainsKey(k) && d[k] != null ? Convert.ToInt32(d[k]) : def; }
        static bool Bool(Dictionary<string, object> d, string k, bool def)
        { return d.ContainsKey(k) && d[k] != null ? Convert.ToBoolean(d[k]) : def; }
    }

    // ---------------------------------------------------------------------
    // Cliente CDP minimo sobre WebSocket (request/response sequencial)
    // ---------------------------------------------------------------------
    class Cdp : IDisposable
    {
        readonly ClientWebSocket _ws = new ClientWebSocket();
        readonly JavaScriptSerializer _ser = new JavaScriptSerializer();
        public readonly List<string> Events = new List<string>();
        int _id = 0;
        public int TimeoutMs = 30000;   // timeout por chamada CDP: se a pagina/renderer travar, lanca em vez de pendurar

        public Cdp() { _ser.MaxJsonLength = 64 * 1024 * 1024; }

        public async Task ConnectAsync(int port)
        {
            string wsUrl = await GetPageWsUrl(port);
            await _ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        }

        async Task<string> GetPageWsUrl(int port)
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(5);
                for (int attempt = 0; attempt < 80; attempt++)
                {
                    try
                    {
                        string s = await http.GetStringAsync("http://127.0.0.1:" + port + "/json");
                        var arr = _ser.DeserializeObject(s) as object[];
                        if (arr != null)
                        {
                            foreach (var o in arr)
                            {
                                var d = o as Dictionary<string, object>;
                                if (d != null && d.ContainsKey("type") && Convert.ToString(d["type"]) == "page"
                                    && d.ContainsKey("webSocketDebuggerUrl"))
                                    return Convert.ToString(d["webSocketDebuggerUrl"]);
                            }
                        }
                    }
                    catch { /* Edge ainda subindo */ }
                    await Task.Delay(500);
                }
            }
            throw new Exception("Nao foi possivel obter o endpoint WebSocket do Edge (porta " + port + ").");
        }

        public async Task<Dictionary<string, object>> Send(string method, object prms)
        {
            int id = ++_id;
            var msg = new Dictionary<string, object>();
            msg["id"] = id;
            msg["method"] = method;
            if (prms != null) msg["params"] = prms;
            string json = _ser.Serialize(msg);

            // Timeout por chamada: o token cancela o send/receive se a resposta nao chegar a tempo,
            // evitando o bot pendurar para sempre quando o renderer do Edge trava.
            using (var cts = new CancellationTokenSource(TimeoutMs))
            {
                try
                {
                    await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                                        WebSocketMessageType.Text, true, cts.Token);

                    while (true)
                    {
                        string resp = await Receive(cts.Token);
                        var d = _ser.DeserializeObject(resp) as Dictionary<string, object>;
                        if (d == null) continue;
                        if (d.ContainsKey("id") && Convert.ToInt32(d["id"]) == id)
                            return d; // resposta do nosso comando
                        // captura eventos de erro/log para diagnostico
                        if (d.ContainsKey("method"))
                        {
                            string m = Convert.ToString(d["method"]);
                            if ((m == "Runtime.exceptionThrown" || m == "Log.entryAdded") && Events.Count < 200)
                            {
                                object prm = d.ContainsKey("params") ? d["params"] : null;
                                string s = prm != null ? _ser.Serialize(prm) : "";
                                if (s.Length > 280) s = s.Substring(0, 280);
                                Events.Add(m + " " + s);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("Sem resposta CDP em " + (TimeoutMs / 1000)
                        + "s para o comando '" + method + "' (a pagina/renderer do Edge parou de responder).");
                }
            }
        }

        async Task<string> Receive(CancellationToken ct)
        {
            var ms = new MemoryStream();
            var buf = new byte[16384];
            while (true)
            {
                var res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (res.MessageType == WebSocketMessageType.Close)
                    throw new Exception("WebSocket fechado pelo Edge.");
                ms.Write(buf, 0, res.Count);
                if (res.EndOfMessage) break;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // Avalia uma expressao JS e retorna o valor (string/num/bool) ou null.
        public async Task<object> Eval(string expr)
        {
            var p = new Dictionary<string, object>();
            p["expression"] = expr;
            p["returnByValue"] = true;
            p["awaitPromise"] = true;
            var resp = await Send("Runtime.evaluate", p);
            var result = resp.ContainsKey("result") ? resp["result"] as Dictionary<string, object> : null;
            if (result == null) return null;
            if (result.ContainsKey("exceptionDetails")) return null;
            var inner = result.ContainsKey("result") ? result["result"] as Dictionary<string, object> : null;
            if (inner == null) return null;
            return inner.ContainsKey("value") ? inner["value"] : null;
        }

        public async Task<string> EvalStr(string expr)
        {
            object v = await Eval(expr);
            return v == null ? null : Convert.ToString(v);
        }

        public async Task<int> EvalInt(string expr, int def)
        {
            object v = await Eval(expr);
            if (v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }

        public async Task Navigate(string url)
        {
            var p = new Dictionary<string, object>();
            p["url"] = url;
            await Send("Page.navigate", p);
        }

        // Clique "trusted" (o ZK ignora eventos sinteticos do JS, mas aceita input do CDP).
        public async Task ClickAt(double x, double y)
        {
            await Mouse("mouseMoved", x, y, 0);
            await Task.Delay(40);
            await Mouse("mousePressed", x, y, 1);
            await Task.Delay(50);
            await Mouse("mouseReleased", x, y, 1);
        }

        async Task Mouse(string type, double x, double y, int clickCount)
        {
            var p = new Dictionary<string, object>();
            p["type"] = type;
            p["x"] = x;
            p["y"] = y;
            p["button"] = "left";
            p["buttons"] = (type == "mousePressed") ? 1 : 0;
            p["clickCount"] = clickCount;
            await Send("Input.dispatchMouseEvent", p);
        }

        // Insere texto como input "trusted" (dispara eventos que o ZK escuta).
        public async Task InsertText(string text)
        {
            var p = new Dictionary<string, object>();
            p["text"] = text;
            await Send("Input.insertText", p);
        }

        // Pressiona Enter (alguns modais ZK submetem no Enter; tambem forca blur/commit).
        public async Task PressEnter()
        {
            var down = new Dictionary<string, object>();
            down["type"] = "rawKeyDown"; down["windowsVirtualKeyCode"] = 13; down["key"] = "Enter"; down["code"] = "Enter";
            await Send("Input.dispatchKeyEvent", down);
            var up = new Dictionary<string, object>();
            up["type"] = "keyUp"; up["windowsVirtualKeyCode"] = 13; up["key"] = "Enter"; up["code"] = "Enter";
            await Send("Input.dispatchKeyEvent", up);
        }

        public async Task SetImagesEnabled(bool enabled)
        {
            // Bloqueia/permite carregamento de imagens via CDP (Network domain).
            await Send("Network.enable", null);
            var p = new Dictionary<string, object>();
            if (enabled) p["urls"] = new string[] { };
            else p["urls"] = new string[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp" };
            await Send("Network.setBlockedURLs", p);
        }

        public void Dispose()
        {
            try { if (_ws.State == WebSocketState.Open) _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(2000); }
            catch { }
            try { _ws.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------------
    // Programa principal
    // ---------------------------------------------------------------------
    class Program
    {
        static readonly Random _rng = new Random();
        static Config _cfg;
        static Cdp _cdp;

        static void Main()
        {
            try { MainAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log("ERRO FATAL: " + ex.Message); }
            finally { WaitForUserToClose(); }
        }

        // Mantem a janela aberta ao final (sucesso OU erro) ate o usuario confirmar — assim,
        // ao rodar com duplo-clique no .exe a janela nao fecha sozinha levando embora os logs.
        // Se a saida estiver redirecionada (sem console interativo), ReadLine retorna sem
        // bloquear, entao nao trava execucoes automatizadas.
        static void WaitForUserToClose()
        {
            Console.WriteLine();
            Console.WriteLine("Execucao finalizada. Pressione ENTER para fechar a janela...");
            try { Console.ReadLine(); }
            catch { /* stdin indisponivel: nao bloqueia */ }
        }

        static async Task MainAsync()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = Path.Combine(baseDir, "config.json");
            if (!File.Exists(cfgPath))
            {
                Log("config.json nao encontrado em " + baseDir + " - crie o arquivo (veja README.md).");
                return;
            }
            _cfg = Config.Load(cfgPath);
            if (string.IsNullOrEmpty(_cfg.Email) || string.IsNullOrEmpty(_cfg.Password))
            {
                Log("Preencha email e password em config.json.");
                return;
            }

            string edge = FindEdge();
            if (edge == null) { Log("msedge.exe nao encontrado. Instale o Microsoft Edge."); return; }

            string dataDir = Path.Combine(Path.GetTempPath(), "FIEMGPlayBot_profile");
            KillStaleEdge(dataDir); // evita travar em "Conectando via CDP..." por Edge orfao de execucao anterior
            Process proc = LaunchEdge(edge, dataDir);
            Log("Edge iniciado (PID " + proc.Id + "). Conectando via CDP...");

            _cdp = new Cdp();
            _cdp.TimeoutMs = _cfg.CdpTimeoutMs;
            try
            {
                await _cdp.ConnectAsync(_cfg.EdgePort);
                await _cdp.Send("Page.enable", null);
                await _cdp.Send("Runtime.enable", null);
                if (_cfg.BlockImages) { await _cdp.SetImagesEnabled(false); Log("Imagens bloqueadas (modo economia de memoria)."); }
                Log("Conectado.");

                await EnsureLoggedIn();
                await FeedLoop();
            }
            finally
            {
                _cdp.Dispose();
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                Log("Encerrado.");
            }
        }

        // -----------------------------------------------------------------
        // Login (e-mail -> Entrar -> modal de senha -> OK)
        // -----------------------------------------------------------------
        static async Task EnsureLoggedIn()
        {
            await _cdp.Navigate(_cfg.LoginUrl);
            await WaitDomReady(20000);
            await Task.Delay(1500);
            Dbg("DIAG inicial: " + await _cdp.EvalStr(JS_DIAG));

            // Ja logado? (home autenticada presente)
            if (await _cdp.EvalInt(JS_LOGGED_IN, 0) > 0)
            { Log("Sessao reaproveitada (ja autenticado). Pontos: " + await GetPoints()); return; }

            // Aceitar cookies (essenciais para login funcionar)
            await ClickByTextIfPresent("Aceitar");
            await Task.Delay(700);

            // Garantir que o formulario de e-mail apareca: clicar "Entrar" da landing e aguardar (com retries)
            for (int i = 0; i < 5 && await GetCenter(JS_EMAIL_CENTER) == null; i++)
            {
                await ClickByTextIfPresent("Entrar");
                await Task.Delay(1800);
            }

            Dbg("DIAG pre-email: " + await _cdp.EvalStr(JS_DIAG));
            Log("Autenticando: preenchendo e-mail...");
            if (!await ClickCenter(JS_EMAIL_CENTER, 10000))
                throw new Exception("Campo de e-mail nao encontrado. DIAG: " + await _cdp.EvalStr(JS_DIAG));
            await _cdp.InsertText(_cfg.Email);
            await Task.Delay(400);

            // Botao "Entrar" do formulario (blur do campo dispara onChange do ZK)
            if (!await ClickByText("Entrar", 8000))
                throw new Exception("Botao Entrar nao encontrado.");

            Log("Aguardando modal de senha...");
            if (!await WaitUntil(async () => await GetCenter(JS_PWD_CENTER) != null, 12000, 400))
                throw new Exception("Modal de senha nao apareceu. INPUTS: " + await _cdp.EvalStr(JS_INPUTS));

            Log("Preenchendo senha...");
            await ClickCenter(JS_PWD_CENTER, 3000);
            await _cdp.InsertText(_cfg.Password);
            await Task.Delay(500);
            Dbg("DIAG pos-senha: " + await _cdp.EvalStr(JS_INPUTS));

            bool okClicked = await ClickByText("OK", 6000);
            Dbg("OK clicado=" + okClicked + ". Confirmando com Enter...");
            await _cdp.PressEnter();
            await Task.Delay(2500);
            Dbg("DIAG pos-OK: " + await _cdp.EvalStr(JS_DIAG));

            Log("Confirmando login...");
            if (!await WaitUntil(async () => await _cdp.EvalInt(JS_LOGGED_IN, 0) > 0, 25000, 700))
                throw new Exception("Login nao confirmado. DIAG: " + await _cdp.EvalStr(JS_DIAG));
            Log("Login concluido. Pontos atuais: " + await GetPoints());
        }

        // -----------------------------------------------------------------
        // Loop principal: coleta posts, curte os novos, rola, repete
        // -----------------------------------------------------------------
        static async Task FeedLoop()
        {
            await EnsureFeedLoaded();
            await DismissBlockingModal(); // fecha o popup diario (Web Check In) que cobre o feed e trava o 'Carregar mais'

            string donePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _cfg.DoneFile);
            string lastPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _cfg.LastFile);
            var done = LoadDone(donePath);
            Log("IDs ja processados carregados: " + done.Count);

            int totalLiked = 0;
            int emptyRounds = 0;
            int lastPoints = await GetPoints(); // ponto de partida p/ mostrar o delta por curtida

            while (true)
            {
                if (_cfg.MaxLikes > 0 && totalLiked >= _cfg.MaxLikes)
                { Log("Limite de " + _cfg.MaxLikes + " avaliacoes atingido."); break; }

                string idsCsv = await _cdp.EvalStr(JS_COLLECT_IDS);
                var ids = (idsCsv ?? "").Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                var novos = ids.Where(x => !done.Contains(x)).Distinct().ToList();

                if (novos.Count > 0)
                {
                    emptyRounds = 0;
                    foreach (var id in novos)
                    {
                        if (_cfg.MaxLikes > 0 && totalLiked >= _cfg.MaxLikes) break;

                        bool acted = await ReactToPost(id);
                        done.Add(id);   // dedup em MEMORIA sempre: evita reprocessar/loopar o mesmo post nesta execucao
                        WriteLast(lastPath, id);  // ULTIMO post analisado (sobrescreve sempre, com ou sem ganho)
                        totalLiked++;

                        // Espaco anti-bloqueio (tambem da tempo do servidor creditar os pontos antes da leitura).
                        await Task.Delay(_rng.Next(_cfg.MinDelayMs, _cfg.MaxDelayMs + 1));

                        // So grava em done.txt a curtida que REALMENTE rendeu pontos. Sem ganho = post ja
                        // contabilizado antes (a plataforma nao regrava a curtida): fica FORA do done.txt
                        // para ser tentado de novo numa execucao futura.
                        int after = await GetPoints();
                        bool gained = (lastPoints >= 0 && after >= 0 && after > lastPoints);
                        if (gained) AppendDone(donePath, id);
                        string delta = (lastPoints >= 0 && after >= 0)
                            ? (gained ? (" (+" + (after - lastPoints) + " pts -> " + after + ")")
                                      : " (sem ganho)")
                            : "";
                        if (after >= 0) lastPoints = after;
                        Log("[" + totalLiked + "] post " + id + (acted ? " avaliado" : " sem widget/pulado") + delta);

                        if (_cfg.LongPauseEveryLikes > 0 && totalLiked % _cfg.LongPauseEveryLikes == 0)
                        { Log("Pausa longa (" + (_cfg.LongPauseMs / 1000) + "s) para nao congestionar o servidor..."); await Task.Delay(_cfg.LongPauseMs); }

                        if (_cfg.ReloadEveryLikes > 0 && totalLiked % _cfg.ReloadEveryLikes == 0)
                        { Log("Recarregando pagina para liberar memoria..."); await ReloadFeed(); break; }
                    }
                    continue; // re-coleta: pode haver mais avaliaveis ja carregados
                }

                // Sem posts avaliaveis na tela -> carrega posts MAIS ANTIGOS via 'Carregar mais'
                bool grew = await TryLoadMore();
                if (grew) { emptyRounds = 0; continue; }

                // 'Carregar mais' nao cresceu agora. O bot NAO encerra por isso: ele continua
                // buscando posts mais antigos (o botao costuma reaparecer). So para por interrupcao
                // do usuario (fechar a janela / Ctrl+C) ou erro critico. 'maxEmptyScrollRounds' define
                // um limite OPCIONAL de tentativas vazias seguidas; 0 (padrao) = nunca desiste sozinho.
                emptyRounds++;
                if (_cfg.MaxEmptyScrollRounds > 0 && emptyRounds >= _cfg.MaxEmptyScrollRounds)
                { Log("Sem novos posts apos " + emptyRounds + " tentativas (maxEmptyScrollRounds). Encerrando."); break; }
                if (emptyRounds == 1 || emptyRounds % 10 == 0)
                    Log("Sem novos posts no momento (tentativa " + emptyRounds + "); rolando e seguindo a busca por posts mais antigos...");
                await ScrollDown();
                await Task.Delay(_cfg.ScrollWaitMs);
            }

            Log("FIM. Total de posts avaliados nesta execucao: " + totalLiked + ".");
        }

        // Reage a um post acionando o coracao escolhido via seu onclick INLINE (rateClick -> envia a
        // nota ao ZK por comm.sendEvent). Bem mais robusto que clique por coordenada: os coracoes
        // ficam ocultos (so aparecem no hover) e tem larguras/posicoes que faziam o clique por pixel
        // cair no coracao errado (dava nota 2 em vez de 5). Chamar .click() dispara a nota CERTA
        // independentemente de visibilidade/posicao. MaxHearts manda indice alto e o JS limita ao
        // ultimo coracao (maior valor = "Amei").
        static async Task<bool> ReactToPost(string id)
        {
            int idx = _cfg.MaxHearts ? 999 : _cfg.HeartIndex;
            string r = await _cdp.EvalStr(BuildJs(JS_RATE, id, idx));
            if (r == "ok") { await Task.Delay(400); return true; }
            return false; // post sem widget de reacao (ou nao encontrado na tela)
        }

        static async Task ReloadFeed()
        {
            await _cdp.Navigate(_cfg.FeedUrl);
            await WaitDomReady(20000);
            // se a sessao caiu, refaz login
            if (await GetCenter(JS_EMAIL_CENTER) != null) { Log("Sessao expirou; refazendo login."); await EnsureLoggedIn(); }
            // a renavegacao volta para 'Inicio' -> reabrir a aba configurada (Social) antes de continuar
            await GoToFeedTab();
            await WaitUntil(async () => await IsFeedReady(), 15000, 600);
        }

        static async Task ScrollDown()
        {
            await _cdp.Eval("window.scrollBy(0, Math.round(window.innerHeight*0.85)); 1");
        }

        // Clica em 'Carregar mais' (se existir) e espera novos posts. Retorna true se carregou mais.
        static async Task<bool> TryLoadMore()
        {
            await DismissBlockingModal(); // sem isso, a mascara do popup intercepta o clique no 'Carregar mais'
            var c = await GetCenter(JS_CARREGAR_CENTER);
            if (c == null) return false; // botao ausente => nao ha mais posts
            int before = await _cdp.EvalInt(JS_FEED_COUNT, 0);
            Log("Clicando em 'Carregar mais'...");
            await _cdp.ClickAt(c[0], c[1]);
            bool grew = await WaitUntil(async () => await _cdp.EvalInt(JS_FEED_COUNT, 0) > before, 12000, 700);
            int after = await _cdp.EvalInt(JS_FEED_COUNT, 0);
            Log("'Carregar mais': posts " + before + " -> " + after + (grew ? "" : " (sem novos)"));
            await Task.Delay(800);
            return grew;
        }

        // Fecha um popup MODAL do ZK que esteja cobrindo a pagina com '.z-modal-mask' — tipicamente o
        // "Web Check In" diario. A mascara (z-index alto) intercepta os cliques por COORDENADA, travando
        // o 'Carregar mais' (clique trusted) em loop nos primeiros posts — enquanto coracoes/abas seguem
        // funcionando por usarem .click() via JS (que ignora a mascara). Rola ao topo e da um clique
        // trusted no card do modal, o que REIVINDICA os +50 e fecha o popup; com fallback que esconde a
        // mascara via JS caso o fechamento normal nao resolva.
        static async Task DismissBlockingModal()
        {
            if (await _cdp.EvalInt(JS_HAS_MODAL_MASK, 0) <= 0) return; // nada bloqueando
            Log("Popup modal cobrindo o feed (ex.: Web Check In diario); fechando para liberar o 'Carregar mais'...");
            double[] c = await GetCenter(JS_MODAL_CENTER); // ja rola ao topo e retorna o centro do modal
            if (c != null) { await _cdp.ClickAt(c[0], c[1]); await Task.Delay(1200); }
            if (await _cdp.EvalInt(JS_HAS_MODAL_MASK, 0) > 0)
            { await _cdp.Eval(JS_HIDE_MODAL_MASK); Log("Mascara modal removida via fallback."); }
            else Log("Popup fechado.");
        }

        // Abre a aba de feed configurada (ex.: "Social", que carrega MAIS posts que "Inicio").
        // A aba e um <a onclick="selectMenuItem('li_friends')"> com href="#friends": um handler
        // INLINE comum (nao um widget ZK), entao invocar .click() via JS dispara o onclick de
        // forma confiavel. Vantagem sobre clique por coordenada: NAO depende de layout/visibilidade,
        // que era a causa da falha (a navbar ainda estava em 'Atualizando...' durante a carga inicial).
        static async Task GoToFeedTab()
        {
            if (string.IsNullOrEmpty(_cfg.FeedTab)) return; // vazio = usar a aba atual (padrao 'Inicio')
            Log("Abrindo a aba '" + _cfg.FeedTab + "' (carrega mais posts que 'Inicio')...");
            // WaitUntil cobre o estado 'Atualizando...' do ZK: tenta ate o <a> da aba existir no DOM.
            bool clicked = await WaitUntil(async () =>
                (await _cdp.EvalStr(BuildText(JS_CLICK_TAB, _cfg.FeedTab))) == "ok", 45000, 700);
            if (clicked) { await Task.Delay(2500); Log("Aba '" + _cfg.FeedTab + "' aberta."); } // espera o feed da aba renderizar
            else Log("AVISO: aba '" + _cfg.FeedTab + "' nao encontrada; seguindo no feed atual.");
        }

        // Garante que o feed tenha posts carregados (carga preguicosa: precisa de tempo + scroll suave)
        static async Task EnsureFeedLoaded()
        {
            await _cdp.Send("Log.enable", null);

            // 0) Troca para a aba de feed configurada (Social) ANTES de esperar posts:
            //    'Inicio' tambem tem .rating_options, entao a troca precisa vir primeiro.
            await GoToFeedTab();

            // 1) Espera na pagina atual (pos-login), sem re-navegar, com nudges suaves
            if (await WaitForPosts(60000)) return;

            // 2) Ultimo recurso: re-navega UMA vez
            Log("Feed ainda vazio; renavegando ao feed uma vez...");
            await _cdp.Navigate(_cfg.FeedUrl);
            await WaitDomReady(20000);
            if (await GetCenter(JS_EMAIL_CENTER) != null) { Log("Sessao caiu; refazendo login."); await EnsureLoggedIn(); }
            await GoToFeedTab();
            if (await WaitForPosts(60000)) return;

            Log("AVISO: feed vazio. DIAG: " + await _cdp.EvalStr(JS_FEED_DIAG));
            Log("Erros JS capturados: " + (_cdp.Events.Count == 0 ? "(nenhum)" : string.Join(" || ", _cdp.Events.Take(8).ToArray())));
        }

        static async Task<bool> WaitForPosts(int timeoutMs)
        {
            int waited = 0, pollMs = 2000;
            while (waited < timeoutMs)
            {
                int c = await _cdp.EvalInt(JS_FEED_COUNT, 0);
                if (c > 0) { Log("Feed carregado: " + c + " posts."); return true; }
                await _cdp.Eval("window.scrollBy(0, 600); 1");
                await Task.Delay(pollMs);
                waited += pollMs;
                if (waited % 10000 == 0) Dbg("  ...aguardando posts (" + (waited / 1000) + "s) DIAG: " + await _cdp.EvalStr(JS_FEED_DIAG));
            }
            return false;
        }

        // -----------------------------------------------------------------
        // Helpers de pagina
        // -----------------------------------------------------------------
        static async Task<bool> IsFeedReady()
        { return await _cdp.EvalInt(JS_FEED_COUNT, 0) > 0; }

        static async Task<int> GetPoints()
        { return await _cdp.EvalInt(JS_POINTS, -1); }

        static async Task WaitDomReady(int timeoutMs)
        { await WaitUntil(async () => (await _cdp.EvalStr("document.readyState")) == "complete", timeoutMs, 400); }

        // Retorna [x,y] do centro do elemento, ou null.
        static async Task<double[]> GetCenter(string js)
        {
            string s = await _cdp.EvalStr(js);
            if (string.IsNullOrEmpty(s) || s.IndexOf(',') < 0) return null;
            var parts = s.Split(',');
            double x, y;
            if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out x)
             && double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out y))
                return new double[] { x, y };
            return null;
        }

        static async Task<bool> ClickCenter(string js, int timeoutMs)
        {
            double[] c = null;
            bool ok = await WaitUntil(async () => { c = await GetCenter(js); return c != null; }, timeoutMs, 300);
            if (ok && c != null) { await _cdp.ClickAt(c[0], c[1]); return true; }
            return false;
        }

        static async Task<bool> ClickByText(string text, int timeoutMs)
        { return await ClickCenter(BuildText(JS_TEXT_CENTER, text), timeoutMs); }

        static async Task ClickByTextIfPresent(string text)
        {
            var c = await GetCenter(BuildText(JS_TEXT_CENTER, text));
            if (c != null) await _cdp.ClickAt(c[0], c[1]);
        }

        static async Task<bool> WaitUntil(Func<Task<bool>> cond, int timeoutMs, int pollMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try { if (await cond()) return true; } catch { }
                await Task.Delay(pollMs);
                waited += pollMs;
            }
            try { return await cond(); } catch { return false; }
        }

        // -----------------------------------------------------------------
        // Persistencia de IDs ja processados
        // -----------------------------------------------------------------
        static HashSet<string> LoadDone(string path)
        {
            var set = new HashSet<string>();
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                { var t = line.Trim(); if (t.Length > 0) set.Add(t); }
            return set;
        }

        static void AppendDone(string path, string id)
        { try { File.AppendAllText(path, id + Environment.NewLine); } catch { } }

        // Grava (SOBRESCREVENDO) o ID do ultimo post analisado — o arquivo mantem sempre apenas o mais recente.
        static void WriteLast(string path, string id)
        { try { File.WriteAllText(path, id + Environment.NewLine); } catch { } }

        // -----------------------------------------------------------------
        // Edge
        // -----------------------------------------------------------------
        static string FindEdge()
        {
            string[] cands = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
            };
            foreach (var c in cands) if (File.Exists(c)) return c;
            return null;
        }

        // Encerra instancias do Edge deixadas por execucoes anteriores que ainda seguram ESTE
        // perfil/porta. Sem isso, o novo msedge.exe so encaminha para a instancia velha e sai,
        // sem abrir a porta de debug -> o bot trava em "Conectando via CDP...". Filtra pela
        // linha de comando (caminho do perfil dedicado), entao NUNCA mata um Edge pessoal.
        static void KillStaleEdge(string dataDir)
        {
            int killed = 0;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        string cmd = Convert.ToString(mo["CommandLine"]) ?? "";
                        if (cmd.IndexOf(dataDir, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try { Process.GetProcessById(Convert.ToInt32(mo["ProcessId"])).Kill(); killed++; } catch { }
                    }
                }
            }
            catch { /* WMI indisponivel: segue sem matar (o LaunchEdge ainda tenta) */ }

            // Remove locks de singleton do perfil para garantir um arranque limpo
            try { foreach (var f in Directory.GetFiles(dataDir, "Singleton*")) { try { File.Delete(f); } catch { } } }
            catch { /* perfil ainda nao existe (primeira execucao) */ }

            if (killed > 0) { Log("Limpeza: " + killed + " processo(s) Edge orfao(s) do bot encerrado(s)."); Thread.Sleep(700); }
        }

        static Process LaunchEdge(string edge, string dataDir)
        {
            var sb = new StringBuilder();
            if (_cfg.Headless) sb.Append("--headless=new ");
            sb.Append("--remote-debugging-port=" + _cfg.EdgePort + " ");
            sb.Append("--remote-allow-origins=* ");           // necessario em Chromium recente p/ aceitar o WebSocket
            sb.Append("--user-data-dir=\"" + dataDir + "\" "); // persiste sessao/cookies entre execucoes
            sb.Append("--no-first-run --no-default-browser-check --disable-extensions ");
            sb.Append("--window-size=1280,2000 ");
            sb.Append("about:blank");

            var psi = new ProcessStartInfo(edge, sb.ToString());
            psi.UseShellExecute = false;
            return Process.Start(psi);
        }

        static void Log(string m)
        { Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + m); }

        static void Dbg(string m)
        { if (_cfg != null && _cfg.Verbose) Log(m); }

        // -----------------------------------------------------------------
        // Templates de JavaScript (injetados via Runtime.evaluate)
        // -----------------------------------------------------------------
        static string BuildJs(string tpl, string id, int idx)
        { return tpl.Replace("__ID__", id).Replace("__IDX__", idx.ToString()); }
        static string BuildText(string tpl, string text)
        { return tpl.Replace("__TXT__", text.Replace("'", "\\'")); }

        // Diagnostico de pagina
        const string JS_DIAG = @"
(function(){
  var inputs=document.querySelectorAll('input'); var vis=0;
  for(var i=0;i<inputs.length;i++) if(inputs[i].offsetParent!==null) vis++;
  var bt=(document.body?document.body.innerText:'').replace(/\s+/g,' ').slice(0,200);
  return 'url='+location.href+' | title='+document.title+' | inputs='+inputs.length+' vis='+vis+' | body=['+bt+']';
})()";

        // Detecta home autenticada (tem 'Sair' e 'pontos')
        const string JS_LOGGED_IN = @"
(function(){ var t=(document.body?document.body.innerText:''); return (/Sair/.test(t) && /pontos/i.test(t)) ? 1 : 0; })()";

        // Conta posts/widgets de reacao carregados (rating_options eh o seletor confiavel)
        const string JS_FEED_COUNT = @"document.querySelectorAll('.rating_options').length";

        // Diagnostico do feed
        const string JS_FEED_DIAG = @"
(function(){
  var a=document.querySelectorAll('.social').length;
  var b=document.querySelectorAll('.rating_options').length;
  var c=document.querySelectorAll('.beforeRated').length;
  var feedTxt='';
  var els=document.querySelectorAll('*');
  for(var i=0;i<els.length;i++){
    if(els[i].children.length===0 && /Acompanhe as atividades/.test(els[i].textContent)){
      var p=els[i]; for(var k=0;k<6&&p;k++)p=p.parentElement;
      if(p) feedTxt=(p.innerText||'').replace(/\s+/g,' ').slice(0,180);
      break;
    }
  }
  return 'social='+a+' rating_options='+b+' beforeRated='+c+' feed=['+feedTxt+']';
})()";

        // CSV dos IDs ACIONAVEIS (posts NAO avaliados).
        // Sinais (robusto p/ layouts largo e estreito): o wrapper tem classe 'post_rating'
        // (vs 'post_rated' = ja avaliado) OU o coracao .beforeRated esta visivel.
        // O ID vem do token numerico de .rating_options (ex.: 'rating_options 2827971').
        const string JS_COLLECT_IDS = @"
(function(){
  var opts=document.querySelectorAll('.rating_options'); var ids=[];
  for(var i=0;i<opts.length;i++){
    var o=opts[i]; var cls=(o.className||'').split(/\s+/); var id=null;
    for(var j=0;j<cls.length;j++){ if(/^\d+$/.test(cls[j])){ id=cls[j]; break; } }
    if(!id) continue;
    var unrated=false;
    var wrap=o.parentElement?o.parentElement.parentElement:null;
    if(wrap && (' '+(wrap.className||'')+' ').indexOf(' post_rating ')>=0) unrated=true;
    var before=o.parentElement?o.parentElement.querySelector('.beforeRated'):null;
    if(before && getComputedStyle(before).display!=='none') unrated=true;
    if(unrated) ids.push(id);
  }
  return ids.join(',');
})()";

        // Centro do botao 'Carregar mais'. NA PRATICA e um ICONE de seta-circulo
        // (i.fa-chevron-circle-down), as vezes sem o texto. So existe quando ha mais posts.
        //
        // CUIDADO (descoberto na aba Social): a pagina tem VARIOS desses icones — um por
        // secao de aba (Inicio/Social compartilham 'id=newsfeed' duplicado) e ate um na
        // secao 'Atividades'. Os de abas inativas ficam OCULTOS (offsetParent===null).
        // querySelector pegava o PRIMEIRO do DOM (um oculto) e desistia -> o bot achava que
        // o feed acabou. Aqui: varremos TODOS, ficamos com os VISIVEIS e priorizamos o que
        // esta dentro do feed de posts (#newsfeed), pegando o mais ao fim da pagina.
        const string JS_CARREGAR_CENTER = @"
(function(){
  // 1) icone de seta para baixo (o botao real de carregar mais)
  var icons=document.querySelectorAll('i.fa-chevron-circle-down, i.fa-arrow-circle-down, i.fa-angle-double-down, i.fa-chevron-down, i.fa-angle-down');
  var vis=[];
  for(var i=0;i<icons.length;i++){
    var ic=icons[i]; if(ic.offsetParent===null) continue;           // ignora os de abas/secoes ocultas
    var r=ic.getBoundingClientRect(); if(r.width<=0||r.height<=0) continue;
    var inFeed=0, p=ic;
    for(var k=0;k<8&&p;k++){ if(p.id && p.id.toLowerCase().indexOf('newsfeed')>=0){ inFeed=1; break; } p=p.parentElement; }
    vis.push({ic:ic, inFeed:inFeed, top:r.top});
  }
  if(vis.length){
    // prioriza o do #newsfeed; entre varios, o mais abaixo (fim do feed carregado)
    vis.sort(function(a,b){ if(a.inFeed!==b.inFeed) return b.inFeed-a.inFeed; return b.top-a.top; });
    var best=vis[0].ic; best.scrollIntoView({block:'center'});
    var rr=best.getBoundingClientRect();
    if(rr.width>0) return Math.round(rr.left+rr.width/2)+','+Math.round(rr.top+rr.height/2);
  }
  // 2) fallback: elemento com texto 'Carregar mais'
  var els=document.querySelectorAll('a,button,div,span,li');
  for(var j=0;j<els.length;j++){
    var e=els[j]; if(e.offsetParent===null) continue;
    var t=(e.textContent||'').trim();
    if(t.indexOf('Carregar mais')===0 && t.length<25 && e.children.length<=3){
      e.scrollIntoView({block:'center'});
      var r2=e.getBoundingClientRect(); if(r2.width>0) return Math.round(r2.left+r2.width/2)+','+Math.round(r2.top+r2.height/2);
    }
  }
  return null;
})()";

        // Conta mascaras modais do ZK ATIVAS (visiveis). >0 = ha um popup (ex.: Web Check In) cobrindo
        // a pagina e bloqueando os cliques por coordenada (como o do 'Carregar mais').
        const string JS_HAS_MODAL_MASK = @"
(function(){ var m=document.querySelectorAll('.z-modal-mask'); var n=0;
  for(var i=0;i<m.length;i++){ if(getComputedStyle(m[i]).display!=='none') n++; } return n; })()";

        // Centro do modal do ZK visivel. Rola ao topo antes (o modal e position:absolute em top:100,
        // entao some da viewport quando a pagina esta rolada). Clicar no centro do card do Web Check In
        // reivindica os +50 e fecha o popup.
        const string JS_MODAL_CENTER = @"
(function(){
  var w=document.querySelector('.z-window-modal');
  if(!w || w.offsetParent===null) return null;
  window.scrollTo(0,0);
  var r=w.getBoundingClientRect();
  if(r.width<=0||r.height<=0) return null;
  return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2);
})()";

        // Fallback: esconde mascara(s)/modal(is) via JS para liberar os cliques por coordenada,
        // caso o fechamento normal (clicar no card) nao resolva.
        const string JS_HIDE_MODAL_MASK = @"
(function(){ var n=0;
  var m=document.querySelectorAll('.z-modal-mask'); for(var i=0;i<m.length;i++){ m[i].style.display='none'; n++; }
  var w=document.querySelectorAll('.z-window-modal'); for(var j=0;j<w.length;j++){ w[j].style.display='none'; }
  return n; })()";

        // Aciona o coracao __IDX__ do post __ID__ chamando seu onclick INLINE (rateClick), que envia
        // a nota ao ZK via comm.sendEvent. __IDX__ e a nota (1..N; com MaxHearts vem alto e e limitado
        // ao ultimo = maior valor = 'Amei'). Cada coracao tem onclick=rateClick(win,N,postid), entao
        // disparar .click() no coracao N submete a nota N — sem depender de coordenada/hover (os
        // coracoes ficam ocultos e o clique por pixel caia no coracao errado). Retorna 'ok',
        // 'noimg' (widget sem coracoes) ou 'notfound' (post nao esta na tela).
        const string JS_RATE = @"
(function(){
  var opts=document.querySelectorAll('.rating_options');
  for(var i=0;i<opts.length;i++){
    var o=opts[i];
    if((''+(o.className||'')).split(/\s+/).indexOf('__ID__')<0) continue;
    var imgs=o.querySelectorAll('img');
    if(imgs.length===0) return 'noimg';
    var idx=__IDX__; if(idx<1)idx=1; if(idx>imgs.length)idx=imgs.length;
    imgs[idx-1].click();
    return 'ok';
  }
  return 'notfound';
})()";

        // Pontos do cabecalho (numero formatado tipo 186.910, perto da palavra 'pontos')
        const string JS_POINTS = @"
(function(){
  var all=document.querySelectorAll('*');
  for(var i=0;i<all.length;i++){
    var e=all[i];
    if(e.children.length===0){
      var t=(e.textContent||'').trim();
      if(/^\d{1,3}(\.\d{3})+$/.test(t)){
        var ctx=''; var p=e; for(var k=0;k<4&&p;k++){ ctx+=' '+(p.textContent||''); p=p.parentElement; }
        if(/pontos|ponto/i.test(ctx)) return parseInt(t.replace(/\./g,''),10);
      }
    }
  }
  return -1;
})()";

        // Centro do campo de e-mail (input cujo rotulo proximo contem E-MAIL)
        const string JS_EMAIL_CENTER = @"
(function(){
  var ins=document.querySelectorAll('input');
  for(var i=0;i<ins.length;i++){
    var el=ins[i]; if(el.offsetParent===null) continue;
    var t='',p=el; for(var j=0;j<4&&p;j++){ t+=' '+(p.textContent||''); p=p.parentElement; }
    if(t.toUpperCase().indexOf('E-MAIL')>=0){
      el.scrollIntoView({block:'center'}); var r=el.getBoundingClientRect();
      if(r.width<=0) return null;
      return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2);
    }
  }
  return null;
})()";

        // Centro do campo de senha (ESTRITO: type=password ou placeholder contem SENHA)
        const string JS_PWD_CENTER = @"
(function(){
  var ins=document.querySelectorAll('input');
  for(var i=0;i<ins.length;i++){
    var el=ins[i]; if(el.offsetParent===null) continue;
    var ph=(el.getAttribute('placeholder')||'').toUpperCase();
    if(el.type==='password' || ph.indexOf('SENHA')>=0){
      el.scrollIntoView({block:'center'}); var r=el.getBoundingClientRect();
      if(r.width>0) return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2);
    }
  }
  return null;
})()";

        // Lista atributos dos inputs (diagnostico)
        const string JS_INPUTS = @"
(function(){
  var ins=document.querySelectorAll('input'); var out=[];
  for(var i=0;i<ins.length;i++){var el=ins[i];
    out.push('{type='+el.type+',ph='+(el.getAttribute('placeholder')||'')+',vis='+(el.offsetParent!==null)+'}');}
  return out.join(' ');
})()";

        // Clica na ABA de navegacao cujo texto == __TXT__ (ex.: 'Social'), via seu onclick
        // inline 'selectMenuItem(...)'. Retorna 'ok' se achou e clicou, senao 'notfound'.
        // Restringe aos <a> de navegacao (onclick com selectMenuItem) para nunca clicar por
        // engano em outro 'Social' do corpo da pagina. Robusto a timing: so exige o <a> no DOM.
        const string JS_CLICK_TAB = @"
(function(){
  var name='__TXT__';
  var links=document.querySelectorAll('a[onclick]');
  for(var i=0;i<links.length;i++){
    var a=links[i];
    if((a.getAttribute('onclick')||'').indexOf('selectMenuItem')<0) continue;
    if((a.textContent||'').trim()===name){ a.click(); return 'ok'; }
  }
  return 'notfound';
})()";

        // Centro de um elemento clicavel cujo texto/valor == __TXT__
        const string JS_TEXT_CENTER = @"
(function(){
  var els=document.querySelectorAll('a,button,div,span,input,li');
  for(var i=0;i<els.length;i++){
    var e=els[i]; if(e.offsetParent===null) continue;
    var et=(e.tagName==='INPUT')?(e.value||''):(e.textContent||'');
    if(e.children.length<=2 && et.trim()==='__TXT__'){
      e.scrollIntoView({block:'center'}); var r=e.getBoundingClientRect();
      if(r.width>0&&r.height>0) return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2);
    }
  }
  return null;
})()";
    }
}
