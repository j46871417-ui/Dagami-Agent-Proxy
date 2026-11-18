using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DevProxyApp
{
    public sealed class BenchProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Slot { get; set; }
        public string Template { get; set; }
        public string Host { get; set; }
        public string[] Servers { get; set; }
        public bool IsCustom { get; set; }
        public DnsEndpoint[] Endpoints {get;set;}
        public bool IsExperimental { get; set; }
        public string Note { get; set; }

        public BenchProfile()
        {
            Servers = new string[0];
        }
    }

    public sealed class BenchDomainResult
    {
        public string Host { get; set; }
        public int Ok { get; set; }
        public int Total { get; set; }
        public bool Resolved { get; set; }
        public bool TlsOk { get; set; }
        public int Answers { get; set; }
        public int Reachable { get; set; }
        public string Error { get; set; }
    }

    public sealed class BenchProfileResult
    {
        public string Id { get; set; }
        public string Slot { get; set; }
        public string Name { get; set; }
        public string Template { get; set; }
        public string[] Servers { get; set; }
        public int Attempts { get; set; }
        public int Successes { get; set; }
        public double Availability { get; set; }
        public long MedianMs { get; set; }
        public long P95Ms { get; set; }
        public long TlsMs { get; set; }
        public double Stability { get; set; }
        public double Reach { get; set; }
        public List<BenchDomainResult> Domains { get; set; }
        public bool DomainsOk { get; set; }
        public bool Alive { get; set; }
        public bool Selectable { get; set; }
        public bool Recommendable { get; set; }
        public bool Experimental { get; set; }
        public bool Best { get; set; }
        public bool BogusAnswer { get; set; }
        public bool Final { get; set; }
        public bool Truncated { get; set; }
        public string Verdict { get; set; }
        public int Score { get; set; }
        public int Rank { get; set; }
        public string Detail { get; set; }

        public BenchProfileResult()
        {
            MedianMs = -1;
            P95Ms = -1;
            TlsMs = -1;
            Verdict = "Pending";
            Detail = "";
            Servers = new string[0];
            Domains = new List<BenchDomainResult>();
        }
    }

    public sealed class BenchRun
    {
        public string Id { get; set; }
        public string StartedUtc { get; set; }
        public long DurationMs { get; set; }
        public int Attempts { get; set; }
        public string Winner { get; set; }
        public bool Cancelled { get; set; }
        public bool Truncated { get; set; }
        public List<BenchProfileResult> Results { get; set; }

        public BenchRun()
        {
            Results = new List<BenchProfileResult>();
        }
    }

    public sealed class BenchProgress
    {
        public string ProfileId { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public BenchProfileResult Partial { get; set; }
        public bool ProfileFinished { get; set; }
    }

    public static class DnsBenchmarkEngine
    {
        public static readonly string HistoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DagamiAgentProxy", "benchmark-history.json");

        private static readonly string[] Targets = new string[] {
            "cloudcode-pa.googleapis.com",
            "generativelanguage.googleapis.com"
        };

        public static List<BenchProfile> GetDefaultProfiles()
        {
            var list = new List<BenchProfile>();
            var groups=new Dictionary<string,List<DnsEndpoint>>();
            foreach(var endpoint in DnsProviders.AllBuiltInEndpoints()) {
                string id=endpoint.Provider;
                if(!groups.ContainsKey(id))groups[id]=new List<DnsEndpoint>();
                groups[id].Add(endpoint);
            }
            foreach(var group in groups) {
                var first=group.Value[0];
                string template=first.Protocol=="HTTPS" ? "https://"+first.Host+(first.Port==443?"":":"+first.Port)+first.Path :
                    (first.Protocol=="TLS"?"tls":first.Protocol.ToLowerInvariant())+"://"+first.Host+":"+first.Port;
                var addresses=new List<string>();foreach(var endpoint in group.Value)if(!addresses.Contains(endpoint.Address))addresses.Add(endpoint.Address);
                list.Add(new BenchProfile{Id=group.Key,Name=DnsProviders.ProviderName(group.Key),Slot="P"+(list.Count+1),Template=template,Host=first.Host,Servers=addresses.ToArray(),Endpoints=group.Value.ToArray(),Note="Общий обновляемый каталог Dagami"});
            }
            string current=ConfigManager.Instance==null?"":ConfigManager.Instance.DnsHost;
            if(!string.IsNullOrEmpty(current)&&DnsProviders.MatchBuiltInProvider(current)==null) {
                Uri parsed;string host=Uri.TryCreate(current,UriKind.Absolute,out parsed)?parsed.Host:current;
                list.Add(new BenchProfile{Id="custom",Name="Свой DNS",Slot="C1",Template=current,Host=host,IsCustom=true});
            }
            return list;
        }

        public static BenchRun RunBenchmark(List<BenchProfile> profiles, Action<BenchProgress> progressCallback, CancellationToken cancelToken)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var run = new BenchRun {
                Id = Guid.NewGuid().ToString("N"),
                StartedUtc = DateTime.UtcNow.ToString("o"),
                Attempts = 4
            };

            var results = new List<BenchProfileResult>();
            int finishedCount = 0;
            object sync = new object();

            var threads = new List<Thread>();
            using (var sem = new Semaphore(4, 4))
            {
                foreach (var prof in profiles)
                {
                    var p = prof;
                    var t = new Thread(() => {
                        sem.WaitOne();
                        try
                        {
                            if (cancelToken.IsCancellationRequested) return;
                            var res = MeasureProfile(p, run.Attempts, cancelToken, (prog) => {
                                if (progressCallback != null)
                                {
                                    try { progressCallback(prog); } catch { }
                                }
                            });
                            lock (sync)
                            {
                                results.Add(res);
                                finishedCount++;
                                if (progressCallback != null)
                                {
                                    var bp = new BenchProgress {
                                        ProfileId = p.Id,
                                        Done = finishedCount,
                                        Total = profiles.Count,
                                        Partial = res,
                                        ProfileFinished = true
                                    };
                                    try { progressCallback(bp); } catch { }
                                }
                            }
                        }
                        finally
                        {
                            sem.Release();
                        }
                    });
                    t.IsBackground = true;
                    threads.Add(t);
                    t.Start();
                }

                foreach (var t in threads)
                {
                    t.Join();
                }
            }

            sw.Stop();
            run.DurationMs = sw.ElapsedMilliseconds;
            run.Cancelled = cancelToken.IsCancellationRequested;
            run.Results = results;

            RankResults(run.Results);
            foreach (var r in run.Results)
            {
                if (r.Best)
                {
                    run.Winner = r.Id;
                    break;
                }
            }

            SaveHistory(run);
            return run;
        }

        private static BenchProfileResult MeasureProfile(BenchProfile p, int rounds, CancellationToken ct, Action<BenchProgress> reportPartial)
        {
            var res = new BenchProfileResult {
                Id = p.Id,
                Slot = p.Slot,
                Name = p.Name,
                Template = p.Template,
                Servers = p.Servers,
                Experimental = p.IsExperimental,
                Attempts = 0,
                Successes = 0,
                Final = false
            };

            var dohLatencies = new List<long>();
            if(p.IsCustom) {
                p.Endpoints=DnsProviders.ParseCustomSpec(p.Template);
                if(p.Endpoints==null||p.Endpoints.Length==0){res.Final=true;res.Verdict="Dead";res.Detail="Не удалось подготовить указанный DNS";return res;}
                var addresses=new List<string>();foreach(var endpoint in p.Endpoints)addresses.Add(endpoint.Address);p.Servers=addresses.ToArray();
            }
            var tlsLatencies = new List<long>();
            int totalChecks = rounds * Targets.Length;
            int successfulChecks = 0;

            var serverList = new List<string>();
            IPAddress dummyIp;
            if (p.Servers != null && p.Servers.Length > 0)
            {
                foreach (var s in p.Servers)
                {
                    if (IPAddress.TryParse(s, out dummyIp)) serverList.Add(s);
                }
            }

            if (serverList.Count == 0)
            {
                try
                {
                    var resolved = Dns.GetHostAddresses(p.Host);
                    if (resolved != null)
                    {
                        foreach (var ip in resolved)
                        {
                            if (ip.AddressFamily == AddressFamily.InterNetwork)
                                serverList.Add(ip.ToString());
                        }
                    }
                }
                catch { }
            }

            string dohPath = "/dns-query";
            try { Uri u = new Uri(p.Template); dohPath = u.PathAndQuery; } catch { }

            for (int r = 0; r < rounds; r++)
            {
                if (ct.IsCancellationRequested) { res.Truncated = true; break; }

                string currentServer = serverList.Count > 0 ? serverList[r % serverList.Count] : null;
                if (currentServer == null) break;

                DnsEndpoint ep = p.Endpoints!=null&&p.Endpoints.Length>0 ? p.Endpoints[r % p.Endpoints.Length] : new DnsEndpoint(p.Name, currentServer, p.Host, dohPath, "HTTPS", 443);

                long tlsMs = (ep.Protocol=="HTTPS"||ep.Protocol=="TLS") ? MeasureTlsHandshake(ep.Address, ep.Host, ep.Port, 2500, ct) : -1;
                if (tlsMs >= 0)
                {
                    tlsLatencies.Add(tlsMs);
                    res.TlsMs = tlsLatencies[tlsLatencies.Count / 2];
                }

                for (int tIdx = 0; tIdx < Targets.Length; tIdx++)
                {
                    if (ct.IsCancellationRequested) { res.Truncated = true; break; }
                    string targetDomain = Targets[tIdx];

                    res.Attempts++;
                    byte[] query = DnsHttpsTransport.Question(targetDomain);
                    var timer = System.Diagnostics.Stopwatch.StartNew();

                    byte[] answer = null;
                    try
                    {
                        answer = DnsHttpsTransport.Exchange(ep, query, 3000, false);
                    }
                    catch (Exception)
                    {
                        answer = null;
                    }
                    timer.Stop();

                    if (answer != null && answer.Length >= 12 && DnsHttpsTransport.SameQuestion(query, answer))
                    {
                        List<IPAddress> addrs;
                        try { addrs=ValidAddresses(query,answer,targetDomain); }
                        catch { if(AnswerSanity.ExtractAddresses(answer).Exists(AnswerSanity.IsBogus))res.BogusAnswer=true;continue; }
                        if(addrs.Count==0 || (answer[3]&15)!=0)continue;
                        bool isBogus = false;
                        for (int aIdx = 0; aIdx < addrs.Count; aIdx++)
                        {
                            if (AnswerSanity.IsBogus(addrs[aIdx])) { isBogus = true; break; }
                        }

                        if (isBogus)
                        {
                            res.BogusAnswer = true;
                        }
                        else
                        {
                            successfulChecks++;
                            res.Successes++;
                            res.Alive = true;
                            dohLatencies.Add(timer.ElapsedMilliseconds);
                        }
                    }

                    if (reportPartial != null)
                    {
                        reportPartial(new BenchProgress {
                            ProfileId = p.Id,
                            Done = res.Attempts,
                            Total = totalChecks,
                            Partial = res,
                            ProfileFinished = false
                        });
                    }
                }

                if (!res.Alive && r >= 1) break;
            }

            res.Final = true;
            res.Availability = res.Attempts > 0 ? Math.Round((double)successfulChecks / res.Attempts, 3) : 0;

            dohLatencies.Sort();
            if (dohLatencies.Count > 0)
            {
                res.MedianMs = dohLatencies[dohLatencies.Count / 2];
                res.P95Ms = dohLatencies[(int)Math.Min(dohLatencies.Count - 1, Math.Ceiling(0.95 * dohLatencies.Count) - 1)];
            }

            tlsLatencies.Sort();
            if (tlsLatencies.Count > 0)
            {
                res.TlsMs = tlsLatencies[tlsLatencies.Count / 2];
            }

            res.DomainsOk = false;
            if(res.Alive&&!res.BogusAnswer&&!ct.IsCancellationRequested) {
                string failure=AutomaticAiRoute.ProbeAntigravity(new AiRoute(p.Name,null,p.Template),new HashSet<IPAddress>());
                res.DomainsOk=failure==null;
                res.Detail=failure==null ? "Сетевые проверки API прошли; ответ модели и аккаунт не проверены." : failure;
            }
            res.Stability = res.Availability >= 0.9 ? 1.0 : res.Availability >= 0.7 ? 0.8 : 0.5;
            res.Reach = res.Alive ? 1.0 : 0.0;

            if (res.BogusAnswer)
            {
                res.Verdict = "Bogus";
                res.Score = 0;
                res.Selectable = false;
                res.Recommendable = false;
                res.Alive = false;
                res.Detail = "Провайдер подменил DNS-ответы (заглушка РКН или локальный IP)";
            }
            else if (!res.Alive)
            {
                res.Verdict = "Dead";
                res.Score = 0;
                res.Selectable = false;
                res.Recommendable = false;
            }
            else if (res.Availability < 0.75)
            {
                res.Verdict = "Flaky";
                res.Score = (int)(res.Availability * 50);
                res.Selectable = true;
                res.Recommendable = false;
            }
            else if (res.MedianMs > 600)
            {
                res.Verdict = "Slow";
                res.Score = (int)(res.Availability * 70);
                res.Selectable = true;
                res.Recommendable = !res.Experimental;
            }
            else
            {
                res.Verdict = "Good";
                double latScore = Math.Max(0, 1.0 - (double)res.MedianMs / 1000.0);
                res.Score = (int)Math.Round(50 * res.Availability + 50 * latScore);
                res.Selectable = true;
                res.Recommendable = true;
            }

            if(!res.DomainsOk||ct.IsCancellationRequested||res.Truncated) {
                res.Recommendable=false;res.Best=false;
                if(res.Alive)res.Verdict="Partial";
            }
            if(p.IsExperimental)res.Recommendable=false;
            return res;
        }

        internal static List<IPAddress> ValidAddresses(byte[] query,byte[] answer,string host) {
            if(query==null||query.Length<12||answer==null||answer.Length<12||!DnsHttpsTransport.SameQuestion(query,answer))throw new InvalidDataException("DNS response mismatch");
            int ttl;
            var address=ScopedDnsResolver.ParseAnswer(answer,new byte[]{query[0],query[1]},host,out ttl);
            var addresses=AnswerSanity.ExtractAddresses(answer);
            if(addresses.Count==0||addresses.Exists(AnswerSanity.IsBogus))throw new InvalidDataException("Invalid public DNS answer");
            return addresses;
        }

        private static long MeasureTlsHandshake(string ipOrHost, string sniHost, int port, int timeoutMs, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (var tcp = new TcpClient())
                {
                    var ar = tcp.BeginConnect(ipOrHost, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { tcp.Close(); return -1; }
                    tcp.EndConnect(ar);
                    sw.Restart();
                    if (ct.IsCancellationRequested) return -1;

                    using (var ssl = new SslStream(tcp.GetStream(), false))
                    {
                        IAsyncResult sslAr;
                        try
                        {
                            // Tls12 (3072) | Tls13 (12288)
                            SslProtocols modernProtocols = SslProtocols.Tls12;
                            sslAr = ssl.BeginAuthenticateAsClient(sniHost, null, modernProtocols, false, null, null);
                        }
                        catch
                        {
                            sslAr = ssl.BeginAuthenticateAsClient(sniHost, null, SslProtocols.Tls12, false, null, null);
                        }

                        if (!sslAr.AsyncWaitHandle.WaitOne(timeoutMs)) return -1;
                        ssl.EndAuthenticateAsClient(sslAr);
                        sw.Stop();
                        return sw.ElapsedMilliseconds;
                    }
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void RankResults(List<BenchProfileResult> results)
        {
            results.Sort((a, b) => {
                if (a.BogusAnswer != b.BogusAnswer) return a.BogusAnswer.CompareTo(b.BogusAnswer);
                if (a.Recommendable != b.Recommendable) return b.Recommendable.CompareTo(a.Recommendable);
                if (a.Selectable != b.Selectable) return b.Selectable.CompareTo(a.Selectable);
                if ((a.Verdict == "Good") != (b.Verdict == "Good")) return (b.Verdict == "Good").CompareTo(a.Verdict == "Good");
                if (a.Verdict == "Good" && b.Verdict == "Good")
                {
                    long ma = a.MedianMs < 0 ? long.MaxValue : a.MedianMs;
                    long mb = b.MedianMs < 0 ? long.MaxValue : b.MedianMs;
                    if (ma != mb) return ma.CompareTo(mb);
                }
                if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < results.Count; i++)
            {
                results[i].Rank = i + 1;
                results[i].Best = (i == 0 && results[i].Recommendable && (results[i].Verdict == "Good"||results[i].Verdict=="Slow"));
            }
        }

        public static void SaveHistory(BenchRun run)
        {
            try
            {
                string dir = Path.GetDirectoryName(HistoryPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var ser = new JavaScriptSerializer();
                string json = ser.Serialize(run);
                File.WriteAllText(HistoryPath, json, Encoding.UTF8);
            }
            catch { }
        }

        public static BenchRun LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return null;
                string json = File.ReadAllText(HistoryPath, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                return ser.Deserialize<BenchRun>(json);
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class BenchRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Slot { get; set; }
        public string Note { get; set; }
        public BenchProfileResult Result { get; set; }
        public bool Running { get; set; }
        public bool Selected { get; set; }
    }

    public sealed class BenchTable : Control
    {
        public readonly List<BenchRow> Rows = new List<BenchRow>();
        public float Progress = -1f;

        private int sortColumn = -1;
        private bool sortDescending = false;
        private int focusedRow = 0;
        private int hoveredRow = -1;

        public event Action<BenchRow> RowClicked;

        public BenchTable()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            BackColor = FluentTheme.Surface;
            AccessibleRole = AccessibleRole.Table;
            AccessibleName = "Таблица замера DNS-профилей";
            TabStop=true;
        }

        protected override bool IsInputKey(Keys key){return key==Keys.Up||key==Keys.Down||base.IsInputKey(key);}
        protected override void OnKeyDown(KeyEventArgs e){
            var rows=DisplayRows;
            if(rows.Length>0&&(e.KeyCode==Keys.Up||e.KeyCode==Keys.Down||e.KeyCode==Keys.Enter)){
                if(e.KeyCode==Keys.Up)focusedRow=Math.Max(0,focusedRow-1);
                if(e.KeyCode==Keys.Down)focusedRow=Math.Min(rows.Length-1,focusedRow+1);
                if(RowClicked!=null)RowClicked(rows[focusedRow]);
                AccessibleDescription=rows[focusedRow].Name+": "+(rows[focusedRow].Result==null?"не измерен":rows[focusedRow].Result.Verdict);
                Invalidate();e.Handled=true;
            }
            base.OnKeyDown(e);
        }

        public BenchRow[] DisplayRows
        {
            get
            {
                var copy = new List<BenchRow>(Rows);
                if (sortColumn < 0) return copy.ToArray();

                copy.Sort((a, b) => {
                    int dir = sortDescending ? -1 : 1;
                    if (sortColumn == 0) // Rank
                    {
                        int ra = a.Result != null && a.Result.Rank > 0 ? a.Result.Rank : 999;
                        int rb = b.Result != null && b.Result.Rank > 0 ? b.Result.Rank : 999;
                        return ra.CompareTo(rb) * dir;
                    }
                    if (sortColumn == 1) // Name
                    {
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) * dir;
                    }
                    if (sortColumn == 2) // Availability
                    {
                        double aa = a.Result != null ? a.Result.Availability : -1;
                        double ab = b.Result != null ? b.Result.Availability : -1;
                        return aa.CompareTo(ab) * dir;
                    }
                    if (sortColumn == 3) // Median
                    {
                        long ma = a.Result != null && a.Result.MedianMs >= 0 ? a.Result.MedianMs : 999999;
                        long mb = b.Result != null && b.Result.MedianMs >= 0 ? b.Result.MedianMs : 999999;
                        return ma.CompareTo(mb) * dir;
                    }
                    if (sortColumn == 4) // TLS
                    {
                        long ta = a.Result != null && a.Result.TlsMs >= 0 ? a.Result.TlsMs : 999999;
                        long tb = b.Result != null && b.Result.TlsMs >= 0 ? b.Result.TlsMs : 999999;
                        return ta.CompareTo(tb) * dir;
                    }
                    return 0;
                });
                return copy.ToArray();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int headerHeight = 32;
            int rowHeight = 44;
            int newHover = -1;
            if (e.Y >= headerHeight)
            {
                int r = (e.Y - headerHeight) / rowHeight;
                var list = DisplayRows;
                if (r >= 0 && r < list.Length) newHover = r;
            }
            if (newHover != hoveredRow)
            {
                hoveredRow = newHover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoveredRow != -1)
            {
                hoveredRow = -1;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int headerHeight = 32;
            if (e.Y < headerHeight)
            {
                int[] colX = GetColumnOffsets();
                for (int i = 0; i < colX.Length; i++)
                {
                    int x1 = colX[i];
                    int x2 = (i < colX.Length - 1) ? colX[i + 1] : Width;
                    if (e.X >= x1 && e.X < x2)
                    {
                        if (sortColumn == i) sortDescending = !sortDescending;
                        else { sortColumn = i; sortDescending = false; }
                        Invalidate();
                        return;
                    }
                }
            }
            else
            {
                int rowHeight = 44;
                int r = (e.Y - headerHeight) / rowHeight;
                var list = DisplayRows;
                if (r >= 0 && r < list.Length)
                {
                    focusedRow = r;
                    if (RowClicked != null) RowClicked(list[r]);
                    Invalidate();
                }
            }
        }

        private int[] GetColumnOffsets()
        {
            int w = Width;
            return new int[] {
                12,                   // # (Rank)
                56,                   // Профиль
                Math.Max(180, w - 240), // Доступность
                Math.Max(260, w - 150), // Задержка
                Math.Max(320, w - 75)   // TLS
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var bgBrush = new SolidBrush(FluentTheme.Surface))
            {
                g.FillRectangle(bgBrush, ClientRectangle);
            }

            int headerHeight = 32;
            int[] colX = GetColumnOffsets();
            string[] headers = new[] { "#", "Профиль", "Доступность", "Задержка", "TLS" };

            using (var headerBg = new SolidBrush(FluentTheme.Sidebar))
            {
                g.FillRectangle(headerBg, 0, 0, Width, headerHeight);
            }
            using (var linePen = new Pen(FluentTheme.Border))
            {
                g.DrawLine(linePen, 0, headerHeight - 1, Width, headerHeight - 1);
            }

            using (var headerFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var headerBrush = new SolidBrush(FluentTheme.Muted))
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    string text = headers[i];
                    if (sortColumn == i) text += sortDescending ? " ▾" : " ▴";
                    int x = colX[i];
                    int w = (i < colX.Length - 1 ? colX[i + 1] : Width) - x;
                    var rect = new Rectangle(x, 0, w, headerHeight);
                    TextRenderer.DrawText(g, text, headerFont, rect, FluentTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                }
            }

            if (Progress >= 0f && Progress <= 1f)
            {
                int barH = 3;
                using (var track = new SolidBrush(Color.FromArgb(60, FluentTheme.Primary)))
                {
                    g.FillRectangle(track, 0, headerHeight - barH, Width, barH);
                }
                using (var fill = new SolidBrush(FluentTheme.Primary))
                {
                    g.FillRectangle(fill, 0, headerHeight - barH, (int)(Width * Progress), barH);
                }
            }

            var rows = DisplayRows;
            int rowHeight = 44;
            int y = headerHeight;

            using (var nameFont = new Font("Segoe UI", 9.5f, FontStyle.Regular))
            using (var boldFont = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var smallFont = new Font("Segoe UI", 8f))
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    var r = rows[i];
                    var rowRect = new Rectangle(0, y, Width, rowHeight);

                    bool isBest = r.Result != null && r.Result.Best;
                    if (isBest)
                    {
                        using (var bestBg = new SolidBrush(Color.FromArgb(24, 46, 175, 125)))
                        {
                            g.FillRectangle(bestBg, rowRect);
                        }
                        using (var bestPen = new Pen(Color.FromArgb(46, 175, 125), 1.5f))
                        {
                            g.DrawRectangle(bestPen, 1, y, Width - 2, rowHeight - 1);
                        }
                    }
                    else if (r.Selected)
                    {
                        using (var selBrush = new SolidBrush(Color.FromArgb(20, FluentTheme.Primary)))
                        {
                            g.FillRectangle(selBrush, rowRect);
                        }
                        using (var selPen = new Pen(FluentTheme.Primary, 1.2f))
                        {
                            g.DrawRectangle(selPen, 1, y, Width - 2, rowHeight - 1);
                        }
                    }
                    else if (i == hoveredRow)
                    {
                        using (var hovBrush = new SolidBrush(Color.FromArgb(15, FluentTheme.Text)))
                        {
                            g.FillRectangle(hovBrush, rowRect);
                        }
                    }

                    using (var borderPen = new Pen(FluentTheme.Border))
                    {
                        g.DrawLine(borderPen, 0, y + rowHeight - 1, Width, y + rowHeight - 1);
                    }

                    // Rank badge
                    int rank = r.Result != null && r.Result.Rank > 0 ? r.Result.Rank : i + 1;
                    Color rankColor = isBest ? Color.FromArgb(46, 175, 125) : FluentTheme.Muted;
                    var rankRect = new Rectangle(colX[0], y, colX[1] - colX[0], rowHeight);
                    TextRenderer.DrawText(g, "#" + rank, boldFont, rankRect, rankColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

                    // Name + Slot + Verdict
                    int nameX = colX[1];
                    int nameW = colX[2] - nameX - 10;
                    string displayName = r.Name;
                    if (isBest) displayName += " ★ Лучший";
                    else if (r.Selected) displayName += " (выбран в таблице)";
                    TextRenderer.DrawText(g, displayName, boldFont, new Rectangle(nameX, y + 4, nameW, 18), isBest ? Color.FromArgb(46, 175, 125) : FluentTheme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

                    string verdict = r.Result != null ? GetVerdictText(r.Result.Verdict) : (r.Running ? "Измеряю…" : "Не измерен");
                    Color vColor = r.Result != null ? GetVerdictColor(r.Result.Verdict) : (r.Running ? FluentTheme.Primary : FluentTheme.Muted);
                    string slotInfo = (!string.IsNullOrEmpty(r.Slot) ? r.Slot + " · " : "") + verdict;
                    TextRenderer.DrawText(g, slotInfo, smallFont, new Rectangle(nameX, y + 24, nameW, 16), vColor, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

                    // Availability
                    int availX = colX[2];
                    int availW = colX[3] - availX - 10;
                    if (r.Result != null && r.Result.Attempts > 0)
                    {
                        int pct = (int)Math.Round(r.Result.Availability * 100);
                        string availText = pct + "%";
                        TextRenderer.DrawText(g, availText, nameFont, new Rectangle(availX, y + 6, availW, 16), FluentTheme.Text, TextFormatFlags.Left);

                        int barY = y + 26;
                        int barTotalW = Math.Min(60, availW);
                        int barFillW = (int)(barTotalW * r.Result.Availability);
                        using (var track = new SolidBrush(Color.FromArgb(40, FluentTheme.Muted)))
                        {
                            g.FillRectangle(track, availX, barY, barTotalW, 6);
                        }
                        using (var bar = new SolidBrush(vColor))
                        {
                            g.FillRectangle(bar, availX, barY, barFillW, 6);
                        }
                    }
                    else
                    {
                        TextRenderer.DrawText(g, "—", nameFont, new Rectangle(availX, y, availW, rowHeight), FluentTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                    }

                    // Median Latency
                    int latX = colX[3];
                    int latW = colX[4] - latX - 10;
                    if (r.Result != null && r.Result.MedianMs >= 0)
                    {
                        string latText = r.Result.MedianMs + " мс";
                        TextRenderer.DrawText(g, latText, boldFont, new Rectangle(latX, y + 5, latW, 18), FluentTheme.Text, TextFormatFlags.Left);
                        if (r.Result.P95Ms >= 0)
                        {
                            string p95 = "p95: " + r.Result.P95Ms + " мс";
                            TextRenderer.DrawText(g, p95, smallFont, new Rectangle(latX, y + 24, latW, 16), FluentTheme.Muted, TextFormatFlags.Left);
                        }
                    }
                    else
                    {
                        TextRenderer.DrawText(g, "—", nameFont, new Rectangle(latX, y, latW, rowHeight), FluentTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                    }

                    // TLS Handshake
                    int tlsX = colX[4];
                    int tlsW = Width - tlsX - 10;
                    if (r.Result != null && r.Result.TlsMs >= 0)
                    {
                        string tlsText = r.Result.TlsMs + " мс";
                        TextRenderer.DrawText(g, tlsText, nameFont, new Rectangle(tlsX, y, tlsW, rowHeight), FluentTheme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                    }
                    else
                    {
                        TextRenderer.DrawText(g, "—", nameFont, new Rectangle(tlsX, y, tlsW, rowHeight), FluentTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                    }

                    y += rowHeight;
                }
            }
        }

        private static string GetVerdictText(string v)
        {
            if (v == "Good") return "Работает";
            if (v == "Slow") return "Медленно";
            if (v == "Flaky") return "Нестабильно";
            if (v == "Partial") return "Частично";
            if (v == "Bogus") return "Подмена ответов (заглушка)";
            if (v == "Dead") return "Недоступен";
            if (v == "NotMeasured") return "Не измерен";
            return "Измеряю…";
        }

        private static Color GetVerdictColor(string v)
        {
            if (v == "Good") return Color.FromArgb(46, 175, 125);
            if (v == "Slow") return Color.FromArgb(230, 160, 40);
            if (v == "Flaky" || v == "Partial") return Color.FromArgb(220, 120, 50);
            if (v == "Bogus" || v == "Dead") return Color.FromArgb(220, 60, 60);
            return FluentTheme.Muted;
        }
    }

    public partial class MainForm
    {
        private BenchTable _benchTable;
        private Label _benchStatusLabel;
        private FluentButton _benchRunButton;
        private FluentButton _benchBestButton;
        private CancellationTokenSource _benchCts;
        private bool _benchRunning;
        private BenchRun _lastBenchRun;
        private List<BenchRow> _benchRows = new List<BenchRow>();
        private DateTime _benchCooldownUntil = DateTime.MinValue;

        internal Panel BuildBenchPage()
        {
            var host = new FluentPageHost { Dock = DockStyle.Fill, AutoScroll = true, BackColor = FluentTheme.Background };
            var page = new FluentLayout { Dock = DockStyle.Top, Height = 550, ColumnCount = 1, RowCount = 2, Padding = new Padding(4, 4, 8, 4) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 380));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));

            var tableCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 0, 0, 8) };
            var tableLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var tableTitle = FluentLabel("Замер DoH/DNS профилей", 15, true);
            tableLayout.Controls.Add(tableTitle, 0, 0);

            _benchTable = new BenchTable { Dock = DockStyle.Fill, Name = "BenchTableControl" };
            _benchTable.RowClicked += OnBenchRowClicked;
            tableLayout.Controls.Add(_benchTable, 0, 1);
            tableCard.Controls.Add(tableLayout);
            page.Controls.Add(tableCard, 0, 0);

            var actionCard = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 0, 0, 8) };
            var actionRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            actionRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actionRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            actionRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            _benchStatusLabel = new Label {
                Name = "BenchStatusLabel",
                Dock = DockStyle.Fill,
                ForeColor = FluentTheme.Muted,
                Font = new Font("Segoe UI", 9f),
                Text = "Проверяет ответы DNS и доступ к API. Настройки сети не меняет."
            };
            actionRows.Controls.Add(_benchStatusLabel, 0, 0);

            var buttonFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            _benchRunButton = new FluentButton {
                Name = "BenchRunButton",
                Text = "Измерить",
                Primary = true,
                Width = 140,
                Height = 38
            };
            _benchRunButton.Click += (s, e) => ToggleBenchmark();
            buttonFlow.Controls.Add(_benchRunButton);

            _benchBestButton = new FluentButton {
                Name = "BenchBestButton",
                Text = "Выбрать лучший",
                Enabled = false,
                Width = 160,
                Height = 38,
                Margin = new Padding(8, 0, 0, 0)
            };
            _benchBestButton.Click += (s, e) => SelectBestProfile();
            buttonFlow.Controls.Add(_benchBestButton);

            var toDnsBtn = new FluentButton {
                Name = "BenchToDnsButton",
                Text = "Настройки DNS →",
                Width = 150,
                Height = 38,
                Margin = new Padding(8, 0, 0, 0)
            };
            toDnsBtn.Click += (s, e) => ShowFluentPage(2);
            buttonFlow.Controls.Add(toDnsBtn);

            actionRows.Controls.Add(buttonFlow, 0, 1);
            actionCard.Controls.Add(actionRows);
            page.Controls.Add(actionCard, 0, 1);

            host.Controls.Add(page);

            RebuildBenchRows();
            LoadBenchmarkHistory();

            return host;
        }

        private void RebuildBenchRows()
        {
            _benchRows.Clear();
            var profiles = DnsBenchmarkEngine.GetDefaultProfiles();
            string curDns = ConfigManager.Instance != null ? (ConfigManager.Instance.DnsHost ?? "") : "";
            foreach (var p in profiles)
            {
                bool selected = string.Equals(curDns, p.Host, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(curDns, p.Template, StringComparison.OrdinalIgnoreCase);
                _benchRows.Add(new BenchRow {
                    Id = p.Id,
                    Name = p.Name,
                    Slot = p.Slot,
                    Note = p.Note,
                    Selected = selected
                });
            }
            if (_benchTable != null)
            {
                _benchTable.Rows.Clear();
                _benchTable.Rows.AddRange(_benchRows);
                _benchTable.Invalidate();
            }
        }

        private void OnBenchRowClicked(BenchRow row)
        {
            if (row == null) return;
            foreach (var r in _benchRows) r.Selected = (r.Id == row.Id);
            if (_benchTable != null) _benchTable.Invalidate();
        }

        private void LoadBenchmarkHistory()
        {
            try
            {
                var run = DnsBenchmarkEngine.LoadHistory();
                if (run != null && run.Results != null && run.Results.Count > 0)
                {
                    _lastBenchRun = run;
                    ApplyBenchRunResults(run);
                    DateTime dt;
                    string when = DateTime.TryParse(run.StartedUtc, out dt) ? dt.ToLocalTime().ToString("dd.MM HH:mm") : "?";
                    _benchStatusLabel.Text = "Прошлый замер от " + when + ". Сеть могла измениться — измерьте заново.";
                    _benchBestButton.Enabled = false;
                }
            }
            catch { }
        }

        private void ApplyBenchRunResults(BenchRun run)
        {
            foreach (var row in _benchRows)
            {
                row.Running = false;
                BenchProfileResult found = null;
                foreach (var res in run.Results)
                {
                    if (res.Id == row.Id) { found = res; break; }
                }
                row.Result = found;
            }

            _benchRows.Sort((a, b) => {
                int ra = a.Result != null && a.Result.Rank > 0 ? a.Result.Rank : 999;
                int rb = b.Result != null && b.Result.Rank > 0 ? b.Result.Rank : 999;
                return ra.CompareTo(rb);
            });

            if (_benchTable != null)
            {
                _benchTable.Rows.Clear();
                _benchTable.Rows.AddRange(_benchRows);
                _benchTable.Invalidate();
            }

            BenchProfileResult best = null;
            foreach (var r in run.Results)
            {
                if (r.Best) { best = r; break; }
            }

            if (run.Cancelled)
            {
                _benchBestButton.Enabled=false;
                _benchStatusLabel.Text = "Замер отменён пользователем. Показаны полученные результаты.";
            }
            else if (best != null)
            {
                _benchStatusLabel.Text = "Лучший: " + best.Name + " (" + best.Slot + ") · " +
                    best.Score + " баллов · задержка " + (best.MedianMs >= 0 ? best.MedianMs + " мс" : "—") +
                    " · доступность " + (int)(best.Availability * 100) + "%.";
                _benchBestButton.Enabled = true;
            }
            else
            {
                _benchStatusLabel.Text = "Подходящего профиля нет: ни один не прошёл проверку всех целевых доменов.";
                _benchBestButton.Enabled = false;
            }
        }

        private void ToggleBenchmark()
        {
            if (_benchRunning)
            {
                if (_benchCts != null)
                {
                    try { _benchCts.Cancel(); } catch { }
                    _benchStatusLabel.Text = "Отменяю замер…";
                }
                return;
            }

            if (DateTime.UtcNow < _benchCooldownUntil)
            {
                int wait = Math.Max(1, (int)(_benchCooldownUntil - DateTime.UtcNow).TotalSeconds);
                _benchStatusLabel.Text = "Повторный замер возможен через " + wait + " с.";
                return;
            }

            _benchRunning = true;
            _benchRunButton.Text = "Отмена";
            _benchBestButton.Enabled = false;
            if (_benchTable != null)
            {
                _benchTable.Progress = 0f;
                foreach (var row in _benchRows)
                {
                    row.Result = null;
                    row.Running = true;
                }
                _benchTable.Invalidate();
            }

            _benchCts = new CancellationTokenSource();
            var ct = _benchCts.Token;

            var profiles = DnsBenchmarkEngine.GetDefaultProfiles();
            int total = profiles.Count;
            _benchStatusLabel.Text = "Идёт замер: 0 из " + total + " профилей (DoH + TLS). «Отмена» остановит замер.";

            ThreadPool.QueueUserWorkItem(state => {
                BenchRun run = null;
                try
                {
                    run = DnsBenchmarkEngine.RunBenchmark(profiles, progress => {
                        try
                        {
                            BeginInvoke(new Action(() => {
                                if (IsDisposed) return;
                                foreach (var r in _benchRows)
                                {
                                    if (r.Id == progress.ProfileId)
                                    {
                                        r.Result = progress.Partial;
                                        r.Running = !progress.ProfileFinished;
                                        break;
                                    }
                                }
                                if (progress.ProfileFinished && _benchTable != null)
                                {
                                    _benchTable.Progress = total > 0 ? (float)progress.Done / total : 0f;
                                    _benchStatusLabel.Text = "Идёт замер: " + progress.Done + " из " + total + " профилей (DoH + TLS)…";
                                }
                                if (_benchTable != null) _benchTable.Invalidate();
                            }));
                        }
                        catch { }
                    }, ct);
                }
                catch (Exception ex)
                {
                    Logger.Log("[Benchmark] " + ex.Message);
                }

                try
                {
                    BeginInvoke(new Action(() => {
                        if (IsDisposed) return;
                        _benchRunning = false;
                        _benchRunButton.Text = "Измерить";
                        if (_benchTable != null) _benchTable.Progress = -1f;
                        _benchCooldownUntil = DateTime.UtcNow.AddSeconds(3);

                        if (run != null)
                        {
                            _lastBenchRun = run;
                            ApplyBenchRunResults(run);
                        }
                        else
                        {
                            _benchStatusLabel.Text = "Замер завершён или отменён.";
                        }
                    }));
                }
                catch { }
            });
        }

        private void SelectBestProfile()
        {
            if (_lastBenchRun == null || string.IsNullOrEmpty(_lastBenchRun.Winner)) return;
            DateTime started;
            if(_lastBenchRun.Cancelled||!DateTime.TryParse(_lastBenchRun.StartedUtc,out started)||DateTime.UtcNow-started.ToUniversalTime()>TimeSpan.FromMinutes(10)){_benchBestButton.Enabled=false;_benchStatusLabel.Text="Результат устарел или замер отменён. Измерьте заново.";return;}
            string winnerId = _lastBenchRun.Winner;
            BenchProfileResult winnerResult = null;
            foreach (var r in _lastBenchRun.Results)
            {
                if (r.Id == winnerId) { winnerResult = r; break; }
            }
            if (winnerResult == null) return;

            int presetIdx = -1;
            for (int i = 0; i < BuiltInDnsPresets.Length - 1; i++)
            {
                string presetSpec = BuiltInDnsPresets[i][1];
                if (string.Equals(presetSpec, winnerResult.Template, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(presetSpec, winnerResult.Id, StringComparison.OrdinalIgnoreCase) ||
                    (winnerResult.Servers != null && winnerResult.Servers.Length > 0 && presetSpec.Contains(winnerResult.Servers[0])) ||
                    winnerResult.Name.IndexOf(BuiltInDnsPresets[i][0], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    presetIdx = i;
                    break;
                }
            }

            if (presetIdx >= 0)
            {
                _dnsQuickPresetBox.SelectedIndex = presetIdx;
            }
            else
            {
                _dnsQuickPresetBox.SelectedIndex = BuiltInDnsPresets.Length - 1;
                _dnsCustomSource.Text = winnerResult.Template;
            }

            _dnsDraftEnabled.Checked = true;
            SaveDnsPreference();
            ShowFluentPage(2);
            MessageBox.Show(this, "По результатам замера выбран лучший профиль:\r\n" + winnerResult.Name +
                " (медиана: " + (winnerResult.MedianMs >= 0 ? winnerResult.MedianMs + " мс" : "—") + ", доступность: " + (int)(winnerResult.Availability * 100) + "%).\r\nНастройки сохранены.",
                "Выбор лучшего DNS", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
