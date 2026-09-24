using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// =====================================================================
//  LoadTester - Prueba de carga controlada (SOLO entornos locales)
//  Ejemplo:
//  dotnet run -c Release -- --url http://localhost:5132/api/auth/login
//                          --duration 15 --concurrency 1,10,50,100
//  (--requests N limita además el total de peticiones por ronda; 0 = sin límite)
// =====================================================================

// ---------- Configuración (parámetros con valores por defecto) ----------
string url = GetArg(args, "--url", "http://localhost:5132/api/auth/login");
string method = GetArg(args, "--method", "POST").ToUpperInvariant();
string body = GetArg(args, "--body", "{\"usernameOrEmail\":\"loadtest\",\"password\":\"Pass123!\"}");
int requestsPerRound = int.Parse(GetArg(args, "--requests", "0"));   // 0 = sin límite
int durationSec = int.Parse(GetArg(args, "--duration", "15"));  // 0 = sin límite de tiempo
int[] concurrencies = GetArg(args, "--concurrency", "1,10,50,100,250,500,1000")
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(int.Parse).ToArray();
int timeoutSeconds = int.Parse(GetArg(args, "--timeout", "10"));
string processName = GetArg(args, "--process", "MiniIdentityApi.Api");
string outputCsv = GetArg(args, "--out", "resultados.csv");
string label = GetArg(args, "--label", DateTime.Now.ToString("yyyyMMdd-HHmm"));
double minSuccessRate = double.Parse(GetArg(args, "--min-success", "50"), CultureInfo.InvariantCulture);
bool autoRegister = !args.Contains("--no-register");

// Permite pasar el cuerpo desde un archivo: --body @login.json
if (body.StartsWith('@')) body = File.ReadAllText(body[1..]);

// ---------- Regla de seguridad: solo localhost ----------
if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"URL rechazada: {url}");
    Console.WriteLine("Este programa solo puede apuntar a localhost / 127.0.0.1 (prueba controlada local).");
    Console.ResetColor();
    return;
}

// ---------- Información del entorno (útil para el informe) ----------
Console.WriteLine("=== LoadTester ===");
Console.WriteLine($"Máquina : {Environment.MachineName}");
Console.WriteLine($"SO      : {RuntimeInformation.OSDescription}");
Console.WriteLine($"Núcleos : {Environment.ProcessorCount} lógicos");
Console.WriteLine($".NET    : {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Destino : {method} {uri}");
if (durationSec <= 0 && requestsPerRound <= 0)
{
    Console.WriteLine("Debes indicar --duration o --requests mayor que 0.");
    return;
}
Console.WriteLine($"Rondas  : concurrencia [{string.Join(", ", concurrencies)}], " +
                  (durationSec > 0 ? $"{durationSec} s c/u" : "") +
                  (requestsPerRound > 0 ? $" (máx. {requestsPerRound} peticiones)" : ""));
Console.WriteLine();

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = int.MaxValue,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
};
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

// ---------- Preparación: registrar el usuario de prueba ----------
if (autoRegister)
{
    var regUri = new Uri(uri, "/api/auth/register");
    const string regBody = "{\"username\":\"loadtest\",\"email\":\"loadtest@example.com\",\"password\":\"Pass123!\"}";
    try
    {
        using var r = await client.PostAsync(regUri, new StringContent(regBody, Encoding.UTF8, "application/json"));
        Console.WriteLine(r.IsSuccessStatusCode
            ? "Usuario 'loadtest' registrado."
            : $"Registro previo devolvió {(int)r.StatusCode} (normal si el usuario ya existía).");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"No se pudo contactar la API: {ex.Message}");
        Console.WriteLine("¿Está corriendo la API? Revisa el puerto en la URL.");
        return;
    }
}

// ---------- Calentamiento (evita que la 1ª ronda salga lenta por JIT) ----------
Console.Write("Calentando... ");
for (int i = 0; i < 20; i++)
{
    try { using var _ = await client.SendAsync(BuildRequest()); } catch { /* se ignora */ }
}
Console.WriteLine("listo.\n");

var monitorCheck = System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;
if (!monitorCheck)
    Console.WriteLine($"Aviso: no encontré el proceso '{processName}'. CPU/RAM no se medirán (usa --process).\n");

Console.WriteLine($"{"Conc",5} | {"Total",8} | {"OK",8} | {"Fallos",6} | {"Éxito",7} | {"Prom ms",8} | {"p95 ms",8} | {"req/s",7} | {"CPU %",6} | {"RAM MB",7}");
Console.WriteLine(new string('-', 100));

// ---------- Rondas progresivas ----------
foreach (var concurrency in concurrencies)
{
    var r = await RunRound(concurrency);
    PrintRound(r);
    SaveCsv(r);

    if (r.SuccessRate < minSuccessRate)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\nTasa de éxito < {minSuccessRate}%: se detienen las rondas (criterio de parada de seguridad).");
        Console.ResetColor();
        break;
    }
    await Task.Delay(3000); // enfriamiento entre rondas
}

Console.WriteLine($"\nResultados guardados en: {Path.GetFullPath(outputCsv)}");

// =====================================================================
//  Funciones
// =====================================================================

HttpRequestMessage BuildRequest()
{
    var req = new HttpRequestMessage(new HttpMethod(method), uri);
    if (method is not ("GET" or "DELETE"))
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
    return req;
}

async Task<RoundResult> RunRound(int concurrency)
{
    var perWorker = new List<double>[concurrency];
    var codes = new ConcurrentDictionary<string, int>();
    int issued = 0, ok = 0;
    var limit = durationSec > 0 ? TimeSpan.FromSeconds(durationSec) : TimeSpan.MaxValue;

    using var monitor = new ProcessMonitor(processName);
    monitor.Start();
    var sw = Stopwatch.StartNew();

    // "concurrency" trabajadores enviando peticiones sin pausa hasta que se acabe
    // el tiempo de la ronda (o se alcance el máximo de peticiones, si se indicó)
    var workers = Enumerable.Range(0, concurrency).Select(async w =>
    {
        var lat = perWorker[w] = new List<double>();
        while (sw.Elapsed < limit)
        {
            if (requestsPerRound > 0 && Interlocked.Increment(ref issued) > requestsPerRound) break;

            long start = Stopwatch.GetTimestamp();
            string key;
            try
            {
                using var req = BuildRequest();
                using var resp = await client.SendAsync(req);
                if (resp.IsSuccessStatusCode) Interlocked.Increment(ref ok);
                key = ((int)resp.StatusCode).ToString();
            }
            catch (TaskCanceledException) { key = "timeout"; }
            catch (HttpRequestException) { key = "error_conexion"; }
            catch (Exception) { key = "otro_error"; }

            lat.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            codes.AddOrUpdate(key, 1, (_, c) => c + 1);
        }
    }).ToArray();

    await Task.WhenAll(workers);
    sw.Stop();
    var usage = await monitor.StopAsync();

    var latencies = perWorker.SelectMany(l => l).ToArray();
    int total = latencies.Length;
    if (total == 0) latencies = new[] { 0.0 };
    var sorted = (double[])latencies.Clone();
    Array.Sort(sorted);

    return new RoundResult(
        Concurrency: concurrency,
        Total: total,
        Success: ok,
        Failed: total - ok,
        SuccessRate: total == 0 ? 0 : ok * 100.0 / total,
        DurationSec: sw.Elapsed.TotalSeconds,
        Throughput: total / sw.Elapsed.TotalSeconds,
        AvgMs: latencies.Average(),
        MinMs: sorted[0],
        P50Ms: Percentile(sorted, 50),
        P95Ms: Percentile(sorted, 95),
        P99Ms: Percentile(sorted, 99),
        MaxMs: sorted[^1],
        CpuAvg: usage.CpuAvg, CpuMax: usage.CpuMax,
        MemAvgMb: usage.MemAvg, MemMaxMb: usage.MemMax,
        Codes: string.Join(" ", codes.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}")));
}

void PrintRound(RoundResult r)
{
    Console.ForegroundColor = r.SuccessRate >= 99 ? ConsoleColor.Green
                            : r.SuccessRate >= 90 ? ConsoleColor.Yellow : ConsoleColor.Red;
    Console.WriteLine($"{r.Concurrency,5} | {r.Total,8} | {r.Success,8} | {r.Failed,6} | {r.SuccessRate,6:F1}% | {r.AvgMs,8:F1} | {r.P95Ms,8:F1} | {r.Throughput,7:F0} | {Fmt(r.CpuAvg),6} | {Fmt(r.MemMaxMb),7}");
    Console.ResetColor();
    if (r.Failed > 0) Console.WriteLine($"        códigos: {r.Codes}");
}

void SaveCsv(RoundResult r)
{
    bool newFile = !File.Exists(outputCsv);
    using var w = new StreamWriter(outputCsv, append: true, Encoding.UTF8);
    if (newFile)
        w.WriteLine("etiqueta;fecha;endpoint;concurrencia;peticiones;exitosas;fallidas;tasa_exito_pct;duracion_s;throughput_rps;" +
                    "lat_prom_ms;lat_min_ms;p50_ms;p95_ms;p99_ms;lat_max_ms;cpu_prom_pct;cpu_max_pct;ram_prom_mb;ram_max_mb;codigos");
    w.WriteLine(string.Join(';',
        label, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), uri.AbsolutePath,
        r.Concurrency, r.Total, r.Success, r.Failed,
        Fmt(r.SuccessRate), Fmt(r.DurationSec), Fmt(r.Throughput),
        Fmt(r.AvgMs), Fmt(r.MinMs), Fmt(r.P50Ms), Fmt(r.P95Ms), Fmt(r.P99Ms), Fmt(r.MaxMs),
        Fmt(r.CpuAvg), Fmt(r.CpuMax), Fmt(r.MemAvgMb), Fmt(r.MemMaxMb), r.Codes));
}

static string Fmt(double d) => double.IsNaN(d) ? "" : d.ToString("F2", CultureInfo.CurrentCulture);

static double Percentile(double[] sorted, double p)
{
    if (sorted.Length == 0) return 0;
    int idx = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
    return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
}

static string GetArg(string[] args, string name, string defaultValue)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : defaultValue;
}

// =====================================================================
//  Tipos
// =====================================================================

record RoundResult(
    int Concurrency, int Total, int Success, int Failed, double SuccessRate,
    double DurationSec, double Throughput,
    double AvgMs, double MinMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
    double CpuAvg, double CpuMax, double MemAvgMb, double MemMaxMb, string Codes);

/// <summary>Muestrea CPU (%) y memoria (MB) del proceso de la API cada 500 ms.</summary>
sealed class ProcessMonitor : IDisposable
{
    private readonly Process? _proc;
    private readonly List<double> _cpu = new();
    private readonly List<double> _mem = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private TimeSpan _lastCpu;
    private long _lastTime;

    public ProcessMonitor(string processName) =>
        _proc = Process.GetProcessesByName(processName).FirstOrDefault();

    public void Start()
    {
        if (_proc is null) return;
        _proc.Refresh();
        _lastCpu = _proc.TotalProcessorTime;
        _lastTime = Stopwatch.GetTimestamp();

        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(500, _cts.Token); }
                catch (TaskCanceledException) { break; }
                Sample();
            }
        });
    }

    private void Sample()
    {
        try
        {
            _proc!.Refresh();
            var cpu = _proc.TotalProcessorTime;
            long now = Stopwatch.GetTimestamp();
            double wallMs = Stopwatch.GetElapsedTime(_lastTime, now).TotalMilliseconds;
            if (wallMs > 50)
            {
                // % respecto a TODOS los núcleos (igual que el Administrador de tareas)
                _cpu.Add((cpu - _lastCpu).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100);
                _mem.Add(_proc.WorkingSet64 / 1024.0 / 1024.0);
                _lastCpu = cpu;
                _lastTime = now;
            }
        }
        catch { /* el proceso pudo terminar */ }
    }

    public async Task<(double CpuAvg, double CpuMax, double MemAvg, double MemMax)> StopAsync()
    {
        _cts.Cancel();
        if (_loop is not null) await _loop;
        if (_proc is not null) Sample(); // muestra final

        if (_cpu.Count == 0) return (double.NaN, double.NaN, double.NaN, double.NaN);
        return (_cpu.Average(), _cpu.Max(), _mem.Average(), _mem.Max());
    }

    public void Dispose()
    {
        _cts.Dispose();
        _proc?.Dispose();
    }
}
