using System.Text;

namespace Aheui;

public enum Op : byte
{
    // 제어
    Halt, Loop, Nop,
    // 스택(및 ㅎ 통로) 저장공간 명령
    Push, InNum, InChr,
    Add, Mul, Sub, Div, Mod, Cmp,
    Pop, PrintNum, PrintChr,
    Dup, Swap, Move, Branch,
    // 큐(ㅇ) 저장공간 명령
    QPush, QInNum, QInChr,
    QAdd, QMul, QSub, QDiv, QMod, QCmp,
    QPop, QPrintNum, QPrintChr,
    QDup, QSwap, QMove, QBranch,
    // 최적화기가 만드는 융합 명령 (스택 전용)
    AddC, MulC, SubC, DivC, ModC, CmpC,   // top = top (op) K
    PrintNumC, PrintChrC,                  // 상수 출력
    CmpBr, CmpCBr,                         // 비교 + ㅊ
}

/// <summary>
/// 컴파일된 명령 하나. (위치, 진행 방향, 현재 저장공간)마다 하나씩 만들어지므로
/// 다음 위치와 저장공간 종류가 모두 정적으로 결정되어 있다.
/// </summary>
public struct Node
{
    public Op Op;
    public byte S;      // 현재 저장공간
    public byte A;      // ㅆ의 대상 저장공간
    public long K;      // 상수
    public int Next;    // 정상 진행
    public int Alt;     // 저장공간 부족 / ㅊ에서 0 → 반대 방향
    public int Alt2;    // CmpBr 계열: 비교 결과가 0일 때
    public int X, Y, Dir;
}

public sealed class CompiledProgram
{
    public required Node[] Nodes;
    public required int Start;
    public int OriginalNodeCount;

    public string Dump()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Nodes.Length; i++)
        {
            ref var n = ref Nodes[i];
            sb.Append($"{i,5}{(i == Start ? '*' : ' ')} {n.Op,-10} s={n.S,-2} ");
            switch (n.Op)
            {
                case Op.Push or Op.QPush or Op.AddC or Op.MulC or Op.SubC or Op.DivC or Op.ModC or Op.CmpC
                    or Op.PrintNumC or Op.PrintChrC or Op.CmpCBr:
                    sb.Append($"k={n.K} "); break;
                case Op.Move or Op.QMove: sb.Append($"to={n.A} "); break;
            }
            if (n.Op is not (Op.Halt or Op.Loop)) sb.Append($"-> {n.Next}");
            if (n.Alt >= 0 && n.Op is not (Op.Halt or Op.Loop or Op.Nop)) sb.Append($" | alt {n.Alt}");
            if (n.Op is Op.CmpBr or Op.CmpCBr) sb.Append($" | zero {n.Alt2}");
            if (n.X >= 0) sb.Append($"   @({n.X},{n.Y})");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>
/// 2차원 코드를 명령 그래프로 바꾼다.
/// - 상태 = (칸, 홀소리 적용 후 방향, 현재 저장공간). 시작 상태에서 도달 가능한 것만 만든다.
/// - 아무것도 하지 않는 칸(빈칸, 한글 외 문자, ㅇ/ㄱ/ㄲ/ㅉ/ㅋ)과 ㅅ(저장공간 선택)은
///   모두 컴파일 타임에 따라가서 간선으로 접어 버린다. → 실행 시 이동 계산이 전혀 없다.
/// - 저장공간이 정적이므로 스택/큐 전용 명령을 따로 고른다.
/// </summary>
public sealed class Compiler
{
    readonly CodeSpace _cs;
    readonly List<Node> _nodes = new();
    readonly Dictionary<long, int> _nodeMap = new();
    readonly Dictionary<long, int> _chainMemo = new();
    readonly Queue<int> _work = new();
    int _loopNode = -1;

    Compiler(CodeSpace cs) => _cs = cs;

    public static CompiledProgram Compile(CodeSpace cs, bool optimize = true)
    {
        if (cs.IsEmpty)
        {
            var halt = new Node { Op = Op.Halt, X = -1, Next = -1, Alt = -1, Alt2 = -1 };
            return new CompiledProgram { Nodes = new[] { halt }, Start = 0, OriginalNodeCount = 1 };
        }
        var c = new Compiler(cs);
        // 커서는 (0,0)에서 아래 방향, 받침 없는 저장공간(0)으로 시작
        int start = c.Resolve(0, 0, Hangul.Down, 0);
        while (c._work.Count > 0) c.Link(c._work.Dequeue());
        var nodes = c._nodes.ToArray();
        int original = nodes.Length;
        if (optimize) start = Optimizer.Run(ref nodes, start);
        return new CompiledProgram { Nodes = nodes, Start = start, OriginalNodeCount = original };
    }

    long Key(int x, int y, int d, int s) => ((((long)y * (_cs.MaxWidth + 1)) + x) * 8 + d) * 28 + s;

    int LoopNode()
    {
        if (_loopNode < 0)
        {
            _loopNode = _nodes.Count;
            _nodes.Add(new Node { Op = Op.Loop, X = -1, Next = -1, Alt = -1, Alt2 = -1 });
        }
        return _loopNode;
    }

    /// <summary>방향 d로 (x,y)에 도착했을 때 처음 실행될 명령 노드를 찾는다.</summary>
    int Resolve(int x, int y, int d, int s)
    {
        List<long>? path = null;
        HashSet<long>? seen = null;
        int result;
        while (true)
        {
            long ck = Key(x, y, d, s);
            if (_chainMemo.TryGetValue(ck, out result)) break;
            seen ??= new HashSet<long>();
            if (!seen.Add(ck)) { result = LoopNode(); break; } // 명령 없이 영원히 도는 경로
            (path ??= new List<long>()).Add(ck);

            int c = _cs.At(x, y);
            int nd = d;
            if (Hangul.IsSyllable(c))
            {
                var (cho, jung, jong) = Hangul.Decompose(c);
                nd = Hangul.ApplyVowel(jung, d);
                if (Hangul.IsCommand(cho)) { result = GetNode(x, y, nd, s, cho, jong); break; }
                if (cho == Hangul.Siot) s = jong;
            }
            (x, y) = _cs.Move(x, y, nd);
            d = nd;
        }
        if (path != null) foreach (var k in path) _chainMemo[k] = result;
        return result;
    }

    int GetNode(int x, int y, int d, int s, int cho, int jong)
    {
        long key = Key(x, y, d, s);
        if (_nodeMap.TryGetValue(key, out int idx)) return idx;
        bool q = s == Hangul.JongIeung;
        var n = new Node { S = (byte)s, X = x, Y = y, Dir = d, Next = -1, Alt = -1, Alt2 = -1 };
        switch (cho)
        {
            case Hangul.Hieut: n.Op = Op.Halt; break;
            case Hangul.Digeut: n.Op = q ? Op.QAdd : Op.Add; break;
            case Hangul.SsangDigeut: n.Op = q ? Op.QMul : Op.Mul; break;
            case Hangul.Tieut: n.Op = q ? Op.QSub : Op.Sub; break;
            case Hangul.Nieun: n.Op = q ? Op.QDiv : Op.Div; break;
            case Hangul.Rieul: n.Op = q ? Op.QMod : Op.Mod; break;
            case Hangul.Jieut: n.Op = q ? Op.QCmp : Op.Cmp; break;
            case Hangul.Mieum:
                n.Op = jong switch
                {
                    Hangul.JongIeung => q ? Op.QPrintNum : Op.PrintNum,
                    Hangul.JongHieut => q ? Op.QPrintChr : Op.PrintChr,
                    _ => q ? Op.QPop : Op.Pop,
                };
                break;
            case Hangul.Bieup:
                n.Op = jong switch
                {
                    Hangul.JongIeung => q ? Op.QInNum : Op.InNum,
                    Hangul.JongHieut => q ? Op.QInChr : Op.InChr,
                    _ => q ? Op.QPush : Op.Push,
                };
                n.K = Hangul.Strokes[jong];
                break;
            case Hangul.SsangBieup: n.Op = q ? Op.QDup : Op.Dup; break;
            case Hangul.Pieup: n.Op = q ? Op.QSwap : Op.Swap; break;
            case Hangul.SsangSiot: n.Op = q ? Op.QMove : Op.Move; n.A = (byte)jong; break;
            case Hangul.Chieut: n.Op = q ? Op.QBranch : Op.Branch; break;
            default: throw new InvalidOperationException($"not a command: {cho}");
        }
        idx = _nodes.Count;
        _nodes.Add(n);
        _nodeMap[key] = idx;
        _work.Enqueue(idx);
        return idx;
    }

    static bool NeedsAlt(Op op) => op is not (Op.Push or Op.InNum or Op.InChr or Op.QPush or Op.QInNum or Op.QInChr or Op.Halt);

    void Link(int idx)
    {
        var n = _nodes[idx];
        if (n.Op == Op.Halt) return;
        var (nx, ny) = _cs.Move(n.X, n.Y, n.Dir);
        int next = Resolve(nx, ny, n.Dir, n.S);
        int alt = -1;
        if (NeedsAlt(n.Op))
        {
            int rd = n.Dir ^ 1;
            var (rx, ry) = _cs.Move(n.X, n.Y, rd);
            alt = Resolve(rx, ry, rd, n.S);
        }
        n = _nodes[idx];
        n.Next = next;
        n.Alt = alt;
        _nodes[idx] = n;
    }
}
