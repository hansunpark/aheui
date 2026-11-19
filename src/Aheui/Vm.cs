using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aheui;

/// <summary>
/// 저장공간. 스택이면 Buf[0..Count) (맨 위 = Buf[Count-1]),
/// 큐면 Head부터 시작하는 고리 버퍼 (용량은 2의 거듭제곱).
/// 구조체 배열에 담아 ref로 접근한다 (간접 참조 한 단계 + 범위 검사 제거).
/// </summary>
public struct Store<T>
{
    public T[] Buf;
    public int Count;
    public int Head;
    public bool IsQueue;

    public static Store<T> Create(bool isQueue) => new() { IsQueue = isQueue, Buf = new T[isQueue ? 16 : 256] };

    /// <summary>논리 순서(스택: 바닥→맨 위, 큐: 앞→뒤)로 원소 나열.</summary>
    public readonly IEnumerable<T> Items()
    {
        var buf = Buf; int head = Head, count = Count; bool q = IsQueue;
        for (int i = 0; i < count; i++)
            yield return q ? buf[(head + i) & (buf.Length - 1)] : buf[i];
    }
}

static class StoreOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T At<T>(T[] a, int i) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), (nint)(uint)i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Push<T>(ref Store<T> s, T v)
    {
        int c = s.Count;
        if ((uint)c >= (uint)s.Buf.Length) Grow(ref s);
        At(s.Buf, c) = v;
        s.Count = c + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Grow<T>(ref Store<T> s) => Array.Resize(ref s.Buf, s.Buf.Length * 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void QPush<T>(ref Store<T> s, T v)
    {
        if (s.Count == s.Buf.Length) GrowQueue(ref s);
        At(s.Buf, (s.Head + s.Count) & (s.Buf.Length - 1)) = v;
        s.Count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void QPushFront<T>(ref Store<T> s, T v)
    {
        if (s.Count == s.Buf.Length) GrowQueue(ref s);
        s.Head = (s.Head - 1) & (s.Buf.Length - 1);
        At(s.Buf, s.Head) = v;
        s.Count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T QPop<T>(ref Store<T> s)
    {
        T v = At(s.Buf, s.Head);
        s.Head = (s.Head + 1) & (s.Buf.Length - 1);
        s.Count--;
        return v;
    }

    public static void PushAny<T>(ref Store<T> s, T v)
    {
        if (s.IsQueue) QPush(ref s, v); else Push(ref s, v);
    }

    public static T PopAny<T>(ref Store<T> s) => s.IsQueue ? QPop(ref s) : At(s.Buf, --s.Count);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void GrowQueue<T>(ref Store<T> s)
    {
        var nb = new T[s.Buf.Length * 2];
        for (int i = 0; i < s.Count; i++) nb[i] = s.Buf[(s.Head + i) & (s.Buf.Length - 1)];
        s.Buf = nb;
        s.Head = 0;
    }
}

/// <summary>정수 표현별 연산. 구조체 제네릭으로 JIT가 표현마다 특화된 코드를 만든다.</summary>
public interface INumOps<T>
{
    static abstract T From(long k);
    static abstract bool TryAdd(T a, T b, out T r);
    static abstract bool TrySub(T a, T b, out T r);
    static abstract bool TryMul(T a, T b, out T r);
    static abstract bool TryDiv(T a, T b, out T r); // b != 0 보장
    static abstract bool TryMod(T a, T b, out T r); // b != 0 보장
    static abstract bool IsZero(T v);
    static abstract bool Ge(T a, T b);
    static abstract void Print(Output o, T v);
    static abstract long ToCodepoint(T v);
    static abstract int ToExitCode(T v);
    static abstract bool ReadNum(Input i, out T v);
}

/// <summary>빠른 경로: 64비트 정수, 넘침을 검사해서 넘치면 큰 정수 모드로 전환.</summary>
public readonly struct LongOps : INumOps<long>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static long From(long k) => k;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryAdd(long a, long b, out long r)
    {
        r = unchecked(a + b);
        return ((a ^ r) & (b ^ r)) >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySub(long a, long b, out long r)
    {
        r = unchecked(a - b);
        return ((a ^ b) & (a ^ r)) >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryMul(long a, long b, out long r)
    {
        long hi = Math.BigMul(a, b, out r);
        return hi == (r >> 63);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDiv(long a, long b, out long r)
    {
        if (b == -1 && a == long.MinValue) { r = 0; return false; }
        r = a / b;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryMod(long a, long b, out long r)
    {
        r = b == -1 ? 0 : a % b;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool IsZero(long v) => v == 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool Ge(long a, long b) => a >= b;
    public static void Print(Output o, long v) => o.WriteLong(v);
    public static long ToCodepoint(long v) => v;
    public static int ToExitCode(long v) => unchecked((int)v);
    public static bool ReadNum(Input i, out long v) => i.ReadNumber(out v);
}

/// <summary>느린 경로: 임의 정밀도 정수.</summary>
public readonly struct BigOps : INumOps<BigInteger>
{
    public static BigInteger From(long k) => k;
    public static bool TryAdd(BigInteger a, BigInteger b, out BigInteger r) { r = a + b; return true; }
    public static bool TrySub(BigInteger a, BigInteger b, out BigInteger r) { r = a - b; return true; }
    public static bool TryMul(BigInteger a, BigInteger b, out BigInteger r) { r = a * b; return true; }
    public static bool TryDiv(BigInteger a, BigInteger b, out BigInteger r) { r = BigInteger.Divide(a, b); return true; }
    public static bool TryMod(BigInteger a, BigInteger b, out BigInteger r) { r = BigInteger.Remainder(a, b); return true; }
    public static bool IsZero(BigInteger v) => v.IsZero;
    public static bool Ge(BigInteger a, BigInteger b) => a >= b;
    public static void Print(Output o, BigInteger v) => o.WriteBig(v);
    public static long ToCodepoint(BigInteger v) => v >= 0 && v <= 0x10FFFF ? (long)v : -1;
    public static int ToExitCode(BigInteger v) => unchecked((int)(uint)(v & uint.MaxValue));

    public static bool ReadNum(Input i, out BigInteger v)
    {
        if (i.Pending is { } p) { i.Pending = null; v = p; return true; }
        if (!i.ReadNumber(out long l)) { v = i.Pending!.Value; i.Pending = null; return true; }
        v = l;
        return true;
    }
}

/// <summary>프로파일링 여부를 타입으로 전달해서 꺼져 있을 때는 JIT가 코드를 완전히 없앤다.</summary>
public interface IProfile { static abstract bool On { get; } }
public readonly struct NoProfile : IProfile { public static bool On => false; }
public readonly struct WithProfile : IProfile { public static bool On => true; }
/// <summary>실행 횟수 제한 (JIT으로 넘어가기 전 해석기 구간)</summary>
public readonly struct WithBudget : IProfile { public static bool On => true; }

public enum Status { Halted, Overflow, DivideByZero, InfiniteLoop, Yield }

public readonly record struct RunResult(Status Status, int ExitCode, int X, int Y, bool UsedBigInt);

/// <summary>실행용으로 압축한 명령 (24바이트). 위치 정보 등은 Node에만 둔다.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Instr
{
    public Op Op;
    public byte S;
    public byte A;
    public int Next;
    public int Alt;
    public int Alt2;
    public long K;
}

public static class Vm
{
    /// <summary>노드별 실행 횟수 (--profile 일 때만 채워진다).</summary>
    public static long[]? Counts;

    /// <summary>이만큼 실행해도 안 끝나면 JIT으로 넘어간다.</summary>
    public static long JitThreshold = 30_000_000;

    public static RunResult Run(CompiledProgram p, Input input, Output output, bool profile = false, bool jit = false)
    {
        if (profile) Counts = new long[p.Nodes.Length];
        var code = new Instr[p.Nodes.Length];
        for (int i = 0; i < code.Length; i++)
        {
            ref var n = ref p.Nodes[i];
            code[i] = new Instr { Op = n.Op, S = n.S, A = n.A, Next = n.Next, Alt = n.Alt, Alt2 = n.Alt2, K = n.K };
        }

        var stores = new Store<long>[28];
        for (int i = 0; i < 28; i++) stores[i] = Store<long>.Create(i == Hangul.JongIeung);
        int pc = p.Start;
        Status st;
        int exit;
        if (jit && !profile)
        {
            // 단계적 실행: 짧은 프로그램은 해석기로 끝내고 (JIT 컴파일 비용 회피),
            // 오래 도는 프로그램만 JIT으로 넘어간다
            st = Exec<long, LongOps, WithBudget>(code, stores, ref pc, input, output, out exit, JitThreshold);
            if (st == Status.Yield) st = Jit.Run(p, stores, ref pc, input, output, out exit);
        }
        else if (profile) st = Exec<long, LongOps, WithProfile>(code, stores, ref pc, input, output, out exit);
        else st = Exec<long, LongOps, NoProfile>(code, stores, ref pc, input, output, out exit);
        bool big = false;
        if (st == Status.Overflow)
        {
            // 64비트를 넘었다: 상태를 큰 정수로 옮겨서 같은 명령부터 다시 실행
            big = true;
            var bstores = new Store<BigInteger>[28];
            for (int i = 0; i < 28; i++)
            {
                bstores[i] = Store<BigInteger>.Create(stores[i].IsQueue);
                foreach (var v in stores[i].Items()) StoreOps.PushAny(ref bstores[i], (BigInteger)v);
            }
            st = profile
                ? Exec<BigInteger, BigOps, WithProfile>(code, bstores, ref pc, input, output, out exit)
                : Exec<BigInteger, BigOps, NoProfile>(code, bstores, ref pc, input, output, out exit);
        }
        output.Flush();
        var node = p.Nodes[pc];
        return new RunResult(st, exit, node.X, node.Y, big);
    }

    // 한 번만 호출되고 오래 도는 메서드이므로 tier-0을 건너뛰고 바로 최적화 컴파일한다
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static Status Exec<T, TOps, TProf>(Instr[] code, Store<T>[] stores, ref int pcRef, Input input, Output output, out int exitCode, long budget = 0)
        where TOps : struct, INumOps<T>
        where TProf : struct, IProfile
    {
        long[] counts = Counts ?? Array.Empty<long>();
        int pc = pcRef;
        exitCode = 0;
        ref Instr c0 = ref MemoryMarshal.GetArrayDataReference(code);
        ref Store<T> s0 = ref MemoryMarshal.GetArrayDataReference(stores);
        while (true)
        {
            ref Instr n = ref Unsafe.Add(ref c0, (nint)(uint)pc);
            if (typeof(TProf) == typeof(WithProfile)) counts[pc]++;
            if (typeof(TProf) == typeof(WithBudget) && --budget < 0) { pcRef = pc; return Status.Yield; }
            ref Store<T> s = ref Unsafe.Add(ref s0, (nint)n.S);
            switch (n.Op)
            {
                // ───── 스택 ─────
                case Op.Push:
                    StoreOps.Push(ref s, TOps.From(n.K));
                    pc = n.Next;
                    continue;
                case Op.Dup:
                {
                    int c = s.Count;
                    if (c == 0) { pc = n.Alt; continue; }
                    StoreOps.Push(ref s, StoreOps.At(s.Buf, c - 1));
                    pc = n.Next;
                    continue;
                }
                case Op.Swap:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    ref T top = ref StoreOps.At(s.Buf, c - 1);
                    ref T under = ref StoreOps.At(s.Buf, c - 2);
                    (top, under) = (under, top);
                    pc = n.Next;
                    continue;
                }
                case Op.Add:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    ref T b = ref StoreOps.At(s.Buf, c - 2);
                    if (!TOps.TryAdd(b, StoreOps.At(s.Buf, c - 1), out T r)) goto overflow;
                    b = r;
                    s.Count = c - 1;
                    pc = n.Next;
                    continue;
                }
                case Op.Sub:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    ref T b = ref StoreOps.At(s.Buf, c - 2);
                    if (!TOps.TrySub(b, StoreOps.At(s.Buf, c - 1), out T r)) goto overflow;
                    b = r;
                    s.Count = c - 1;
                    pc = n.Next;
                    continue;
                }
                case Op.Mul:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    ref T b = ref StoreOps.At(s.Buf, c - 2);
                    if (!TOps.TryMul(b, StoreOps.At(s.Buf, c - 1), out T r)) goto overflow;
                    b = r;
                    s.Count = c - 1;
                    pc = n.Next;
                    continue;
                }
                case Op.Div:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    T a = StoreOps.At(s.Buf, c - 1);
                    if (TOps.IsZero(a)) goto divZero;
                    ref T b = ref StoreOps.At(s.Buf, c - 2);
                    if (!TOps.TryDiv(b, a, out T r)) goto overflow;
                    b = r;
                    s.Count = c - 1;
                    pc = n.Next;
                    continue;
                }
                case Op.Mod:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    T a = StoreOps.At(s.Buf, c - 1);
                    if (TOps.IsZero(a)) goto divZero;
                    ref T b = ref StoreOps.At(s.Buf, c - 2);
                    TOps.TryMod(b, a, out T r);
                    b = r;
                    s.Count = c - 1;
                    pc = n.Next;
                    continue;
                }
                case Op.Cmp:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    ref T b = ref StoreOps.At(s.Buf, c - 2);
                    b = TOps.From(TOps.Ge(b, StoreOps.At(s.Buf, c - 1)) ? 1 : 0);
                    s.Count = c - 1;
                    pc = n.Next;
                    continue;
                }
                case Op.Pop:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    s.Count--;
                    pc = n.Next;
                    continue;
                case Op.PrintNum:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    TOps.Print(output, StoreOps.At(s.Buf, --s.Count));
                    pc = n.Next;
                    continue;
                case Op.PrintChr:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    output.WriteCodepoint(TOps.ToCodepoint(StoreOps.At(s.Buf, --s.Count)));
                    pc = n.Next;
                    continue;
                case Op.InNum:
                {
                    if (!TOps.ReadNum(input, out T v)) goto overflow;
                    StoreOps.Push(ref s, v);
                    pc = n.Next;
                    continue;
                }
                case Op.InChr:
                    StoreOps.Push(ref s, TOps.From(input.ReadChar()));
                    pc = n.Next;
                    continue;
                case Op.Move:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    StoreOps.PushAny(ref Unsafe.Add(ref s0, (nint)n.A), StoreOps.At(s.Buf, --s.Count));
                    pc = n.Next;
                    continue;
                case Op.Branch:
                    if (s.Count == 0 || TOps.IsZero(StoreOps.At(s.Buf, --s.Count))) { pc = n.Alt; continue; }
                    pc = n.Next;
                    continue;

                // ───── 융합 명령 ─────
                case Op.AddC:
                {
                    int c = s.Count;
                    if (c == 0) { pc = n.Alt; continue; }
                    ref T top = ref StoreOps.At(s.Buf, c - 1);
                    if (!TOps.TryAdd(top, TOps.From(n.K), out T r)) goto overflow;
                    top = r;
                    pc = n.Next;
                    continue;
                }
                case Op.SubC:
                {
                    int c = s.Count;
                    if (c == 0) { pc = n.Alt; continue; }
                    ref T top = ref StoreOps.At(s.Buf, c - 1);
                    if (!TOps.TrySub(top, TOps.From(n.K), out T r)) goto overflow;
                    top = r;
                    pc = n.Next;
                    continue;
                }
                case Op.MulC:
                {
                    int c = s.Count;
                    if (c == 0) { pc = n.Alt; continue; }
                    ref T top = ref StoreOps.At(s.Buf, c - 1);
                    if (!TOps.TryMul(top, TOps.From(n.K), out T r)) goto overflow;
                    top = r;
                    pc = n.Next;
                    continue;
                }
                case Op.DivC:
                {
                    int c = s.Count;
                    if (c == 0) { pc = n.Alt; continue; }
                    if (n.K == 0) goto divZero;
                    ref T top = ref StoreOps.At(s.Buf, c - 1);
                    if (!TOps.TryDiv(top, TOps.From(n.K), out T r)) goto overflow;
                    top = r;
                    pc = n.Next;
                    continue;
                }
                case Op.ModC:
                {
                    int c = s.Count;
                    if (c == 0) { pc = n.Alt; continue; }
                    if (n.K == 0) goto divZero;
                    ref T top = ref StoreOps.At(s.Buf, c - 1);
                    TOps.TryMod(top, TOps.From(n.K), out T r);
                    top = r;
                    pc = n.Next;
                    continue;
                }
                case Op.CmpC:
                {
                    int c = s.Count;
                    if (c == 0) { pc = n.Alt; continue; }
                    ref T top = ref StoreOps.At(s.Buf, c - 1);
                    top = TOps.From(TOps.Ge(top, TOps.From(n.K)) ? 1 : 0);
                    pc = n.Next;
                    continue;
                }
                case Op.CmpBr:
                {
                    int c = s.Count;
                    if (c < 2) { pc = n.Alt; continue; }
                    bool ge = TOps.Ge(StoreOps.At(s.Buf, c - 2), StoreOps.At(s.Buf, c - 1));
                    s.Count = c - 2;
                    pc = ge ? n.Next : n.Alt2;
                    continue;
                }
                case Op.CmpCBr:
                {
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    bool ge = TOps.Ge(StoreOps.At(s.Buf, --s.Count), TOps.From(n.K));
                    pc = ge ? n.Next : n.Alt2;
                    continue;
                }
                case Op.PrintNumC:
                    TOps.Print(output, TOps.From(n.K));
                    pc = n.Next;
                    continue;
                case Op.PrintChrC:
                    output.WriteCodepoint(n.K);
                    pc = n.Next;
                    continue;
                case Op.Nop:
                    pc = n.Next;
                    continue;

                // ───── 큐 ─────
                case Op.QPush:
                    StoreOps.QPush(ref s, TOps.From(n.K));
                    pc = n.Next;
                    continue;
                case Op.QInNum:
                {
                    if (!TOps.ReadNum(input, out T v)) goto overflow;
                    StoreOps.QPush(ref s, v);
                    pc = n.Next;
                    continue;
                }
                case Op.QInChr:
                    StoreOps.QPush(ref s, TOps.From(input.ReadChar()));
                    pc = n.Next;
                    continue;
                case Op.QAdd:
                case Op.QSub:
                case Op.QMul:
                case Op.QDiv:
                case Op.QMod:
                case Op.QCmp:
                {
                    if (s.Count < 2) { pc = n.Alt; continue; }
                    int mask = s.Buf.Length - 1;
                    T a = s.Buf[s.Head];                   // 먼저 뽑히는 값
                    T b = s.Buf[(s.Head + 1) & mask];      // 두 번째로 뽑히는 값
                    T r;
                    bool ok;
                    switch (n.Op)
                    {
                        case Op.QAdd: ok = TOps.TryAdd(b, a, out r); break;
                        case Op.QSub: ok = TOps.TrySub(b, a, out r); break;
                        case Op.QMul: ok = TOps.TryMul(b, a, out r); break;
                        case Op.QDiv:
                            if (TOps.IsZero(a)) goto divZero;
                            ok = TOps.TryDiv(b, a, out r); break;
                        case Op.QMod:
                            if (TOps.IsZero(a)) goto divZero;
                            ok = TOps.TryMod(b, a, out r); break;
                        default: r = TOps.From(TOps.Ge(b, a) ? 1 : 0); ok = true; break;
                    }
                    if (!ok) goto overflow;
                    s.Head = (s.Head + 2) & mask;
                    s.Count -= 2;
                    StoreOps.QPush(ref s, r);
                    pc = n.Next;
                    continue;
                }
                case Op.QPop:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    StoreOps.QPop(ref s);
                    pc = n.Next;
                    continue;
                case Op.QPrintNum:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    TOps.Print(output, StoreOps.QPop(ref s));
                    pc = n.Next;
                    continue;
                case Op.QPrintChr:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    output.WriteCodepoint(TOps.ToCodepoint(StoreOps.QPop(ref s)));
                    pc = n.Next;
                    continue;
                case Op.QDup:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    StoreOps.QPushFront(ref s, s.Buf[s.Head]);
                    pc = n.Next;
                    continue;
                case Op.QSwap:
                {
                    if (s.Count < 2) { pc = n.Alt; continue; }
                    int i0 = s.Head, i1 = (s.Head + 1) & (s.Buf.Length - 1);
                    (s.Buf[i0], s.Buf[i1]) = (s.Buf[i1], s.Buf[i0]);
                    pc = n.Next;
                    continue;
                }
                case Op.QMove:
                    if (s.Count == 0) { pc = n.Alt; continue; }
                    StoreOps.PushAny(ref Unsafe.Add(ref s0, (nint)n.A), StoreOps.QPop(ref s));
                    pc = n.Next;
                    continue;
                case Op.QBranch:
                    if (s.Count == 0 || TOps.IsZero(StoreOps.QPop(ref s))) { pc = n.Alt; continue; }
                    pc = n.Next;
                    continue;

                // ───── 제어 ─────
                case Op.Halt:
                    exitCode = s.Count > 0 ? TOps.ToExitCode(StoreOps.PopAny(ref s)) : 0;
                    pcRef = pc;
                    return Status.Halted;
                case Op.Loop:
                    pcRef = pc;
                    return Status.InfiniteLoop;
                default:
                    throw new InvalidOperationException($"bad op {n.Op}");
            }
        }
    overflow:
        pcRef = pc;
        return Status.Overflow;
    divZero:
        pcRef = pc;
        return Status.DivideByZero;
    }
}
