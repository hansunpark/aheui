using System.Numerics;
using System.Text;

namespace Aheui;

/// <summary>
/// 아희 코드를 텍스트로 읽고 디버깅하는 도구 (AI 에이전트와 사람 모두를 위해).
/// 2차원 코드를 머리로 따라가지 않아도 되도록 좌표, 명령, 흐름, 실행 기록을 글로 풀어 준다.
///   explain  칸마다 좌표·분해·명령
///   asm      실제 실행 흐름을 선형 어셈블리로
///   trace    한 단계씩 실행 기록 (저장공간 내용 포함)
///   lint     구현마다 다르게 동작하거나 채점기에서 문제 되는 곳 경고
/// </summary>
public static class AgentTools
{
    const string ChoNames = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    const string JungNames = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
    static readonly string[] JongNames =
    {
        "", "ㄱ", "ㄲ", "ㄳ", "ㄴ", "ㄵ", "ㄶ", "ㄷ", "ㄹ", "ㄺ", "ㄻ", "ㄼ", "ㄽ", "ㄾ", "ㄿ", "ㅀ",
        "ㅁ", "ㅂ", "ㅄ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
    };

    public const string Usage = """
        에이전트/디버깅 도구:
          aheui explain <파일> [--rows a:b] [--grid-only | --cells-only]
              격자(칸 번호 포함)와 칸마다 (x,y) 좌표, 초성·중성·받침 분해, 명령, 방향을 표로 출력
          aheui asm <파일> [--opt]
              시작점에서 실제로 도달하는 명령만 실행 순서대로 선형 어셈블리로 출력 (각 줄에 원래 좌표)
          aheui trace <파일> [--input 텍스트 | --input-file 파일] [--steps N] [--from N]
                             [--at x,y] [--cmds-only] [--until-output] [--quiet] [--rpaheui-input]
              한 단계씩 실행하며 위치, 글자, 명령, 방향, 저장공간 내용, 출력을 기록 (기본 200단계, 0 = 제한 없음)
              입력을 안 주면 표준 입력을 읽는다. --quiet는 기록 없이 출력만 (참조 구현으로 실행)
              --rpaheui-input은 입력을 백준 채점기(rpaheui 1.2.2-24)처럼 읽는다 (aheui --help 참고)
          aheui lint <파일>
              두 칸 이동의 가장자리 감싸기, 종료 코드, 입력 처리, rpaheui -O2 버그 위험 등 경고
        좌표: (x,y) = (줄 안의 칸 번호, 줄 번호), 둘 다 0부터. 시작: (0,0)에서 아래(↓), 저장공간 _(받침 없음).
        """;

    // ───────────────────────── 칸 해석 ─────────────────────────

    public static string StoreName(int s) => s == 0 ? "_" : JongNames[s];

    static string StoreKind(int s) => s switch
    {
        Hangul.JongIeung => "큐",
        Hangul.JongHieut => "통로(스택처럼)",
        _ => "스택",
    };

    public static string Arrow(int d)
    {
        string a = "↑↓←→"[d & 3].ToString();
        return (d & Hangul.Fast) != 0 ? a + a : a;
    }

    /// <summary>홀소리가 방향에 하는 일 (현재 방향과 무관한 설명).</summary>
    public static string VowelText(int jung) => jung switch
    {
        0 => "→", 2 => "→→", 4 => "←", 6 => "←←",
        8 => "↑", 12 => "↑↑", 13 => "↓", 17 => "↓↓",
        18 => "ㅡ세로반사", 20 => "ㅣ가로반사", 19 => "ㅢ반사",
        _ => "유지",
    };

    /// <summary>명령이 저장공간에서 꺼내야 하는 값의 수 (모자라면 반대 방향으로 간다).</summary>
    public static int Need(int cho, int jong) => cho switch
    {
        Hangul.Digeut or Hangul.SsangDigeut or Hangul.Tieut or Hangul.Nieun or Hangul.Rieul
            or Hangul.Jieut or Hangul.Pieup => 2,
        Hangul.Mieum or Hangul.SsangBieup or Hangul.SsangSiot or Hangul.Chieut => 1,
        _ => 0,
    };

    /// <summary>명령 약어와 뜻. 한글 음절이 아니면 NOP.</summary>
    public static (string mnemonic, string meaning) Command(int cp)
    {
        if (!Hangul.IsSyllable(cp)) return ("NOP", cp == ' ' ? "빈칸" : "한글 아님: 아무것도 안 함");
        var (cho, _, jong) = Hangul.Decompose(cp);
        string j = StoreName(jong);
        return cho switch
        {
            Hangul.Hieut => ("HALT", "종료 (종료 코드 = 현재 저장공간에서 꺼낸 값, 비었으면 0)"),
            Hangul.Digeut => ("ADD", "a b → a+b"),
            Hangul.SsangDigeut => ("MUL", "a b → a*b"),
            Hangul.Tieut => ("SUB", "a b → a-b (먼저 넣은 a에서 위의 b를 뺌)"),
            Hangul.Nieun => ("DIV", "a b → a/b (0 쪽으로 버림, b=0이면 오류)"),
            Hangul.Rieul => ("MOD", "a b → a%b (부호는 a를 따름)"),
            Hangul.Jieut => ("CMP", "a b → a>=b ? 1 : 0"),
            Hangul.Mieum => jong switch
            {
                Hangul.JongIeung => ("PRINTNUM", "꺼내서 숫자로 출력"),
                Hangul.JongHieut => ("PRINTCHR", "꺼내서 유니코드 문자로 출력"),
                _ => ("POP", "꺼내서 버림"),
            },
            Hangul.Bieup => jong switch
            {
                Hangul.JongIeung => ("INNUM", "정수 하나 입력해서 넣음"),
                Hangul.JongHieut => ("INCHR", "문자 하나 입력해서 코드값을 넣음"),
                _ => ($"PUSH {Hangul.Strokes[jong]}", jong == 0 ? "0을 넣음" : $"받침 {j}의 획수 {Hangul.Strokes[jong]}을 넣음"),
            },
            Hangul.SsangBieup => ("DUP", "맨 위 값을 복제 (큐는 맨 앞에 복제)"),
            Hangul.Pieup => ("SWAP", "a b → b a (위 두 값 바꿈, 큐는 앞 두 값)"),
            Hangul.Siot => ($"SEL {j}", $"저장공간 {j}({StoreKind(jong)}) 선택"),
            Hangul.SsangSiot => ($"MOV {j}", $"꺼내서 저장공간 {j}에 넣음"),
            Hangul.Chieut => ("BRZ", "꺼내서 0이면 반대 방향, 아니면 그대로"),
            _ => ("NOP", "명령 없는 초성 (ㅇ ㄱ ㄲ ㅋ ㅉ)"),
        };
    }

    static string Glyph(int cp) => cp == ' ' ? "·" : CodeSpace.CellToString(cp);

    static string Parts(int cp)
    {
        if (!Hangul.IsSyllable(cp)) return "-";
        var (cho, jung, jong) = Hangul.Decompose(cp);
        return $"{ChoNames[cho]}+{JungNames[jung]}" + (jong != 0 ? "+" + JongNames[jong] : "");
    }

    // ───────────────────────── explain ─────────────────────────

    public static string Explain(CodeSpace cs, int rowFrom = 0, int rowTo = int.MaxValue, bool grid = true, bool cells = true)
    {
        var sb = new StringBuilder();
        rowTo = Math.Min(rowTo, cs.Height - 1);
        sb.AppendLine($"# {cs.Height}줄, 가장 긴 줄 {cs.MaxWidth}칸. 좌표 (x,y) = (칸, 줄), 0부터. 시작 (0,0) ↓, 저장공간 _.");
        sb.AppendLine("# 가로로 끝을 넘으면 그 줄의 반대쪽 끝으로, 세로로 넘으면 맨 위/아래 줄로 간다. 짧은 줄 밖은 빈칸.");
        if (grid)
        {
            sb.AppendLine("## 격자 ([x]글자, 빈칸은 ·)");
            for (int y = rowFrom; y <= rowTo; y++)
            {
                sb.Append($"y{y}:");
                var row = cs.Rows[y];
                for (int x = 0; x < row.Length; x++) sb.Append($" [{x}]{Glyph(row[x])}");
                sb.AppendLine();
            }
        }
        if (cells)
        {
            sb.AppendLine("## 칸 (빈칸 제외)  좌표 글자 = 분해 | 명령 | 홀소리 | 뜻");
            for (int y = rowFrom; y <= rowTo; y++)
            {
                var row = cs.Rows[y];
                for (int x = 0; x < row.Length; x++)
                {
                    int cp = row[x];
                    if (cp == ' ') continue;
                    var (m, meaning) = Command(cp);
                    string v = Hangul.IsSyllable(cp) ? VowelText(Hangul.Decompose(cp).jung) : "유지";
                    string need = "";
                    if (Hangul.IsSyllable(cp))
                    {
                        var (cho, _, jong) = Hangul.Decompose(cp);
                        int k = Need(cho, jong);
                        if (k > 0) need = $" [{k}개 미만이면 반대 방향]";
                    }
                    sb.AppendLine($"({x},{y}) {Glyph(cp)} = {Parts(cp)} | {m} | {v} | {meaning}{need}");
                }
            }
        }
        return sb.ToString();
    }

    // ───────────────────────── asm ─────────────────────────

    public static string Asm(CodeSpace cs, bool optimize)
    {
        var p = Compiler.Compile(cs, optimize);
        var nodes = p.Nodes;
        var sb = new StringBuilder();
        sb.AppendLine("# 시작점에서 도달하는 명령만, 실행 순서대로. 빈칸·ㅇ류·ㅅ은 간선에 접혀 있음(ㅅ의 효과는 s=로 보임).");
        sb.AppendLine("# 형식: 라벨: 명령  ; @(x,y) 글자 진행방향 s=저장공간 | 분기");
        sb.AppendLine("#   ?부족→Lk : 저장공간 값이 모자라면 Lk로 (반대 방향)   0→Lk : BRZ에서 꺼낸 값이 0이면 Lk로");
        if (optimize) sb.AppendLine("#   --opt: 융합 명령(ADDC=상수 더하기, CMPBR=비교+분기 등)과 좌표 없는 보정 노드가 섞여 있음");

        // 라벨이 필요한 노드: 점프 대상이 되는 노드 전부
        var referenced = new bool[nodes.Length];
        referenced[p.Start] = true;
        foreach (var n in nodes)
        {
            if (n.Op is Op.Halt or Op.Loop) continue;
            if (n.Alt >= 0 && n.Op != Op.Nop) referenced[n.Alt] = true;
            if (n.Alt2 >= 0 && n.Op is Op.CmpBr or Op.CmpCBr) referenced[n.Alt2] = true;
        }
        // 선형화: Next를 따라 이어 쓰고, 이미 쓴 노드를 만나면 JMP
        var emitted = new bool[nodes.Length];
        var order = new List<int>();
        var pending = new Stack<int>();
        pending.Push(p.Start);
        var jmpAfter = new Dictionary<int, int>();
        while (pending.Count > 0)
        {
            int i = pending.Pop();
            if (emitted[i]) continue;
            referenced[i] = true; // 새 덩어리의 시작
            while (i >= 0 && !emitted[i])
            {
                emitted[i] = true;
                order.Add(i);
                ref var n = ref nodes[i];
                if (n.Op is Op.Halt or Op.Loop) { i = -1; break; }
                if (n.Op is Op.CmpBr or Op.CmpCBr && n.Alt2 >= 0) pending.Push(n.Alt2);
                if (n.Alt >= 0 && n.Op != Op.Nop) pending.Push(n.Alt);
                if (n.Next >= 0 && emitted[n.Next]) { jmpAfter[i] = n.Next; referenced[n.Next] = true; i = -1; break; }
                i = n.Next;
            }
        }
        // 덩어리 순서 때문에 '다음 줄'이 Next가 아닌 경우는 JMP로 표시
        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k];
            ref var n = ref nodes[i];
            if (n.Op is Op.Halt or Op.Loop || jmpAfter.ContainsKey(i)) continue;
            if (k + 1 >= order.Count || order[k + 1] != n.Next) { jmpAfter[i] = n.Next; referenced[n.Next] = true; }
        }

        foreach (int i in order)
        {
            ref var n = ref nodes[i];
            string label = referenced[i] ? $"L{i}:" : "";
            string ins = OpText(n);
            var line = new StringBuilder();
            line.Append($"{label,-7} {ins,-14}; ");
            if (n.X >= 0) line.Append($"@({n.X},{n.Y}) {Glyph(cs.At(n.X, n.Y))} {Arrow(n.Dir)} ");
            else line.Append("(보정) ");
            line.Append($"s={StoreName(n.S)}");
            if (n.Op is Op.Branch or Op.QBranch) line.Append($" | 0→L{n.Alt}");
            else if (n.Op is Op.CmpBr or Op.CmpCBr) line.Append($" | 0→L{n.Alt2} | ?부족→L{n.Alt}");
            else if (n.Alt >= 0 && n.Op is not (Op.Halt or Op.Loop or Op.Nop)) line.Append($" | ?부족→L{n.Alt}");
            if (i == p.Start) line.Append("   ← 시작");
            sb.AppendLine(line.ToString());
            if (jmpAfter.TryGetValue(i, out int t)) sb.AppendLine($"{"",-7} JMP L{t}");
        }
        return sb.ToString();
    }

    static string OpText(in Node n)
    {
        string op = n.Op.ToString().ToUpperInvariant();
        if (op.StartsWith('Q') && n.Op != Op.QPush) op = op[1..]; // 큐 여부는 s=ㅇ로 보인다
        if (n.Op == Op.QPush) op = "PUSH";
        return n.Op switch
        {
            Op.Push or Op.QPush => $"PUSH {n.K}",
            Op.AddC or Op.MulC or Op.SubC or Op.DivC or Op.ModC or Op.CmpC or Op.PrintNumC or Op.PrintChrC or Op.CmpCBr => $"{op} {n.K}",
            Op.Move or Op.QMove => $"MOV {StoreName(n.A)}",
            Op.Branch or Op.QBranch => "BRZ",
            Op.Loop => "LOOP_FOREVER",
            _ => op,
        };
    }

    // ───────────────────────── trace ─────────────────────────

    public sealed class TraceOptions
    {
        public long Steps = 200;          // 0 = 제한 없음
        public long From;                 // 이 단계부터 기록
        public (int x, int y)? At;        // 이 칸만 기록
        public bool CmdsOnly;             // 명령 없는 칸은 기록 안 함
        public bool UntilOutput;          // 처음 출력이 나오면 멈춤
        public bool Quiet;                // 기록 없이 프로그램 출력만
        public bool RpaheuiInput;         // 입력을 백준 채점기(rpaheui 1.2.2-24)처럼 읽기
    }

    /// <summary>표준 동작을 칸 단위로 그대로 따라가는 참조 실행기 (느리지만 단순해서 디버깅용).</summary>
    public sealed class RefMachine
    {
        readonly CodeSpace _cs;
        public int X, Y, D = Hangul.Down, S;
        public readonly List<BigInteger>[] Stores = new List<BigInteger>[28];
        public long Steps;
        public bool Halted;
        public int ExitCode;
        public string? Error;
        readonly byte[] _in;
        int _pos;
        public readonly StringBuilder Output = new();

        readonly bool _rpaheui;

        public RefMachine(CodeSpace cs, byte[] input, bool rpaheuiInput = false)
        {
            _cs = cs;
            _in = input;
            _rpaheui = rpaheuiInput;
            for (int i = 0; i < 28; i++) Stores[i] = new List<BigInteger>();
            if (cs.IsEmpty) Halted = true;
        }

        bool IsQueue(int s) => s == Hangul.JongIeung;

        BigInteger Pop(int s)
        {
            var st = Stores[s];
            int i = IsQueue(s) ? 0 : st.Count - 1;
            var v = st[i];
            st.RemoveAt(i);
            return v;
        }

        public readonly record struct StepInfo(int X, int Y, int Cp, int DirOut, int StoreBefore, string Mnemonic, string Note, string Out);

        public StepInfo Step()
        {
            int x = X, y = Y, cp = _cs.At(x, y), sBefore = S;
            int nd = D;
            string m = "NOP", note = "", outText = "";
            if (Hangul.IsSyllable(cp))
            {
                var (cho, jung, jong) = Hangul.Decompose(cp);
                nd = Hangul.ApplyVowel(jung, D);
                m = Command(cp).mnemonic;
                var st = Stores[S];
                int need = Need(cho, jong);
                if (st.Count < need)
                {
                    nd ^= 1;
                    note = $"저장공간 {StoreName(S)} 부족({st.Count}<{need}) → 반대 방향";
                }
                else
                {
                    switch (cho)
                    {
                        case Hangul.Hieut:
                            Halted = true;
                            ExitCode = st.Count > 0 ? unchecked((int)(uint)(Pop(S) & uint.MaxValue)) : 0;
                            note = $"종료 코드 {ExitCode}";
                            break;
                        case Hangul.Digeut or Hangul.SsangDigeut or Hangul.Tieut or Hangul.Nieun or Hangul.Rieul or Hangul.Jieut:
                        {
                            var b = Pop(S); var a = Pop(S);
                            if (cho is Hangul.Nieun or Hangul.Rieul && b.IsZero)
                            {
                                Error = $"0으로 나눔 @({x},{y})";
                                Halted = true; ExitCode = 1;
                                note = Error;
                                break;
                            }
                            var r = cho switch
                            {
                                Hangul.Digeut => a + b,
                                Hangul.SsangDigeut => a * b,
                                Hangul.Tieut => a - b,
                                Hangul.Nieun => BigInteger.Divide(a, b),
                                Hangul.Rieul => BigInteger.Remainder(a, b),
                                _ => a >= b ? BigInteger.One : BigInteger.Zero,
                            };
                            st.Add(r);
                            note = $"{a},{b} → {r}";
                            break;
                        }
                        case Hangul.Mieum:
                        {
                            var v = Pop(S);
                            if (jong == Hangul.JongIeung) outText = v.ToString();
                            else if (jong == Hangul.JongHieut)
                                outText = v >= 0 && v <= 0x10FFFF && !(v >= 0xD800 && v <= 0xDFFF) ? char.ConvertFromUtf32((int)v) : "\uFFFD";
                            else note = $"{v} 버림";
                            Output.Append(outText);
                            break;
                        }
                        case Hangul.Bieup:
                        {
                            BigInteger v;
                            try
                            {
                                v = jong switch
                                {
                                    Hangul.JongIeung => ReadNumber(),
                                    Hangul.JongHieut => ReadChar(),
                                    _ => Hangul.Strokes[jong],
                                };
                            }
                            catch (RpaheuiInputException e)
                            {
                                Error = $"{e.Message} @({x},{y})";
                                Halted = true; ExitCode = 1;
                                note = Error;
                                break;
                            }
                            st.Add(v);
                            if (jong is Hangul.JongIeung or Hangul.JongHieut) note = $"입력 {v}";
                            break;
                        }
                        case Hangul.SsangBieup:
                            if (IsQueue(S)) st.Insert(0, st[0]); else st.Add(st[^1]);
                            break;
                        case Hangul.Pieup:
                            if (IsQueue(S)) (st[0], st[1]) = (st[1], st[0]);
                            else (st[^1], st[^2]) = (st[^2], st[^1]);
                            break;
                        case Hangul.Siot:
                            S = jong;
                            break;
                        case Hangul.SsangSiot:
                        {
                            var v = Pop(S);
                            Stores[jong].Add(v);
                            note = $"{v} → {StoreName(jong)}";
                            break;
                        }
                        case Hangul.Chieut:
                        {
                            var v = Pop(S);
                            if (v.IsZero) { nd ^= 1; note = "0 → 반대 방향"; }
                            else note = $"{v} ≠ 0 → 그대로";
                            break;
                        }
                    }
                }
            }
            Steps++;
            if (!Halted)
            {
                (X, Y) = _cs.Move(x, y, nd);
                D = nd;
            }
            return new StepInfo(x, y, cp, nd, sBefore, m, note, outText);
        }

        int PeekByte() => _pos < _in.Length ? _in[_pos] : -1;

        BigInteger ReadNumber()
        {
            if (_rpaheui) return BigInteger.Parse(Rpaheui.ReadNumberText(PeekByte, () => _pos++));
            int b;
            while ((b = PeekByte()) is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') _pos++;
            bool neg = false;
            if (b is '+' or '-') { neg = b == '-'; _pos++; b = PeekByte(); }
            if (b < '0' || b > '9') return -1;
            var digits = new StringBuilder();
            while ((b = PeekByte()) >= '0' && b <= '9') { digits.Append((char)b); _pos++; }
            var v = BigInteger.Parse(digits.ToString());
            return neg ? -v : v;
        }

        BigInteger ReadChar()
        {
            if (_rpaheui) return Rpaheui.ReadChar(PeekByte, () => _pos++);
            if (_pos >= _in.Length) return -1;
            // UTF-8 한 글자
            int b0 = _in[_pos];
            int len = b0 < 0x80 ? 1 : (b0 & 0xE0) == 0xC0 ? 2 : (b0 & 0xF0) == 0xE0 ? 3 : (b0 & 0xF8) == 0xF0 ? 4 : 1;
            len = Math.Min(len, _in.Length - _pos);
            var s = Encoding.UTF8.GetString(_in, _pos, len);
            _pos += len;
            try { return char.ConvertToUtf32(s, 0); } catch (ArgumentException) { return 0xFFFD; }
        }

        public string StoresText(int maxItems = 10)
        {
            var sb = new StringBuilder();
            for (int s = 0; s < 28; s++)
            {
                var st = Stores[s];
                if (st.Count == 0 && s != S) continue;
                if (sb.Length > 0) sb.Append(' ');
                if (s == S) sb.Append('*');
                sb.Append(StoreName(s));
                bool q = IsQueue(s);
                sb.Append(q ? "<" : "[");
                int skip = Math.Max(0, st.Count - maxItems);
                if (q)
                {
                    for (int i = 0; i < Math.Min(st.Count, maxItems); i++) sb.Append(i > 0 ? " " : "").Append(st[i]);
                    if (skip > 0) sb.Append($" …+{skip}");
                    sb.Append(']');
                }
                else
                {
                    if (skip > 0) sb.Append($"+{skip}… ");
                    for (int i = skip; i < st.Count; i++) sb.Append(i > skip ? " " : "").Append(st[i]);
                    sb.Append('>');
                }
            }
            return sb.ToString();
        }
    }

    public static int Trace(CodeSpace cs, byte[] input, TraceOptions o, TextWriter w)
    {
        var m = new RefMachine(cs, input, o.RpaheuiInput);
        if (!o.Quiet)
        {
            w.WriteLine("# 단계 (x,y) 글자 명령 나가는방향 | 실행 뒤 저장공간 | 메모");
            w.WriteLine("# 저장공간: *=현재 선택, 스택 [아래 … 위>, 큐 <앞 … 뒤]. 출력은 out=\"…\"");
        }
        bool sawOutput = false;
        while (!m.Halted && (o.Steps == 0 || m.Steps < o.Steps))
        {
            var info = m.Step();
            if (info.Out.Length > 0) sawOutput = true;
            if (o.Quiet) continue;
            long n = m.Steps - 1;
            if (n < o.From) continue;
            if (o.At is { } at && (info.X != at.x || info.Y != at.y)) continue;
            if (o.CmdsOnly && info.Mnemonic == "NOP") continue;
            var line = new StringBuilder();
            line.Append($"#{n,-6} ({info.X},{info.Y}) {Glyph(info.Cp)} {info.Mnemonic,-9} {Arrow(info.DirOut),-2} | {m.StoresText()}");
            if (info.Note.Length > 0) line.Append($" | {info.Note}");
            if (info.Out.Length > 0) line.Append($" | out=\"{Escape(info.Out)}\"");
            w.WriteLine(line.ToString());
            if (o.UntilOutput && sawOutput) break;
        }
        if (o.Quiet)
        {
            w.Write(m.Output.ToString());
            w.Flush();
            if (m.Error != null) Console.Error.WriteLine("aheui: " + m.Error);
            return m.Halted ? m.ExitCode : 3;
        }
        w.WriteLine("# ──");
        string state = m.Error != null ? $"오류: {m.Error}"
            : m.Halted ? $"종료 (종료 코드 {m.ExitCode})"
            : o.UntilOutput && sawOutput ? "첫 출력에서 멈춤"
            : $"단계 제한 {o.Steps}에서 멈춤 (--steps로 늘리기)";
        w.WriteLine($"# {m.Steps}단계, {state}. 다음 위치 ({m.X},{m.Y}) {Arrow(m.D)}, 저장공간 {m.StoresText()}");
        w.WriteLine($"# 전체 출력: \"{Escape(m.Output.ToString())}\"");
        return 0;
    }

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t").Replace("\"", "\\\"");

    // ───────────────────────── lint ─────────────────────────

    public static string Lint(CodeSpace cs)
    {
        var sb = new StringBuilder();
        var warns = new List<string>();
        var infos = new List<string>();
        if (cs.IsEmpty) return "코드가 비어 있습니다 (바로 종료).\n";

        // 칸 단위 상태 (x, y, 들어온 방향, 저장공간) 전부 탐색
        var seen = new HashSet<(int, int, int, int)>();
        var reachedCells = new HashSet<(int, int)>();
        var q = new Queue<(int x, int y, int d, int s)>();
        void Add(int x, int y, int d, int s) { if (seen.Add((x, y, d, s))) q.Enqueue((x, y, d, s)); }
        Add(0, 0, Hangul.Down, 0);
        var wrapWarned = new HashSet<(int, int, int)>();
        var halts = new SortedSet<(int, int, int)>();
        bool inNum = false, inChr = false;
        var inNumCells = new SortedSet<(int, int)>();
        const int Limit = 2_000_000;
        while (q.Count > 0 && seen.Count < Limit)
        {
            var (x, y, d, s) = q.Dequeue();
            reachedCells.Add((x, y));
            int cp = cs.At(x, y);
            int nd = d, ns = s;
            bool canReverse = false;
            if (Hangul.IsSyllable(cp))
            {
                var (cho, jung, jong) = Hangul.Decompose(cp);
                nd = Hangul.ApplyVowel(jung, d);
                if (Need(cho, jong) > 0) canReverse = true;
                if (cho == Hangul.Hieut) { halts.Add((y, x, s)); continue; }
                if (cho == Hangul.Siot) ns = jong;
                if (cho == Hangul.Bieup && jong == Hangul.JongIeung) { inNum = true; inNumCells.Add((y, x)); }
                if (cho == Hangul.Bieup && jong == Hangul.JongHieut) inChr = true;
            }
            foreach (int dd in canReverse ? new[] { nd, nd ^ 1 } : new[] { nd })
            {
                CheckWrap(cs, x, y, dd, canReverse && dd != nd, wrapWarned, warns);
                var (mx, my) = cs.Move(x, y, dd);
                Add(mx, my, dd, canReverse && dd != nd ? s : ns);
            }
        }
        if (seen.Count >= Limit) infos.Add($"상태가 너무 많아 {Limit}개에서 탐색을 멈췄습니다 (결과가 일부일 수 있음).");

        // 도달 불가능한 칸
        var dead = new List<string>();
        for (int y = 0; y < cs.Height; y++)
            for (int x = 0; x < cs.Rows[y].Length; x++)
            {
                int cp = cs.Rows[y][x];
                if (!Hangul.IsSyllable(cp) || reachedCells.Contains((x, y))) continue;
                int cho = Hangul.Decompose(cp).cho;
                if (Hangul.IsCommand(cho) || cho == Hangul.Siot)
                    dead.Add($"({x},{y}){Glyph(cp)}");
            }
        if (dead.Count > 0)
            infos.Add($"시작점에서 어떤 경로로도 닿지 않는 명령 칸 {dead.Count}개: {string.Join(' ', dead.Take(40))}{(dead.Count > 40 ? " …" : "")}");

        if (halts.Count == 0) warns.Add("도달 가능한 ㅎ(종료)이 없습니다: 프로그램이 끝나지 않습니다.");
        foreach (var (y, x, s) in halts)
            infos.Add($"({x},{y}) {Glyph(cs.At(x, y))} 종료 시 저장공간 {StoreName(s)}: 값이 남아 있으면 그 값이 종료 코드 (백준 등 채점기는 0이 아니면 런타임 에러). " +
                      "안전하게 하려면 빈 저장공간을 선택한 뒤 종료하세요.");

        // 컴파일 그래프에서 실행 시 끝나지 않는 명령 없는 루프
        var prog = Compiler.Compile(cs, optimize: false);
        if (prog.Nodes.Any(n => n.Op == Op.Loop))
            warns.Add("명령이 하나도 없는 순환 경로가 있습니다: 거기 들어가면 영원히 돕니다 (빈칸만 도는 길).");

        // rpaheui 1.2.2-24 (백준 채점기) -O2 버그: 저장공간 부족으로 빠져나가는 루프
        var underflowExits = UnderflowLoopExits(prog);
        if (underflowExits.Count > 0)
            infos.Add("루프를 '저장공간이 비면 반대로 가기'로 빠져나가는 곳: " +
                      string.Join(' ', underflowExits.Take(20)) +
                      ". 대부분은 괜찮지만, 백준 채점기(rpaheui 1.2.2-24, -O2)는 다른 루프에서 다른 저장공간에 값이 쌓인 뒤에야 비는 구조의 출구를 " +
                      "잘못 지워 런타임 에러를 낼 수 있습니다. 백준에서만 런타임 에러가 나면 이곳을 의심하고, 개수 세는 값을 따로 두고 ㅊ로 빠져나가게 바꾸세요.");

        if (inNum)
            infos.Add($"숫자 입력(ㅂ+ㅇ) {string.Join(' ', inNumCells.Select(c => $"({c.Item2},{c.Item1})"))}: 입력이 끝났을 때의 값은 구현마다 다릅니다 (이 인터프리터 -1). " +
                      "rpaheui는 숫자 앞 공백을 건너뛰지 않고 숫자가 없으면 오류이며, 숫자 뒤 구분 문자 하나를 먹습니다. 실행·trace에 --rpaheui-input을 주면 백준과 똑같이 읽습니다.");
        if (inNum && inChr)
            warns.Add("숫자 입력과 문자 입력(ㅂ+ㅎ)을 함께 씁니다: 숫자 뒤의 공백/개행이 문자 입력으로 읽히는지는 구현마다 다릅니다.");

        sb.AppendLine($"# lint: 경고 {warns.Count}개, 참고 {infos.Count}개");
        foreach (var w in warns) sb.AppendLine("경고: " + w);
        foreach (var i in infos) sb.AppendLine("참고: " + i);
        return sb.ToString();
    }

    /// <summary>두 칸 이동이 가장자리를 넘어 감싸지는 곳: 착지 칸이 구현마다 다르다.</summary>
    static void CheckWrap(CodeSpace cs, int x, int y, int d, bool reversed, HashSet<(int, int, int)> warned, List<string> warns)
    {
        if ((d & Hangul.Fast) == 0) return;
        // 한 칸만 넘치면 어느 구현이든 반대쪽 끝 칸에 닿는다. 두 칸 넘치거나,
        // 짧은 줄에서 오른쪽으로 넘으면(rpaheui는 가장 긴 줄까지 빈칸을 지나감) 착지 칸이 갈린다.
        int w = cs.Rows[y].Length;
        bool wraps = (d & 3) switch
        {
            Hangul.Up => y == 0,
            Hangul.Down => y == cs.Height - 1,
            Hangul.Left => x == 0,
            _ => x == w - 1 || (x + 2 >= w && w < cs.MaxWidth),
        };
        if (!wraps || !warned.Add((x, y, d))) return;
        var (mx, my) = cs.Move(x, y, d);
        warns.Add($"({x},{y}) {Glyph(cs.At(x, y))}에서 {Arrow(d)} 두 칸 이동이 가장자리를 넘습니다{(reversed ? " (저장공간 부족·0으로 반대로 갈 때)" : "")}. 이 인터프리터는 ({mx},{my})로 가지만 " +
                  "구현마다 착지 칸이 다릅니다 (rpaheui는 가장 긴 줄 기준으로 감싸고 넘친 칸 수를 이어 감). 끝에 빈칸을 두어 넘지 않게 하세요.");
    }

    /// <summary>저장공간 부족 분기(ㅊ 제외)가 루프 안에 있으면서 루프 밖으로 나가는 명령 위치.</summary>
    static List<string> UnderflowLoopExits(CompiledProgram p)
    {
        var res = new List<string>();
        var nodes = p.Nodes;
        if (nodes.Length > 20000) return res;
        foreach (var (n, i) in nodes.Select((n, i) => (n, i)))
        {
            if (n.Op is Op.Halt or Op.Loop or Op.Nop or Op.Branch or Op.QBranch || n.Alt < 0) continue;
            if (n.X < 0) continue;
            // n이 루프 안에 있는지 (Next에서 다시 n으로 돌아올 수 있는지)
            if (!Reaches(nodes, n.Next, i, skipAltFrom: -1)) continue;
            // Alt 쪽이 다시 n으로 돌아오지 않으면 출구
            if (Reaches(nodes, n.Alt, i, skipAltFrom: -1)) continue;
            res.Add($"({n.X},{n.Y})");
        }
        return res.Distinct().ToList();
    }

    static bool Reaches(Node[] nodes, int from, int target, int skipAltFrom)
    {
        if (from < 0) return false;
        var seen = new bool[nodes.Length];
        var st = new Stack<int>();
        st.Push(from);
        while (st.Count > 0)
        {
            int i = st.Pop();
            if (i < 0 || seen[i]) continue;
            if (i == target) return true;
            seen[i] = true;
            ref var n = ref nodes[i];
            if (n.Op is Op.Halt or Op.Loop) continue;
            st.Push(n.Next);
            if (n.Alt >= 0 && n.Op != Op.Nop) st.Push(n.Alt);
            if (n.Op is Op.CmpBr or Op.CmpCBr) st.Push(n.Alt2);
        }
        return false;
    }

    // ───────────────────────── 명령줄 ─────────────────────────

    public static bool IsSubcommand(string a) => a is "explain" or "asm" or "trace" or "lint";

    public static int Main(string[] args)
    {
        string cmd = args[0];
        string? file = null, inputText = null, inputFile = null;
        bool opt = false, gridOnly = false, cellsOnly = false;
        int rowFrom = 0, rowTo = int.MaxValue;
        var t = new TraceOptions();
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                string Val() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 뒤에 값이 필요합니다");
                switch (a)
                {
                    case "--opt": opt = true; break;
                    case "--grid-only": gridOnly = true; break;
                    case "--cells-only": cellsOnly = true; break;
                    case "--rows":
                    {
                        var parts = Val().Split(':');
                        rowFrom = parts[0].Length > 0 ? int.Parse(parts[0]) : 0;
                        rowTo = parts.Length > 1 ? (parts[1].Length > 0 ? int.Parse(parts[1]) : int.MaxValue) : rowFrom;
                        break;
                    }
                    case "--input": inputText = Val(); break;
                    case "--input-file": inputFile = Val(); break;
                    case "--steps": t.Steps = long.Parse(Val()); break;
                    case "--from": t.From = long.Parse(Val()); break;
                    case "--at":
                    {
                        var xy = Val().Split(',');
                        t.At = (int.Parse(xy[0]), int.Parse(xy[1]));
                        break;
                    }
                    case "--cmds-only": t.CmdsOnly = true; break;
                    case "--until-output": t.UntilOutput = true; break;
                    case "--quiet": t.Quiet = true; t.Steps = 0; break;
                    case "--rpaheui-input": t.RpaheuiInput = true; break;
                    case "-h" or "--help": Console.Error.WriteLine(Usage); return 0;
                    default:
                        if (a.StartsWith('-') && a != "-") throw new ArgumentException($"알 수 없는 옵션: {a}");
                        file = a;
                        break;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            Console.Error.WriteLine($"aheui {cmd}: {e.Message}\n\n{Usage}");
            return 2;
        }
        if (file == null) { Console.Error.WriteLine(Usage); return 2; }
        string code;
        try { code = file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"aheui: 파일을 읽을 수 없습니다: {e.Message}");
            return 2;
        }
        var cs = new CodeSpace(code);
        var w = Console.Out;
        switch (cmd)
        {
            case "explain": w.Write(Explain(cs, rowFrom, rowTo, !cellsOnly, !gridOnly)); return 0;
            case "asm": w.Write(Asm(cs, opt)); return 0;
            case "lint": w.Write(Lint(cs)); return 0;
            default:
            {
                byte[] input;
                if (inputText != null) input = Encoding.UTF8.GetBytes(inputText.Replace("\\n", "\n"));
                else if (inputFile != null) input = File.ReadAllBytes(inputFile);
                else if (Console.IsInputRedirected && file != "-")
                {
                    using var ms = new MemoryStream();
                    Console.OpenStandardInput().CopyTo(ms);
                    input = ms.ToArray();
                }
                else input = Array.Empty<byte>();
                if (t.Quiet)
                {
                    using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                    return Trace(cs, input, t, stdout);
                }
                return Trace(cs, input, t, w);
            }
        }
    }
}
