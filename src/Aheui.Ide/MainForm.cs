using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Aheui.Ide;

/// <summary>
/// 아희 IDE 창. UI는 ide/ 폴더의 HTML/JS를 WebView2로 띄우고,
/// 파일 대화상자와 네이티브 실행처럼 브라우저가 못 하는 일만 여기서 처리한다.
/// JS ↔ C#: postMessage({ id, type, ... }) → { id, ok, result | error }
/// </summary>
sealed class MainForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly string? _openOnStart;

    public MainForm(string? openOnStart)
    {
        _openOnStart = openOnStart;
        Text = "아희 IDE";
        Width = 1500;
        Height = 950;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AheuiIde", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "WebView2 런타임을 시작할 수 없습니다.\n" + e.Message, "아희 IDE", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        // AHEUI_IDE_DIR이 있으면 그 폴더를 직접 띄운다 (UI를 고치면서 다시 빌드하지 않고 새로고침만 하려고)
        var uiDir = Environment.GetEnvironmentVariable("AHEUI_IDE_DIR");
        if (string.IsNullOrEmpty(uiDir) || !Directory.Exists(uiDir)) uiDir = Path.Combine(AppContext.BaseDirectory, "ide");

        var core = _web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("aheui.ide", uiDir, CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+F, F5 등 브라우저 단축키는 IDE가 쓴다
        core.WebMessageReceived += OnMessage;
        core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle;
        core.Navigate("https://aheui.ide/index.html");
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); }
        catch { return; }
        if (msg is null) return;
        var id = msg["id"]?.GetValue<int>() ?? 0;
        var type = msg["type"]?.GetValue<string>() ?? "";
        object? result;
        try
        {
            result = type switch
            {
                "runNative" => await RunNative(
                    msg["code"]?.GetValue<string>() ?? "",
                    msg["input"]?.GetValue<string>() ?? "",
                    msg["timeoutMs"]?.GetValue<int>() ?? 30000),
                "openFile" => OpenFile(msg["kind"]?.GetValue<string>() ?? "aheui"),
                "saveFile" => SaveFile(
                    msg["content"]?.GetValue<string>() ?? "",
                    msg["path"]?.GetValue<string>(),
                    msg["suggestedName"]?.GetValue<string>() ?? "program.aheui",
                    msg["kind"]?.GetValue<string>() ?? "aheui"),
                "startupFile" => StartupFile(),
                "readClipboard" => Clipboard.ContainsText() ? Clipboard.GetText() : null,
                _ => throw new InvalidOperationException("알 수 없는 요청: " + type),
            };
            Reply(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Reply(new { id, ok = false, error = ex.Message });
        }
    }

    void Reply(object value) => _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(value));

    static string Filter(string kind) => kind == "project"
        ? "아희 프로젝트 (*.aheuiproj)|*.aheuiproj|모든 파일 (*.*)|*.*"
        : "아희 코드 (*.aheui)|*.aheui|텍스트 (*.txt)|*.txt|모든 파일 (*.*)|*.*";

    object? OpenFile(string kind)
    {
        using var dlg = new OpenFileDialog { Filter = Filter(kind) };
        if (dlg.ShowDialog(this) != DialogResult.OK) return null;
        return new { path = dlg.FileName, name = Path.GetFileName(dlg.FileName), content = File.ReadAllText(dlg.FileName, Encoding.UTF8) };
    }

    object? StartupFile()
    {
        if (_openOnStart == null || !File.Exists(_openOnStart)) return null;
        var full = Path.GetFullPath(_openOnStart);
        return new { path = full, name = Path.GetFileName(full), content = File.ReadAllText(full, Encoding.UTF8) };
    }

    object? SaveFile(string content, string? path, string suggestedName, string kind)
    {
        if (string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog { Filter = Filter(kind), FileName = suggestedName };
            if (dlg.ShowDialog(this) != DialogResult.OK) return null;
            path = dlg.FileName;
        }
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return new { path, name = Path.GetFileName(path) };
    }

    /// <summary>이 실행 파일을 `--run` 모드로 띄워 코드를 실행한다 (무한 루프도 시간 제한으로 끊을 수 있게 별도 프로세스).</summary>
    static async Task<object> RunNative(string code, string input, int timeoutMs)
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
            psi.ArgumentList.Add("--run");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("--stats");

            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            var stdoutTask = ReadCapped(p.StandardOutput);
            var stderrTask = ReadCapped(p.StandardError);
            try
            {
                await p.StandardInput.WriteAsync(input);
                p.StandardInput.Close();
            }
            catch (IOException) { }

            bool timedOut = false;
            using (var cts = new CancellationTokenSource(Math.Clamp(timeoutMs, 100, 600_000)))
            {
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    try { p.Kill(entireProcessTree: true); } catch { }
                    await p.WaitForExitAsync();
                }
            }
            return new
            {
                stdout = await stdoutTask,
                stderr = await stderrTask,
                exitCode = timedOut ? (int?)null : p.ExitCode,
                elapsedMs = sw.Elapsed.TotalMilliseconds,
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
        const int max = 4 << 20;
        var sb = new StringBuilder();
        var buf = new char[8192];
        int n;
        while ((n = await r.ReadAsync(buf)) > 0)
            if (sb.Length < max) sb.Append(buf, 0, Math.Min(n, max - sb.Length));
        return sb.ToString();
    }
}
