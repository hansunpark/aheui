using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Aheui;

/// <summary>
/// 로컬 IDE 서버. 내장된 정적 파일(ide/*)을 제공하고,
/// /api/run 으로 받은 코드를 이 실행 파일을 별도 프로세스로 띄워 네이티브 속도로 실행한다.
/// localhost에만 바인딩한다.
/// </summary>
public static class IdeServer
{
    const int MaxOutputBytes = 4 << 20;

    public static int Run(string[] args)
    {
        int port = 8517;
        bool openBrowser = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] == "--no-browser") openBrowser = false;
        }

        var listener = new HttpListener();
        string prefix = $"http://localhost:{port}/";
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (HttpListenerException e)
        {
            Console.Error.WriteLine($"포트 {port}을(를) 열 수 없습니다: {e.Message}");
            return 1;
        }
        Console.WriteLine($"아희 IDE: {prefix}  (Ctrl+C로 종료)");
        if (openBrowser)
        {
            try { Process.Start(new ProcessStartInfo(prefix) { UseShellExecute = true }); } catch { /* 브라우저가 없으면 무시 */ }
        }

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch (HttpListenerException) { break; }
            _ = Task.Run(() => Handle(ctx));
        }
        return 0;
    }

    static async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            string path = req.Url!.AbsolutePath;
            if (path == "/api/info")
            {
                await WriteJson(res, new { native = true, version = typeof(IdeServer).Assembly.GetName().Version?.ToString() });
            }
            else if (path == "/api/run" && req.HttpMethod == "POST")
            {
                // 다른 출처의 웹페이지가 이 서버를 이용해 코드를 실행하지 못하게 한다
                var origin = req.Headers["Origin"];
                if (origin != null && !origin.StartsWith($"http://localhost:{req.Url.Port}", StringComparison.Ordinal))
                {
                    res.StatusCode = 403;
                    res.Close();
                    return;
                }
                using var doc = await JsonDocument.ParseAsync(req.InputStream);
                var root = doc.RootElement;
                string code = root.GetProperty("code").GetString() ?? "";
                string input = root.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : "";
                int timeout = root.TryGetProperty("timeoutMs", out var t) ? Math.Clamp(t.GetInt32(), 100, 600_000) : 10_000;
                bool optimize = !root.TryGetProperty("optimize", out var o) || o.GetBoolean();
                await WriteJson(res, await RunNative(code, input, timeout, optimize));
            }
            else
            {
                ServeStatic(res, path == "/" ? "index.html" : path.TrimStart('/'));
            }
        }
        catch (Exception e)
        {
            try
            {
                res.StatusCode = 500;
                await WriteJson(res, new { error = e.Message });
            }
            catch { /* 연결이 끊긴 경우 */ }
        }
    }

    static void ServeStatic(HttpListenerResponse res, string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("ide/" + name);
        if (s == null)
        {
            res.StatusCode = 404;
            res.Close();
            return;
        }
        res.ContentType = Path.GetExtension(name) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        res.Headers["Cache-Control"] = "no-cache";
        s.CopyTo(res.OutputStream);
        res.Close();
    }

    static async Task WriteJson(HttpListenerResponse res, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    static async Task<object> RunNative(string code, string input, int timeoutMs, bool optimize)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"aheui-ide-{Guid.NewGuid():N}.aheui");
        await File.WriteAllTextAsync(tmp, code, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("--stats");
            if (!optimize) psi.ArgumentList.Add("--no-opt");

            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            var stdoutTask = ReadCapped(p.StandardOutput);
            var stderrTask = ReadCapped(p.StandardError);
            try
            {
                await p.StandardInput.WriteAsync(input);
                p.StandardInput.Close();
            }
            catch (IOException) { /* 프로그램이 입력을 다 읽기 전에 끝난 경우 */ }

            bool timedOut = false;
            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { p.Kill(entireProcessTree: true); } catch { }
                    await p.WaitForExitAsync();
                }
            }
            var elapsed = sw.Elapsed.TotalMilliseconds;
            return new
            {
                stdout = await stdoutTask,
                stderr = await stderrTask,
                exitCode = timedOut ? (int?)null : p.ExitCode,
                elapsedMs = elapsed,
                timedOut,
            };
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    static async Task<string> ReadCapped(StreamReader r)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf)) > 0)
        {
            if (sb.Length < MaxOutputBytes) sb.Append(buf, 0, Math.Min(n, MaxOutputBytes - sb.Length));
        }
        return sb.ToString();
    }
}
