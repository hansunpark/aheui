using System.Diagnostics;
using System.Text;

namespace Aheui;

/// <summary>명령줄 실행 진입점. CLI와 IDE(네이티브 실행)가 함께 쓴다.</summary>
public static class Cli
{
    const string Usage = """
        아희 인터프리터

        사용법:
          aheui <파일.aheui>            프로그램 실행 (표준 입출력 사용)
          aheui -c "<코드>"             코드를 직접 실행

        옵션:
          --no-jit     JIT 대신 해석기로 실행
          --no-opt     핍홀 최적화 끄기 (비교/디버깅용)
          --dump       컴파일된 명령 그래프를 출력하고 종료
          --stats      실행 후 통계(노드 수, 시간)를 표준 오류로 출력
          --profile    명령 종류별/노드별 실행 횟수를 표준 오류로 출력 (느려짐)
          --rpaheui-input
                       입력을 백준 채점기(rpaheui 1.2.2-24)와 똑같이 읽기: 숫자 앞 공백을 건너뛰지 않고,
                       숫자 뒤 ' '/'\t'/'\n' 하나를 먹고, 숫자가 없으면(EOF 포함) 런타임 에러(종료 코드 1)

        에이전트/디버깅용 하위 명령: aheui explain | asm | trace | lint <파일>  (aheui trace --help)
        """;

    public static int Run(string[] args)
    {
        if (args.Length > 0 && AgentTools.IsSubcommand(args[0])) return AgentTools.Main(args);
        string? file = null, code = null;
        bool optimize = true, dump = false, stats = false, profile = false, jit = true, rpaheuiInput = false;
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
                case "--rpaheui-input": rpaheuiInput = true; break;
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
        var input = new Input(Console.OpenStandardInput(), Console.IsInputRedirected ? null : output, rpaheuiInput);

        sw.Restart();
        RunResult result;
        try { result = Vm.Run(program, input, output, profile, jit); }
        catch (RpaheuiInputException e)
        {
            output.Flush();
            Console.Error.WriteLine($"aheui: {e.Message}");
            return 1;
        }
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
