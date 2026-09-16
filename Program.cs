using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;

public class NetworkEngine
{
    private readonly HashSet<string> _visitedUrls = new HashSet<string>();
    private readonly List<string> _urlsQueue = new List<string>();
    private readonly HttpClient _client;
    private readonly byte[] _dnsQueryPacket;

    private byte _ip1 = 1, _ip2 = 0, _ip3 = 0, _ip4 = 0;

    public int QueueCount
    {
        get { lock (_urlsQueue) { return _urlsQueue.Count; } }
    }

    public NetworkEngine()
    {
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _client.Timeout = TimeSpan.FromSeconds(4);

        _dnsQueryPacket = new byte[]
        {
            0xAA, 0xBB, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01
        };
    }

    public void AddManualUrl(string url)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;
        lock (_urlsQueue) { _urlsQueue.Add(url); }
    }

    public string GetNextSequentialIp()
    {
        lock (this)
        {
            while (true)
            {
                _ip4++;
                if (_ip4 == 255) { _ip4 = 0; _ip3++; } 
                if (_ip3 == 255) { _ip3 = 0; _ip2++; }
                if (_ip2 == 255) { _ip2 = 0; _ip1++; }
                if (_ip1 >= 224) { _ip1 = 1; _ip2 = 0; _ip3 = 0; _ip4 = 0; }

                if (_ip1 == 127 || _ip1 == 192 || _ip1 == 10 || _ip1 == 0) continue;

                return $"{_ip1}.{_ip2}.{_ip3}.{_ip4}";
            }
        }
    }

    public async Task<string> ScanTheWorldForDnsAsync(int workerId)
    {
        string targetIp = GetNextSequentialIp();

        lock (Console.Out)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Поток-{workerId}] Тест по UDP IP: {targetIp} -> порт 53...");
            Console.ResetColor();
        }

        try
        {
            using (UdpClient udpClient = new UdpClient())
            {
                udpClient.Client.SendTimeout = 400;
                udpClient.Client.ReceiveTimeout = 400;
                IPEndPoint ep = new IPEndPoint(IPAddress.Parse(targetIp), 53);

                await udpClient.SendAsync(_dnsQueryPacket, _dnsQueryPacket.Length, ep);
                var receiveTask = udpClient.ReceiveAsync();

                if (await Task.WhenAny(receiveTask, Task.Delay(400)) == receiveTask)
                {
                    var res = await receiveTask;
                    if (res.Buffer.Length > 10)
                    {
                        lock (Console.Out)
                        {
                            Console.ForegroundColor = ConsoleColor.Magenta;
                            Console.WriteLine($"\n[🔥 РАДАР DNS] Поток-{workerId} НАШЁЛ СЕРВЕР: {targetIp}!");
                            Console.ResetColor();
                        }
                        return targetIp;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<string> DigDomainFromRandomIpAsync(int workerId)
    {
        string targetIpStr = GetNextSequentialIp();
        IPAddress targetIp = IPAddress.Parse(targetIpStr);

        lock (Console.Out)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Поток-{workerId}] Запрос DNS PTR для IP: {targetIp}...");
            Console.ResetColor();
        }

        try
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(targetIp);
            string host = entry.HostName.ToLower().Trim().TrimEnd('.');

            if (!string.IsNullOrWhiteSpace(host) && !Regex.IsMatch(host, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$") && host.Contains("."))
            {
                string fullUrl = "https://" + host;
                lock (_visitedUrls)
                {
                    if (!_visitedUrls.Contains(fullUrl))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"\n[🟢 DNS СЛИВ] Поток-{workerId} извлёк сайт: {host} (IP: {targetIp})");
                        Console.ResetColor();
                        return fullUrl;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<string[]> CrawlAndExtractLinksAsync(string targetUrl, int workerId)
    {
        lock (_visitedUrls) { _visitedUrls.Add(targetUrl); }
        try
        {
            string html = await _client.GetStringAsync(targetUrl);
            HtmlDocument doc = new HtmlDocument();
            doc.LoadHtml(html);

            var anchorNodes = doc.DocumentNode.SelectNodes("//a[@href]");
            if (anchorNodes != null)
            {
                int linksAdded = 0;
                foreach (var node in anchorNodes)
                {
                    string href = node.GetAttributeValue("href", "");
                    if (href.Contains('#')) href = href.Split('#')[0];
                    href = href.Trim();

                    if (href.StartsWith("/"))
                    {
                        try { href = new Uri(new Uri(targetUrl), href).AbsoluteUri; } catch { continue; }
                    }

                    if (href.StartsWith("http"))
                    {
                        lock (_visitedUrls)
                        {
                            lock (_urlsQueue)
                            {
                                if (!_visitedUrls.Contains(href) && !_urlsQueue.Contains(href))
                                {
                                    _urlsQueue.Add(href);
                                    linksAdded++;
                                }
                            }
                        }
                    }
                }
                lock (Console.Out)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"[Поток-{workerId}] Найдено подссылок: +{linksAdded}. ");
                    Console.ResetColor();
                }
            }

            string cleanText = doc.DocumentNode.InnerText;
            cleanText = Regex.Replace(cleanText, @"[~`@#\$%\^&\*\(\)\-_\+=\[{\}\]; :""\\\|<>\/\?,\.!—–]+", " ");

            return cleanText.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
        }
        catch
        {
            lock (Console.Out) { Console.ForegroundColor = ConsoleColor.DarkYellow; Console.WriteLine($"[Поток-{workerId}] Узел недоступен."); Console.ResetColor(); }
            return Array.Empty<string>();
        }
    }

    public string PopNextUrl()
    {
        lock (_urlsQueue)
        {
            if (_urlsQueue.Count == 0) return null;
            string url = _urlsQueue[0]; 
            _urlsQueue.RemoveAt(0);
            return url;
        }
    }
}
public class MarkovModel
{
    private readonly Dictionary<string, List<(string Word, int Count)>> _markovDb = new Dictionary<string, List<(string, int)>>();
    public int UniquePairsCount { get { lock (_markovDb) { return _markovDb.Count; } } }

    public void BuildMatrix(string[] words)
    {
        lock (_markovDb)
        {
            for (int i = 0; i < words.Length - 2; i++)
            {
                string keyPair = words[i] + " " + words[i + 1];
                string nextWord = words[i + 2];
                if (!_markovDb.ContainsKey(keyPair)) _markovDb[keyPair] = new List<(string, int)>();
                var list = _markovDb[keyPair];
                int index = list.FindIndex(item => item.Word == nextWord);
                if (index != -1) list[index] = (nextWord, list[index].Count + 1);
                else list.Add((nextWord, 1));
            }
        }
    }

    public List<(string Word, int Count)> GetPredictions(string word1, string word2, out int totalWeight)
    {
        string key = word1 + " " + word2; totalWeight = 0;
        lock (_markovDb)
        {
            if (_markovDb.TryGetValue(key, out var predictions))
            {
                totalWeight = predictions.Sum(p => p.Count);
                return predictions.OrderByDescending(x => x.Count).Take(3).ToList();
            }
        }
        return new List<(string Word, int Count)>();
    }
}
class Program
{
    private static bool _isRunning = true;
    private static int _dnsCheckedCount = 0;
    private static int _vulnerableCount = 0;
    private static bool _useMultiThreading = false;
    private static bool _saveToDisk = false;
    private static readonly string LogFileName = "dns_vulnerabilities.log";
    private static readonly object _fileLock = new object();
    private static readonly object _counterLock = new object();

    private static NetworkEngine engine;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int cpuCores = Environment.ProcessorCount;

        MarkovModel model = new MarkovModel();
        engine = new NetworkEngine(); 
        Process currentProcess = Process.GetCurrentProcess();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== МЕГА-КОМБАЙН СЕТЕВОЙ РАЗВЕДКИ (ПОСЛЕДОВАТЕЛЬНЫЙ IP-СКАНЕР) ===");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"Использовать многопоточность ({cpuCores} ядер CPU)? [y/n]: ");
        string mtChoice = Console.ReadLine()?.Trim().ToLower();
        _useMultiThreading = mtChoice == "y" || mtChoice == "yes";

        Console.Write("Записывать найденные уязвимости в файл на жесткий диск? [y/n]: ");
        string diskChoice = Console.ReadLine()?.Trim().ToLower();
        _saveToDisk = diskChoice == "y" || diskChoice == "yes";

        Console.ForegroundColor = _saveToDisk ? ConsoleColor.Green : ConsoleColor.DarkYellow;
        Console.WriteLine(_saveToDisk ? $"[ДИСК] Запись включена в: {LogFileName}" : "[ДИСК] Запись ВЫКЛЮЧЕНА. Работа строго в ОЗУ (без засорения!).");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("\nВЫБЕРИТЕ РЕЖИМ РАБОТЫ (1-5):");
        Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("  -> Ручной ввод стартовой ссылки (Паук по сайту)");
        Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine("  -> Последовательный сканер DNS-серверов (UDP-Радар)");
        Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine("  -> АВТО-КОМБАЙН ПАУКА: Перебор IP по порядку -> DNS Домен -> Парсинг");
        Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("  -> Точечный AXFR-Аудит конкретного DNS-сервера (Ввод IP)");
        Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  -> ГЛОБАЛЬНЫЙ ПОСЛЕДОВАТЕЛЬНЫЙ РАДАР: Поиск DNS + Авто-тест AXFR");

        Console.ForegroundColor = ConsoleColor.White; Console.Write("\nТвой выбор: ");
        string choice = Console.ReadLine()?.Trim();
        int mode = int.TryParse(choice, out int m) ? m : 1;

        if (mode == 1)
        {
            Console.Write("\nВставь свой URL-адрес (или Enter для авто-активации Режима 3): ");
            string userUrl = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(userUrl)) mode = 3;
            else engine.AddManualUrl(userUrl);
        }
        else if (mode == 4)
        {
            Console.Write("\nВведите IP DNS-сервера: "); string tip = Console.ReadLine()?.Trim();
            Console.Write("Введите имя зоны: "); string tzone = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(tip)) tip = "81.4.108.41"; if (string.IsNullOrWhiteSpace(tzone)) tzone = "zonetransfer.me";
            Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"\n[ТЕСТ] Проверяю {tip} на зону '{tzone}'...");
            bool isVuln = await TestAxfrVulnerabilityAsync(tip, tzone, true);
            Console.ForegroundColor = isVuln ? ConsoleColor.Red : ConsoleColor.Green;
            Console.WriteLine(isVuln ? "[!] УЯЗВИМ! Данные зоны слиты." : "[ОК] Сервер защищен.");
            Console.ResetColor(); Console.ReadKey(); return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n[ Сетевой Движок запущен. НАЖМИ ПРОБЕЛ ИЛИ ENTER для перехода к Т9! ]\n");
        Console.ResetColor();

        while (Console.KeyAvailable) Console.ReadKey(true);

        List<Task> activeTasks = new List<Task>();
        int workerCount = _useMultiThreading ? cpuCores : 1;

        for (int i = 0; i < workerCount; i++)
        {
            int workerId = i + 1;
            activeTasks.Add(Task.Run(async () => {
                while (_isRunning && !Console.KeyAvailable)
                {
                    if (mode == 2) { await engine.ScanTheWorldForDnsAsync(workerId); await Task.Delay(10); }
                    else if (mode == 5) { await GlobalScannerLoopStepAsync(workerId); await Task.Delay(10); }
                    else if (mode == 1 || mode == 3)
                    {
                        string currentTargetUrl = engine.PopNextUrl();
                        if (currentTargetUrl == null)
                        {
                            if (mode == 1) break;
                            if (mode == 3) currentTargetUrl = await engine.DigDomainFromRandomIpAsync(workerId);
                        }

                        if (currentTargetUrl != null)
                        {
                            currentProcess.Refresh();
                            double memoryMb = currentProcess.WorkingSet64 / 1024.0 / 1024.0;
                            lock (Console.Out) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write($"\n[Поток-{workerId}] [Очередь: {engine.QueueCount:D3}] RAM: {memoryMb:F1} МБ "); Console.ResetColor(); }

                            string[] words = await engine.CrawlAndExtractLinksAsync(currentTargetUrl, workerId);
                            if (words.Length >= 3)
                            {
                                model.BuildMatrix(words);
                                lock (Console.Out) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"OK! (+{words.Length} слов, База Т9: {model.UniquePairsCount} пар)"); Console.ResetColor(); }
                            }
                        }
                        await Task.Delay(100);
                    }
                }
            }));
        }

        while (!Console.KeyAvailable) await Task.Delay(100);
        if (Console.KeyAvailable) Console.ReadKey(true);
        _isRunning = false;
        await Task.WhenAll(activeTasks);

        if (model.UniquePairsCount == 0) model.BuildMatrix(new[] { "комбайн", "успешно", "запущен", "набирай", "слова", "комбайн", "работает" });
        List<string> currentSentence = new List<string>();
        while (true)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"=== МАРКОВСКИЙ Т9 (База: {model.UniquePairsCount} пар) ==="); Console.ResetColor();
            Console.Write("\nВаш текст: "); Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(string.Join(" ", currentSentence)); Console.ResetColor();

            List<(string Word, int Count)> topPredictions = new List<(string Word, int Count)>();
            int totalWeight = 0;
            if (currentSentence.Count >= 2) topPredictions = model.GetPredictions(currentSentence[currentSentence.Count - 2], currentSentence[currentSentence.Count - 1], out totalWeight);

            Console.ForegroundColor = ConsoleColor.Magenta; Console.WriteLine("\nВарианты Т9:");
            if (currentSentence.Count < 2) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("  [Введите первые два слова руками...] "); }
            else if (topPredictions.Count > 0)
            {
                for (int i = 0; i < topPredictions.Count; i++)
                {
                    double chance = ((double)topPredictions[i].Count / totalWeight) * 100;
                    Console.WriteLine($"  [{i + 1}] -> {topPredictions[i].Word} ({chance:F1}%)");
                }
            }
            else { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  [Нет продолжения базы]"); }
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine("\nУправление: 1-3 — Выбрать Т9 | Текст руками + Enter | [Esc] - Выход"); Console.ResetColor();
            Console.Write("> "); string userInput = Console.ReadLine()?.Trim();
            if (userInput == null) continue; if (userInput.ToLower() == "esc") break;

            if (topPredictions.Count > 0 && (userInput == "1" || userInput == "2" || userInput == "3"))
            {
                int idx = int.Parse(userInput) - 1;
                if (idx < topPredictions.Count) currentSentence.Add(topPredictions[idx].Word);
            }
            else if (!string.IsNullOrWhiteSpace(userInput))
            {
                foreach (var w in userInput.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) currentSentence.Add(w);
            }
        }
    }

    private static async Task GlobalScannerLoopStepAsync(int workerId)
    {
        string targetIp = engine.GetNextSequentialIp();
        if (targetIp == null) return;
        byte[] udpDnsQuery = new byte[] { 0x11, 0x22, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01 };

        lock (Console.Out) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine($"  [Поток-{workerId}] Тестирую DNS+AXFR для IP: {targetIp}..."); Console.ResetColor(); }

        try
        {
            using (UdpClient udpClient = new UdpClient())
            {
                udpClient.Client.SendTimeout = 400; udpClient.Client.ReceiveTimeout = 400;
                IPEndPoint ep = new IPEndPoint(IPAddress.Parse(targetIp), 53);
                await udpClient.SendAsync(udpDnsQuery, udpDnsQuery.Length, ep);
                var receiveTask = udpClient.ReceiveAsync();

                if (await Task.WhenAny(receiveTask, Task.Delay(400)) == receiveTask)
                {
                    var res = await receiveTask;
                    if (res.Buffer.Length > 10)
                    {
                        lock (_counterLock) { _dnsCheckedCount++; }
                        Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"[РАДАР-{workerId}] Точка №{_dnsCheckedCount}: {targetIp}. Проверяю AXFR..."); Console.ResetColor();

                        bool isVulnerable = await TestAxfrVulnerabilityAsync(targetIp, "zonetransfer.me", false);
                        if (isVulnerable)
                        {
                            lock (_counterLock) { _vulnerableCount++; }
                            Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"[🔥 ДЫРА-{workerId}] Сервер {targetIp} открыт для слива!"); Console.ResetColor();

                            if (_saveToDisk)
                            {
                                lock (_fileLock) { File.AppendAllText(LogFileName, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [VULN] IP: {targetIp}{Environment.NewLine}"); }
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static async Task<bool> TestAxfrVulnerabilityAsync(string ip, string zoneName, bool printPayload)
    {
        try
        {
            using (TcpClient tcpClient = new TcpClient())
            {
                var connectTask = tcpClient.ConnectAsync(ip, 53);
                if (await Task.WhenAny(connectTask, Task.Delay(1500)) != connectTask || !tcpClient.Connected) return false;

                using (NetworkStream stream = tcpClient.GetStream())
                {
                    byte[] rawPacket = BuildAxfrQueryPacket(zoneName);
                    byte[] lengthHeader = BitConverter.GetBytes((ushort)rawPacket.Length);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lengthHeader);

                    await stream.WriteAsync(lengthHeader, 0, lengthHeader.Length);
                    await stream.WriteAsync(rawPacket, 0, rawPacket.Length);
                    await stream.FlushAsync();

                    byte[] responseLenBuffer = new byte[2];
                    int readHeader = await stream.ReadAsync(responseLenBuffer, 0, 2);
                    if (readHeader < 2) return false;

                    if (BitConverter.IsLittleEndian) Array.Reverse(responseLenBuffer);
                    ushort responseLength = BitConverter.ToUInt16(responseLenBuffer, 0);
                    if (responseLength == 0) return false;

                    byte[] responsePayload = new byte[responseLength];
                    int totalBytesRead = 0;
                    while (totalBytesRead < responseLength)
                    {
                        int read = await stream.ReadAsync(responsePayload, totalBytesRead, responseLength - totalBytesRead);
                        if (read == 0) break;
                        totalBytesRead += read;
                    }

                    if (responsePayload.Length > 3)
                    {
                        byte rcode = (byte)(responsePayload[3] & 0x0F);
                        if (rcode == 5 || rcode == 4 || rcode == 1) return false;
                    }
                    else return false;

                    if (printPayload && totalBytesRead > 12)
                    {
                        string cleanText = Regex.Replace(Encoding.ASCII.GetString(responsePayload, 12, totalBytesRead - 12), @"[^a-zA-Z0-9\-\.\s]", " ").Trim();
                        Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine($"\n--- СЛИТАЯ ЗОНА СЕРВЕРА ---\n{cleanText}\n-------------------------"); Console.ResetColor();
                    }
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static byte[] BuildAxfrQueryPacket(string zone)
    {
        List<byte> packet = new List<byte> { 0x12, 0x34, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        foreach (string label in zone.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }
        packet.Add(0x00); packet.Add(0x00); packet.Add(0xFC); packet.Add(0x00); packet.Add(0x01);
        return packet.ToArray();
    }
}
