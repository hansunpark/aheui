using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Aheui;

/// <summary>JIT으로 만든 코드가 쓰는 실행 문맥.</summary>
public sealed class JitContext
{
    public int Pc;
    public int ExitCode;
    public required Input Input;
    public required Output Output;
}

/// <summary>
/// 명령 그래프를 IL로 바꿔 .NET JIT이 기계어로 컴파일하게 한다.
/// - 노드 하나 = IL 레이블 하나, 간선 = 직접 분기. 해석기의 명령 해독/분기 예측 실패가 사라진다.
/// - RyuJIT는 너무 큰 메서드를 최적화하지 않으므로(MinOpts) 노드를 덩어리로 나눠
///   덩어리마다 메서드 하나를 만들고, 덩어리 사이 이동은 바깥 루프(트램펄린)가 처리한다.
/// - 64비트 넘침이 나면 그 노드에서 멈추고 큰 정수 해석기에 넘긴다.
/// </summary>
public static class Jit
{
    const int ChunkSize = 256;
    const int Jump = -1; // 다른 덩어리로 이동

    /// <summary>JIT 컴파일에 쓴 시간 (통계용)</summary>
    public static readonly System.Diagnostics.Stopwatch CompileTime = new();
    public static int ChunksCompiled;
    public static long Transitions;

    delegate int ChunkFn(ref Store<long> stores0, JitContext ctx, int pc);

    public static Status Run(CompiledProgram p, Store<long>[] stores, ref int pc, Input input, Output output, out int exitCode)
    {
        var nodes = p.Nodes;
        int chunkCount = (nodes.Length + ChunkSize - 1) / ChunkSize;
        var chunks = new ChunkFn?[chunkCount];
        var ctx = new JitContext { Input = input, Output = output, Pc = pc };
        ref Store<long> s0 = ref stores[0];
        while (true)
        {
            int ci = ctx.Pc / ChunkSize;
            var fn = chunks[ci];
            if (fn == null)
            {
                // 실제로 실행되는 덩어리만 컴파일. 첫 호출을 미리 해서 기계어 생성 시간까지 잰다.
                CompileTime.Start();
                fn = chunks[ci] = CompileChunk(nodes, ci);
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(fn);
                CompileTime.Stop();
                ChunksCompiled++;
            }
            int r = fn(ref s0, ctx, ctx.Pc);
            if (r == Jump) { Transitions++; continue; }
            pc = ctx.Pc;
            exitCode = ctx.ExitCode;
            return (Status)r;
        }
    }

    static readonly int StoreSize = Unsafe.SizeOf<Store<long>>();

    static MethodInfo H(string name) =>
        typeof(JitHelpers).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(JitHelpers), name);

    static ChunkFn CompileChunk(Node[] nodes, int ci)
    {
        int lo = ci * ChunkSize, hi = Math.Min(nodes.Length, lo + ChunkSize);
        var dm = new DynamicMethod($"aheui_chunk_{ci}", typeof(int),
            new[] { typeof(Store<long>).MakeByRefType(), typeof(JitContext), typeof(int) },
            typeof(Jit).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        var labels = new Label[hi - lo];
        for (int i = 0; i < labels.Length; i++) labels[i] = il.DefineLabel();
        var res = il.DeclareLocal(typeof(int));

        // 진입: pc에 해당하는 레이블로 바로 분기
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4, lo);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Switch, labels);
        il.Emit(OpCodes.Ldc_I4, (int)Status.Halted);
        il.Emit(OpCodes.Ret);

        void Store(int s)
        {
            il.Emit(OpCodes.Ldarg_0);
            if (s != 0)
            {
                il.Emit(OpCodes.Ldc_I4, s * StoreSize);
                il.Emit(OpCodes.Add);
            }
        }

        // 노드 t로 가기: 같은 덩어리면 직접 분기, 아니면 pc를 기록하고 돌아간다
        void Goto(int t)
        {
            if (t >= lo && t < hi) { il.Emit(OpCodes.Br, labels[t - lo]); return; }
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, t);
            il.Emit(OpCodes.Stfld, typeof(JitContext).GetField(nameof(JitContext.Pc))!);
            il.Emit(OpCodes.Ldc_I4, Jump);
            il.Emit(OpCodes.Ret);
        }

        // 현재 노드에서 멈추고 status 반환
        void Exit(int self, Status status)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, self);
            il.Emit(OpCodes.Stfld, typeof(JitContext).GetField(nameof(JitContext.Pc))!);
            il.Emit(OpCodes.Ldc_I4, (int)status);
            il.Emit(OpCodes.Ret);
        }

        // 결과 코드에 따라 분기: targets[r]로 가고, 범위를 벗어나면(넘침/0 나눗셈) 해당 상태로 멈춘다
        void Dispatch(int self, params int[] targets)
        {
            il.Emit(OpCodes.Stloc, res);
            var tl = new Label[targets.Length];
            for (int k = 0; k < targets.Length; k++) tl[k] = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, res);
            il.Emit(OpCodes.Switch, tl);
            // 2 = 넘침, 3 = 0으로 나눔
            var div = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, res);
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Beq, div);
            Exit(self, Status.Overflow);
            il.MarkLabel(div);
            Exit(self, Status.DivideByZero);
            for (int k = 0; k < targets.Length; k++)
            {
                il.MarkLabel(tl[k]);
                Goto(targets[k]);
            }
        }

        // bool 결과: true → next, false → alt
        void IfOk(int next, int alt)
        {
            var bad = il.DefineLabel();
            il.Emit(OpCodes.Brfalse, bad);
            Goto(next);
            il.MarkLabel(bad);
            Goto(alt);
        }

        // ───── 자주 쓰는 스택 명령은 도우미 호출 없이 IL을 직접 만든다 ─────
        // (메서드가 크면 RyuJIT의 인라인 예산이 바닥나 도우미가 진짜 함수 호출이 되기 때문)
        var fCount = typeof(Store<long>).GetField(nameof(Store<long>.Count))!;
        var fBuf = typeof(Store<long>).GetField(nameof(Store<long>.Buf))!;
        var mGrow = H(nameof(JitHelpers.GrowStack));
        var lc = il.DeclareLocal(typeof(int));
        var la = il.DeclareLocal(typeof(long));
        var lb = il.DeclareLocal(typeof(long));
        var lr = il.DeclareLocal(typeof(long));
        var lbuf = il.DeclareLocal(typeof(long[]));

        void LoadCount(int st) { Store(st); il.Emit(OpCodes.Ldfld, fCount); il.Emit(OpCodes.Stloc, lc); }
        void LoadBuf(int st) { Store(st); il.Emit(OpCodes.Ldfld, fBuf); il.Emit(OpCodes.Stloc, lbuf); }
        void SetCount(int st, int delta)
        {
            Store(st);
            il.Emit(OpCodes.Ldloc, lc);
            if (delta != 0) { il.Emit(OpCodes.Ldc_I4, delta); il.Emit(OpCodes.Add); }
            il.Emit(OpCodes.Stfld, fCount);
        }
        // c < need 이면 alt로
        void Need(int need, int alt)
        {
            var ok = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, lc);
            il.Emit(OpCodes.Ldc_I4, need);
            il.Emit(OpCodes.Bge, ok);
            Goto(alt);
            il.MarkLabel(ok);
        }
        void Index(int off)
        {
            il.Emit(OpCodes.Ldloc, lbuf);
            il.Emit(OpCodes.Ldloc, lc);
            if (off != 0) { il.Emit(OpCodes.Ldc_I4, off); il.Emit(OpCodes.Add); }
        }
        // lbuf[lc + off] 읽기
        void Elem(int off) { Index(off); il.Emit(OpCodes.Ldelem_I8); }
        // lbuf[lc + off] = v
        void SetElem(int off, LocalBuilder v) { Index(off); il.Emit(OpCodes.Ldloc, v); il.Emit(OpCodes.Stelem_I8); }
        // 스택 st에 v 넣기 (용량 검사 포함). lc/lbuf를 덮어쓴다.
        void PushLocal(int st, LocalBuilder v)
        {
            LoadCount(st);
            LoadBuf(st);
            var ok = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, lbuf);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            il.Emit(OpCodes.Ldloc, lc);
            il.Emit(OpCodes.Bgt, ok);
            Store(st);
            il.Emit(OpCodes.Call, mGrow);
            LoadBuf(st);
            il.MarkLabel(ok);
            SetElem(0, v);
            SetCount(st, 1);
        }
        // lr = la (+|-) lb 가 넘쳤으면 Overflow로 멈춘다
        // add: ((a ^ r) & (b ^ r)) < 0,  sub: ((a ^ b) & (a ^ r)) < 0
        void CheckOverflow(int self, bool sub)
        {
            var ok = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, la);
            il.Emit(OpCodes.Ldloc, sub ? lb : lr);
            il.Emit(OpCodes.Xor);
            il.Emit(OpCodes.Ldloc, sub ? la : lb);
            il.Emit(OpCodes.Ldloc, lr);
            il.Emit(OpCodes.Xor);
            il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I8, 0L);
            il.Emit(OpCodes.Bge, ok);
            Exit(self, Status.Overflow);
            il.MarkLabel(ok);
        }
        // 스택의 (lc + off) 칸에 "(b >= a) ? 1 : 0" 을 쓴다. 비교할 두 값은 미리 스택(IL)에 올려 둔다.
        void StoreGe(int off, Action pushOperands)
        {
            Index(off);
            pushOperands();
            il.Emit(OpCodes.Clt);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Conv_I8);
            il.Emit(OpCodes.Stelem_I8);
        }
        void Fork(OpCode cond, int whenTrue, int whenFalse)
        {
            var t = il.DefineLabel();
            il.Emit(cond, t);
            Goto(whenFalse);
            il.MarkLabel(t);
            Goto(whenTrue);
        }

        // lr = la * lb. 두 값이 32비트 안이면 넘칠 수 없으므로 바로 곱하고, 아니면 검사하는 함수를 부른다.
        var mTryMul = typeof(LongOps).GetMethod(nameof(LongOps.TryMul))!;
        void Multiply(int self, bool bIsSmallConst)
        {
            var slow = il.DefineLabel();
            var done = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, la); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Conv_I8);
            il.Emit(OpCodes.Ldloc, la); il.Emit(OpCodes.Bne_Un, slow);
            if (!bIsSmallConst)
            {
                il.Emit(OpCodes.Ldloc, lb); il.Emit(OpCodes.Conv_I4); il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Ldloc, lb); il.Emit(OpCodes.Bne_Un, slow);
            }
            il.Emit(OpCodes.Ldloc, la); il.Emit(OpCodes.Ldloc, lb); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Stloc, lr);
            il.Emit(OpCodes.Br, done);
            il.MarkLabel(slow);
            il.Emit(OpCodes.Ldloc, la); il.Emit(OpCodes.Ldloc, lb); il.Emit(OpCodes.Ldloca, lr);
            il.Emit(OpCodes.Call, mTryMul);
            il.Emit(OpCodes.Brtrue, done);
            Exit(self, Status.Overflow);
            il.MarkLabel(done);
        }

        bool EmitInline(int i, Node n)
        {
            if (n.S == Hangul.JongIeung) return false; // 큐는 도우미 사용
            int st = n.S;
            switch (n.Op)
            {
                case Op.Push:
                    il.Emit(OpCodes.Ldc_I8, n.K);
                    il.Emit(OpCodes.Stloc, la);
                    PushLocal(st, la);
                    Goto(n.Next);
                    return true;
                case Op.Dup:
                    LoadCount(st);
                    Need(1, n.Alt);
                    LoadBuf(st);
                    Elem(-1);
                    il.Emit(OpCodes.Stloc, la);
                    PushLocal(st, la);
                    Goto(n.Next);
                    return true;
                case Op.Pop:
                    LoadCount(st);
                    Need(1, n.Alt);
                    SetCount(st, -1);
                    Goto(n.Next);
                    return true;
                case Op.Swap:
                    LoadCount(st);
                    Need(2, n.Alt);
                    LoadBuf(st);
                    Elem(-1); il.Emit(OpCodes.Stloc, la);
                    Elem(-2); il.Emit(OpCodes.Stloc, lb);
                    SetElem(-1, lb);
                    SetElem(-2, la);
                    Goto(n.Next);
                    return true;
                case Op.Branch:
                    LoadCount(st);
                    Need(1, n.Alt);
                    SetCount(st, -1);
                    LoadBuf(st);
                    Elem(-1);
                    Fork(OpCodes.Brtrue, n.Next, n.Alt);
                    return true;
                case Op.Move when n.A != Hangul.JongIeung:
                    LoadCount(st);
                    Need(1, n.Alt);
                    SetCount(st, -1);
                    LoadBuf(st);
                    Elem(-1);
                    il.Emit(OpCodes.Stloc, la);
                    PushLocal(n.A, la);
                    Goto(n.Next);
                    return true;
                case Op.Add or Op.Sub:
                    LoadCount(st);
                    Need(2, n.Alt);
                    LoadBuf(st);
                    Elem(-2); il.Emit(OpCodes.Stloc, la);   // 아래 값
                    Elem(-1); il.Emit(OpCodes.Stloc, lb);   // 맨 위 값
                    il.Emit(OpCodes.Ldloc, la);
                    il.Emit(OpCodes.Ldloc, lb);
                    il.Emit(n.Op == Op.Add ? OpCodes.Add : OpCodes.Sub);
                    il.Emit(OpCodes.Stloc, lr);
                    CheckOverflow(i, n.Op == Op.Sub);
                    SetElem(-2, lr);
                    SetCount(st, -1);
                    Goto(n.Next);
                    return true;
                case Op.AddC or Op.SubC:
                    LoadCount(st);
                    Need(1, n.Alt);
                    LoadBuf(st);
                    Elem(-1); il.Emit(OpCodes.Stloc, la);
                    il.Emit(OpCodes.Ldc_I8, n.K); il.Emit(OpCodes.Stloc, lb);
                    il.Emit(OpCodes.Ldloc, la);
                    il.Emit(OpCodes.Ldloc, lb);
                    il.Emit(n.Op == Op.AddC ? OpCodes.Add : OpCodes.Sub);
                    il.Emit(OpCodes.Stloc, lr);
                    CheckOverflow(i, n.Op == Op.SubC);
                    SetElem(-1, lr);
                    Goto(n.Next);
                    return true;
                case Op.Mul:
                    LoadCount(st);
                    Need(2, n.Alt);
                    LoadBuf(st);
                    Elem(-2); il.Emit(OpCodes.Stloc, la);
                    Elem(-1); il.Emit(OpCodes.Stloc, lb);
                    Multiply(i, false);
                    SetElem(-2, lr);
                    SetCount(st, -1);
                    Goto(n.Next);
                    return true;
                case Op.MulC:
                    LoadCount(st);
                    Need(1, n.Alt);
                    LoadBuf(st);
                    Elem(-1); il.Emit(OpCodes.Stloc, la);
                    il.Emit(OpCodes.Ldc_I8, n.K); il.Emit(OpCodes.Stloc, lb);
                    Multiply(i, n.K == (int)n.K);
                    SetElem(-1, lr);
                    Goto(n.Next);
                    return true;
                case Op.Cmp:
                    LoadCount(st);
                    Need(2, n.Alt);
                    LoadBuf(st);
                    StoreGe(-2, () => { Elem(-2); Elem(-1); });
                    SetCount(st, -1);
                    Goto(n.Next);
                    return true;
                case Op.CmpC:
                    LoadCount(st);
                    Need(1, n.Alt);
                    LoadBuf(st);
                    StoreGe(-1, () => { Elem(-1); il.Emit(OpCodes.Ldc_I8, n.K); });
                    Goto(n.Next);
                    return true;
                case Op.CmpBr:
                    LoadCount(st);
                    Need(2, n.Alt);
                    SetCount(st, -2);
                    LoadBuf(st);
                    Elem(-2);
                    Elem(-1);
                    Fork(OpCodes.Bge, n.Next, n.Alt2);
                    return true;
                case Op.CmpCBr:
                    LoadCount(st);
                    Need(1, n.Alt);
                    SetCount(st, -1);
                    LoadBuf(st);
                    Elem(-1);
                    il.Emit(OpCodes.Ldc_I8, n.K);
                    Fork(OpCodes.Bge, n.Next, n.Alt2);
                    return true;
            }
            return false;
        }

        for (int i = lo; i < hi; i++)
        {
            var n = nodes[i];
            il.MarkLabel(labels[i - lo]);
            if (EmitInline(i, n)) continue;
            switch (n.Op)
            {
                case Op.Nop:
                    Goto(n.Next);
                    break;
                case Op.Halt:
                    Store(n.S);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, H(nameof(JitHelpers.Halt)));
                    Exit(i, Status.Halted);
                    break;
                case Op.Loop:
                    Exit(i, Status.InfiniteLoop);
                    break;
                case Op.Push or Op.QPush:
                    Store(n.S);
                    il.Emit(OpCodes.Ldc_I8, n.K);
                    il.Emit(OpCodes.Call, H(n.Op == Op.Push ? nameof(JitHelpers.Push) : nameof(JitHelpers.QPush)));
                    Goto(n.Next);
                    break;
                case Op.InNum or Op.QInNum or Op.InChr or Op.QInChr:
                    Store(n.S);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, H(n.Op.ToString()));
                    Dispatch(i, n.Next);
                    break;
                case Op.Dup or Op.Swap or Op.Pop or Op.Cmp or Op.QDup or Op.QSwap or Op.QPop or Op.QCmp:
                    Store(n.S);
                    il.Emit(OpCodes.Call, H(n.Op.ToString()));
                    IfOk(n.Next, n.Alt);
                    break;
                case Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Mod
                    or Op.QAdd or Op.QSub or Op.QMul or Op.QDiv or Op.QMod:
                    Store(n.S);
                    il.Emit(OpCodes.Call, H(n.Op.ToString()));
                    Dispatch(i, n.Next, n.Alt);
                    break;
                case Op.AddC or Op.SubC or Op.MulC or Op.DivC or Op.ModC:
                    Store(n.S);
                    il.Emit(OpCodes.Ldc_I8, n.K);
                    il.Emit(OpCodes.Call, H(n.Op.ToString()));
                    Dispatch(i, n.Next, n.Alt);
                    break;
                case Op.CmpC:
                    Store(n.S);
                    il.Emit(OpCodes.Ldc_I8, n.K);
                    il.Emit(OpCodes.Call, H(nameof(JitHelpers.CmpC)));
                    IfOk(n.Next, n.Alt);
                    break;
                case Op.PrintNum or Op.PrintChr or Op.QPrintNum or Op.QPrintChr:
                    Store(n.S);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, H(n.Op.ToString()));
                    IfOk(n.Next, n.Alt);
                    break;
                case Op.PrintNumC or Op.PrintChrC:
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I8, n.K);
                    il.Emit(OpCodes.Call, H(n.Op.ToString()));
                    Goto(n.Next);
                    break;
                case Op.Move or Op.QMove:
                {
                    // 대상 저장공간 종류도 정적으로 알고 있으므로 전용 도우미를 고른다
                    bool toQueue = n.A == Hangul.JongIeung;
                    string name = (n.Op == Op.Move ? "Move" : "QMove") + (toQueue ? "ToQ" : "ToS");
                    Store(n.S);
                    Store(n.A);
                    il.Emit(OpCodes.Call, H(name));
                    IfOk(n.Next, n.Alt);
                    break;
                }
                case Op.Branch or Op.QBranch:
                    Store(n.S);
                    il.Emit(OpCodes.Call, H(n.Op.ToString()));
                    IfOk(n.Next, n.Alt);
                    break;
                case Op.CmpBr:
                    Store(n.S);
                    il.Emit(OpCodes.Call, H(nameof(JitHelpers.CmpBr)));
                    Dispatch(i, n.Next, n.Alt, n.Alt2);
                    break;
                case Op.CmpCBr:
                    Store(n.S);
                    il.Emit(OpCodes.Ldc_I8, n.K);
                    il.Emit(OpCodes.Call, H(nameof(JitHelpers.CmpCBr)));
                    Dispatch(i, n.Next, n.Alt, n.Alt2);
                    break;
                default:
                    throw new InvalidOperationException($"JIT: 지원하지 않는 명령 {n.Op}");
            }
        }
        return dm.CreateDelegate<ChunkFn>();
    }
}

/// <summary>
/// JIT 코드가 부르는 명령 구현. 모두 인라인되도록 작게 유지한다.
/// 결과 코드: bool(true=성공) 또는 int(0=다음, 1=반대 방향, 2=넘침, 3=0으로 나눔).
/// </summary>
public static class JitHelpers
{
    const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining;

    [MethodImpl(Inline)] static ref long At(long[] a, int i) => ref StoreOps.At(a, i);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void GrowStack(ref Store<long> s) => Array.Resize(ref s.Buf, s.Buf.Length * 2);

    [MethodImpl(Inline)] public static void Push(ref Store<long> s, long k) => StoreOps.Push(ref s, k);
    [MethodImpl(Inline)] public static void QPush(ref Store<long> s, long k) => StoreOps.QPush(ref s, k);

    public static void Halt(ref Store<long> s, JitContext ctx) =>
        ctx.ExitCode = s.Count > 0 ? unchecked((int)StoreOps.PopAny(ref s)) : 0;

    [MethodImpl(Inline)]
    public static bool Dup(ref Store<long> s)
    {
        int c = s.Count;
        if (c == 0) return false;
        StoreOps.Push(ref s, At(s.Buf, c - 1));
        return true;
    }

    [MethodImpl(Inline)]
    public static bool Swap(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return false;
        ref long a = ref At(s.Buf, c - 1);
        ref long b = ref At(s.Buf, c - 2);
        (a, b) = (b, a);
        return true;
    }

    [MethodImpl(Inline)]
    public static bool Pop(ref Store<long> s)
    {
        if (s.Count == 0) return false;
        s.Count--;
        return true;
    }

    [MethodImpl(Inline)]
    public static bool Cmp(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return false;
        ref long b = ref At(s.Buf, c - 2);
        b = b >= At(s.Buf, c - 1) ? 1 : 0;
        s.Count = c - 1;
        return true;
    }

    [MethodImpl(Inline)]
    public static int Add(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return 1;
        ref long b = ref At(s.Buf, c - 2);
        if (!LongOps.TryAdd(b, At(s.Buf, c - 1), out long r)) return 2;
        b = r;
        s.Count = c - 1;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int Sub(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return 1;
        ref long b = ref At(s.Buf, c - 2);
        if (!LongOps.TrySub(b, At(s.Buf, c - 1), out long r)) return 2;
        b = r;
        s.Count = c - 1;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int Mul(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return 1;
        ref long b = ref At(s.Buf, c - 2);
        if (!LongOps.TryMul(b, At(s.Buf, c - 1), out long r)) return 2;
        b = r;
        s.Count = c - 1;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int Div(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return 1;
        long a = At(s.Buf, c - 1);
        if (a == 0) return 3;
        ref long b = ref At(s.Buf, c - 2);
        if (!LongOps.TryDiv(b, a, out long r)) return 2;
        b = r;
        s.Count = c - 1;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int Mod(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return 1;
        long a = At(s.Buf, c - 1);
        if (a == 0) return 3;
        ref long b = ref At(s.Buf, c - 2);
        LongOps.TryMod(b, a, out long r);
        b = r;
        s.Count = c - 1;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int AddC(ref Store<long> s, long k)
    {
        int c = s.Count;
        if (c == 0) return 1;
        ref long t = ref At(s.Buf, c - 1);
        if (!LongOps.TryAdd(t, k, out long r)) return 2;
        t = r;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int SubC(ref Store<long> s, long k)
    {
        int c = s.Count;
        if (c == 0) return 1;
        ref long t = ref At(s.Buf, c - 1);
        if (!LongOps.TrySub(t, k, out long r)) return 2;
        t = r;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int MulC(ref Store<long> s, long k)
    {
        int c = s.Count;
        if (c == 0) return 1;
        ref long t = ref At(s.Buf, c - 1);
        if (!LongOps.TryMul(t, k, out long r)) return 2;
        t = r;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int DivC(ref Store<long> s, long k)
    {
        int c = s.Count;
        if (c == 0) return 1;
        if (k == 0) return 3;
        ref long t = ref At(s.Buf, c - 1);
        if (!LongOps.TryDiv(t, k, out long r)) return 2;
        t = r;
        return 0;
    }

    [MethodImpl(Inline)]
    public static int ModC(ref Store<long> s, long k)
    {
        int c = s.Count;
        if (c == 0) return 1;
        if (k == 0) return 3;
        ref long t = ref At(s.Buf, c - 1);
        LongOps.TryMod(t, k, out long r);
        t = r;
        return 0;
    }

    [MethodImpl(Inline)]
    public static bool CmpC(ref Store<long> s, long k)
    {
        int c = s.Count;
        if (c == 0) return false;
        ref long t = ref At(s.Buf, c - 1);
        t = t >= k ? 1 : 0;
        return true;
    }

    /// <summary>0 = 크거나 같음, 1 = 부족, 2 = 작음</summary>
    [MethodImpl(Inline)]
    public static int CmpBr(ref Store<long> s)
    {
        int c = s.Count;
        if (c < 2) return 1;
        bool ge = At(s.Buf, c - 2) >= At(s.Buf, c - 1);
        s.Count = c - 2;
        return ge ? 0 : 2;
    }

    [MethodImpl(Inline)]
    public static int CmpCBr(ref Store<long> s, long k)
    {
        int c = s.Count;
        if (c == 0) return 1;
        s.Count = c - 1;
        return At(s.Buf, c - 1) >= k ? 0 : 2;
    }

    [MethodImpl(Inline)]
    public static bool Branch(ref Store<long> s)
    {
        int c = s.Count;
        if (c == 0) return false;
        s.Count = c - 1;
        return At(s.Buf, c - 1) != 0;
    }

    [MethodImpl(Inline)]
    public static bool MoveToS(ref Store<long> s, ref Store<long> t)
    {
        int c = s.Count;
        if (c == 0) return false;
        s.Count = c - 1;
        StoreOps.Push(ref t, At(s.Buf, c - 1));
        return true;
    }

    [MethodImpl(Inline)]
    public static bool MoveToQ(ref Store<long> s, ref Store<long> t)
    {
        int c = s.Count;
        if (c == 0) return false;
        s.Count = c - 1;
        StoreOps.QPush(ref t, At(s.Buf, c - 1));
        return true;
    }

    public static bool PrintNum(ref Store<long> s, JitContext ctx)
    {
        if (s.Count == 0) return false;
        ctx.Output.WriteLong(At(s.Buf, --s.Count));
        return true;
    }

    public static bool PrintChr(ref Store<long> s, JitContext ctx)
    {
        if (s.Count == 0) return false;
        ctx.Output.WriteCodepoint(At(s.Buf, --s.Count));
        return true;
    }

    public static void PrintNumC(JitContext ctx, long k) => ctx.Output.WriteLong(k);
    public static void PrintChrC(JitContext ctx, long k) => ctx.Output.WriteCodepoint(k);

    public static int InNum(ref Store<long> s, JitContext ctx)
    {
        if (!ctx.Input.ReadNumber(out long v)) return 2;
        StoreOps.Push(ref s, v);
        return 0;
    }

    public static int InChr(ref Store<long> s, JitContext ctx)
    {
        StoreOps.Push(ref s, ctx.Input.ReadChar());
        return 0;
    }

    public static int QInNum(ref Store<long> s, JitContext ctx)
    {
        if (!ctx.Input.ReadNumber(out long v)) return 2;
        StoreOps.QPush(ref s, v);
        return 0;
    }

    public static int QInChr(ref Store<long> s, JitContext ctx)
    {
        StoreOps.QPush(ref s, ctx.Input.ReadChar());
        return 0;
    }

    // ───── 큐 (드물게 쓰이므로 단순하게) ─────
    static int QBin(ref Store<long> s, int op)
    {
        if (s.Count < 2) return 1;
        int mask = s.Buf.Length - 1;
        long a = s.Buf[s.Head], b = s.Buf[(s.Head + 1) & mask];
        long r;
        switch (op)
        {
            case 0: if (!LongOps.TryAdd(b, a, out r)) return 2; break;
            case 1: if (!LongOps.TrySub(b, a, out r)) return 2; break;
            case 2: if (!LongOps.TryMul(b, a, out r)) return 2; break;
            case 3: if (a == 0) return 3; if (!LongOps.TryDiv(b, a, out r)) return 2; break;
            case 4: if (a == 0) return 3; LongOps.TryMod(b, a, out r); break;
            default: r = b >= a ? 1 : 0; break;
        }
        s.Head = (s.Head + 2) & mask;
        s.Count -= 2;
        StoreOps.QPush(ref s, r);
        return 0;
    }

    public static int QAdd(ref Store<long> s) => QBin(ref s, 0);
    public static int QSub(ref Store<long> s) => QBin(ref s, 1);
    public static int QMul(ref Store<long> s) => QBin(ref s, 2);
    public static int QDiv(ref Store<long> s) => QBin(ref s, 3);
    public static int QMod(ref Store<long> s) => QBin(ref s, 4);
    public static bool QCmp(ref Store<long> s) => QBin(ref s, 5) == 0;

    public static bool QPop(ref Store<long> s)
    {
        if (s.Count == 0) return false;
        StoreOps.QPop(ref s);
        return true;
    }

    public static bool QDup(ref Store<long> s)
    {
        if (s.Count == 0) return false;
        StoreOps.QPushFront(ref s, s.Buf[s.Head]);
        return true;
    }

    public static bool QSwap(ref Store<long> s)
    {
        if (s.Count < 2) return false;
        int i0 = s.Head, i1 = (s.Head + 1) & (s.Buf.Length - 1);
        (s.Buf[i0], s.Buf[i1]) = (s.Buf[i1], s.Buf[i0]);
        return true;
    }

    public static bool QPrintNum(ref Store<long> s, JitContext ctx)
    {
        if (s.Count == 0) return false;
        ctx.Output.WriteLong(StoreOps.QPop(ref s));
        return true;
    }

    public static bool QPrintChr(ref Store<long> s, JitContext ctx)
    {
        if (s.Count == 0) return false;
        ctx.Output.WriteCodepoint(StoreOps.QPop(ref s));
        return true;
    }

    public static bool QMoveToS(ref Store<long> s, ref Store<long> t)
    {
        if (s.Count == 0) return false;
        StoreOps.Push(ref t, StoreOps.QPop(ref s));
        return true;
    }

    public static bool QMoveToQ(ref Store<long> s, ref Store<long> t)
    {
        if (s.Count == 0) return false;
        StoreOps.QPush(ref t, StoreOps.QPop(ref s));
        return true;
    }

    public static bool QBranch(ref Store<long> s)
    {
        if (s.Count == 0) return false;
        return StoreOps.QPop(ref s) != 0;
    }
}
