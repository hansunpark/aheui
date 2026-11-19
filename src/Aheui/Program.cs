using System.Diagnostics;
using System.Text;
using Aheui;

// 콘솔에 직접 출력/입력할 때는 콘솔 코드 페이지를 UTF-8로 바꿨다가 끝날 때 되돌린다
// (리다이렉트된 경우에는 바이트를 그대로 UTF-8로 주고받는다)
var oldOut = Console.IsOutputRedirected ? null : Console.OutputEncoding;
var oldIn = Console.IsInputRedirected ? null : Console.InputEncoding;
void Restore()
{
    try
    {
        if (oldOut != null) Console.OutputEncoding = oldOut;
        if (oldIn != null) Console.InputEncoding = oldIn;
    }
    catch (IOException) { }
}
try
{
    if (oldOut != null) Console.OutputEncoding = new UTF8Encoding(false);
    if (oldIn != null) Console.InputEncoding = new UTF8Encoding(false);
}
catch (IOException) { }
Console.CancelKeyPress += (_, _) => Restore();
try { return Cli.Run(args); }
finally { Restore(); }

static class Cli
{
    const string Usage = """
        아희 인터프리터

        사용법:
          aheui <파일.aheui>            프로그램 실행 (표준 입출력 사용)
          aheui -c "<코드>"             코드를 직접 실행
          aheui ide [--port N] [--no-browser]
                                         로컬 IDE 서버 실행 (기본 포트 8517)

        옵션:
          --no-jit     JIT 대신 해석기로 실행
          --no-opt     핍홀 최적화 끄기 (비교/디버깅용)
          --dump       컴파일된 명령 그래프를 출력하고 종료
          --stats      실행 후 통계(노드 수, 시간)를 표준 오류로 출력
          --profile    명령 종류별/노드별 실행 횟수를 표준 오류로 출력 (느려짐)
        """;

    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "ide") return IdeServer.Run(args[1..]);

        string? file = null, code = null;
        bool optimize = true, dump = false, stats = false, profile = false, jit = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-c" when i + 1 < args.Length: code = args[++i]; break;
                case "--no-opt": optimize = false; break;
                case "--dump": dump = true; break;
                case "--stats": stats = true; break;
                case "--profile": profile = true; break;
                case "--no-jit": jit = false; break;
                case "-h" or "--help": Console.Error.WriteLine(Usage); return 0;
                default:
                    if (args[i].StartsWith('-') && args[i] != "-") { Console.Error.WriteLine($"알 수 없는 옵션: {args[i]}\n\n{Usage}"); return 2; }
                    file = args[i];
                    break;
            }
        }
        if (code == null)
        {
            if (file == null) { Console.Error.WriteLine(Usage); return 2; }
            try
            {
                code = file == "-" ? new StreamReader(Console.OpenStandardInput(), Encoding.UTF8).ReadToEnd()
                                   : File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"aheui: 파일을 읽을 수 없습니다: {e.Message}");
                return 2;
            }
        }

        var sw = Stopwatch.StartNew();
        if (Environment.GetEnvironmentVariable("AHEUI_JIT_THRESHOLD") is { } th) Vm.JitThreshold = long.Parse(th);
        var program = Compiler.Compile(new CodeSpace(code), optimize);
        var compileTime = sw.Elapsed;

        if (dump)
        {
            Console.Out.Write(program.Dump());
            return 0;
        }

        var stdout = Console.OpenStandardOutput();
        var output = new Output(stdout);
        var input = new Input(Console.OpenStandardInput(), Console.IsInputRedirected ? null : output);

        sw.Restart();
        var result = Vm.Run(program, input, output, profile, jit);
        var runTime = sw.Elapsed;

        if (stats)
        {
            Console.Error.WriteLine(
                $"[aheui] nodes {program.OriginalNodeCount} -> {program.Nodes.Length}, " +
                $"compile {compileTime.TotalMilliseconds:F2} ms, run {runTime.TotalMilliseconds:F2} ms, " +
                $"jit {(jit && !profile ? $"on ({Jit.ChunksCompiled} chunks, {Jit.Transitions} jumps, {Jit.CompileTime.Elapsed.TotalMilliseconds:F1} ms)" : "off")}, bigint {(result.UsedBigInt ? "yes" : "no")}, status {result.Status}");
        }

        if (profile) PrintProfile(program);

        switch (result.Status)
        {
            case Status.DivideByZero:
                Console.Error.WriteLine($"aheui: 0으로 나누었습니다 (위치 {result.X},{result.Y})");
                return 1;
            case Status.InfiniteLoop:
                // 명령이 하나도 없는 순환 경로: 표준대로라면 영원히 돈다. CPU를 쓰지 않고 대기한다.
                Console.Error.WriteLine("aheui: 명령 없는 무한 루프에 들어갔습니다 (Ctrl+C로 종료)");
                Thread.Sleep(Timeout.Infinite);
                return 0;
            default:
                return result.ExitCode;
        }
    }

    static void PrintProfile(CompiledProgram p)
    {
        var counts = Vm.Counts!;
        long total = counts.Sum();
        Console.Error.WriteLine($"[profile] 총 {total:N0} 명령");
        foreach (var g in Enumerable.Range(0, counts.Length).GroupBy(i => p.Nodes[i].Op)
                     .Select(g => (op: g.Key, n: g.Sum(i => counts[i]))).OrderByDescending(x => x.n).Take(20))
            Console.Error.WriteLine($"  {g.op,-10} {g.n,15:N0}  {100.0 * g.n / Math.Max(1, total),5:F1}%");
        Console.Error.WriteLine("[profile] 가장 많이 실행된 노드");
        foreach (var i in Enumerable.Range(0, counts.Length).OrderByDescending(i => counts[i]).Take(15))
        {
            var n = p.Nodes[i];
            Console.Error.WriteLine($"  #{i,-6} {n.Op,-10} k={n.K,-6} s={n.S,-2} @({n.X},{n.Y}) {counts[i],15:N0}");
        }
    }
}
