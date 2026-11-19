namespace Aheui;

/// <summary>
/// 명령 그래프에 대한 핍홀 최적화.
/// 노드를 제자리에서 "그 노드부터 실행한 것과 동일한 의미"의 노드로 바꾸므로
/// 다른 경로에서 들어오는 간선은 건드리지 않아도 의미가 보존된다.
/// 스택 저장공간에만 적용한다 (큐는 넣는 쪽과 빼는 쪽이 달라 상수 접기가 성립하지 않는다).
/// </summary>
public static class Optimizer
{
    public static int Run(ref Node[] arr, int start)
    {
        var nodes = new List<Node>(arr);
        int loopNode = -1;
        int limit = arr.Length * 4 + 64; // 보정 노드 생성 상한

        for (int pass = 0; pass < 32; pass++)
        {
            start = ThreadNops(nodes, start, ref loopNode);
            bool changed = false;
            int count = nodes.Count;
            for (int i = 0; i < count; i++)
            {
                if (TryFuse(nodes, i, nodes.Count < limit)) changed = true;
            }
            if (!changed) break;
        }
        start = ThreadNops(nodes, start, ref loopNode);
        arr = Compact(nodes, ref start);
        return start;
    }

    static bool IsStack(int s) => s != Hangul.JongIeung;

    static bool TryFuse(List<Node> nodes, int i, bool mayGrow)
    {
        var a = nodes[i];
        if (a.Next < 0 || a.Next == i || !IsStack(a.S)) return false;
        // 보정용으로 만든 노드(X < 0)는 저장공간이 비었을 때만 지나는 차가운 경로이므로 더 융합하지 않는다
        // (융합하면 보정 노드가 사다리처럼 계속 늘어난다)
        if (a.X < 0) return false;
        var x = nodes[a.Next];
        if (x.S != a.S) return false; // 사이에 ㅅ이 있어 저장공간이 바뀐 경우

        switch (a.Op)
        {
            case Op.Push:
            {
                long k = a.K;
                switch (x.Op)
                {
                    case Op.Add or Op.Sub or Op.Mul or Op.Div or Op.Mod or Op.Cmp:
                    {
                        // push k; binop  →  top = top (op) k
                        // 스택이 비어 있으면 원래는 k가 들어간 채로 binop에서 반대로 간다 → 보정 노드
                        if (!mayGrow) return false;
                        int fix = nodes.Count;
                        nodes.Add(new Node { Op = Op.Push, S = a.S, K = k, Next = x.Alt, Alt = -1, Alt2 = -1, X = -1, Y = -1 });
                        a.Op = x.Op switch
                        {
                            Op.Add => Op.AddC, Op.Sub => Op.SubC, Op.Mul => Op.MulC,
                            Op.Div => Op.DivC, Op.Mod => Op.ModC, _ => Op.CmpC,
                        };
                        if (a.Op == Op.SubC && k != long.MinValue) { a.Op = Op.AddC; k = -k; }
                        a.K = k;
                        a.Next = x.Next;
                        a.Alt = fix;
                        break;
                    }
                    case Op.AddC or Op.SubC or Op.MulC or Op.DivC or Op.ModC or Op.CmpC:
                    {
                        if (!TryFold(x.Op, k, x.K, out long r)) return false;
                        a.K = r;
                        a.Next = x.Next;
                        break;
                    }
                    case Op.CmpCBr:
                        a.Op = Op.Nop;
                        a.Next = k >= x.K ? x.Next : x.Alt2;
                        break;
                    case Op.Pop:
                        a.Op = Op.Nop;
                        a.Next = x.Next;
                        break;
                    case Op.Branch:
                        a.Op = Op.Nop;
                        a.Next = k != 0 ? x.Next : x.Alt;
                        break;
                    case Op.PrintNum:
                        a.Op = Op.PrintNumC;
                        a.Next = x.Next;
                        break;
                    case Op.PrintChr:
                        a.Op = Op.PrintChrC;
                        a.Next = x.Next;
                        break;
                    default:
                        return false;
                }
                break;
            }
            case Op.Cmp when x.Op == Op.Branch:
                a.Op = Op.CmpBr;
                a.Alt2 = x.Alt;
                a.Next = x.Next;
                break;
            case Op.CmpC when x.Op == Op.Branch:
                a.Op = Op.CmpCBr;
                a.Alt2 = x.Alt;
                a.Next = x.Next;
                break;
            case Op.AddC or Op.SubC when x.Op is Op.AddC or Op.SubC:
            {
                // 연속된 덧셈/뺄셈 상수 합치기: 첫 노드가 성공하면 둘째는 절대 부족하지 않다
                if ((a.Op == Op.SubC && a.K == long.MinValue) || (x.Op == Op.SubC && x.K == long.MinValue)) return false;
                long ka = a.Op == Op.AddC ? a.K : -a.K;
                long kx = x.Op == Op.AddC ? x.K : -x.K;
                long sum;
                try { sum = checked(ka + kx); } catch (OverflowException) { return false; }
                a.Op = Op.AddC;
                a.K = sum;
                a.Next = x.Next;
                break;
            }
            case Op.MulC when x.Op == Op.MulC:
            {
                long prod;
                try { prod = checked(a.K * x.K); } catch (OverflowException) { return false; }
                a.K = prod;
                a.Next = x.Next;
                break;
            }
            default:
                return false;
        }
        nodes[i] = a;
        return true;
    }

    static bool TryFold(Op op, long a, long b, out long r)
    {
        r = 0;
        try
        {
            switch (op)
            {
                case Op.AddC: r = checked(a + b); return true;
                case Op.SubC: r = checked(a - b); return true;
                case Op.MulC: r = checked(a * b); return true;
                case Op.DivC: if (b == 0) return false; r = checked(a / b); return true;
                case Op.ModC: if (b == 0) return false; r = a % b; return true;
                case Op.CmpC: r = a >= b ? 1 : 0; return true;
            }
        }
        catch (OverflowException) { }
        return false;
    }

    /// <summary>Nop 노드를 거치는 간선을 최종 목적지로 바로 연결한다.</summary>
    static int ThreadNops(List<Node> nodes, int start, ref int loopNode)
    {
        int n = nodes.Count;
        int loop = loopNode;
        var target = new int[n];
        Array.Fill(target, -2);

        int Final(int i)
        {
            if (i < 0) return i;
            if (i < target.Length && target[i] != -2) return target[i];
            int cur = i, steps = 0;
            while (cur >= 0 && nodes[cur].Op == Op.Nop)
            {
                cur = nodes[cur].Next;
                if (++steps > n) { cur = -3; break; } // Nop만으로 이루어진 순환 = 무한 루프
            }
            if (cur == -3)
            {
                if (loop < 0)
                {
                    loop = nodes.Count;
                    nodes.Add(new Node { Op = Op.Loop, Next = -1, Alt = -1, Alt2 = -1, X = -1, Y = -1 });
                }
                cur = loop;
            }
            if (i < target.Length) target[i] = cur;
            return cur;
        }

        for (int i = 0; i < n; i++)
        {
            var node = nodes[i];
            node.Next = Final(node.Next);
            node.Alt = Final(node.Alt);
            node.Alt2 = Final(node.Alt2);
            nodes[i] = node;
        }
        int st = Final(start);
        loopNode = loop;
        return st;
    }

    /// <summary>도달 가능한 노드만 남기고 시작점부터 BFS 순서로 번호를 다시 매긴다 (캐시 지역성).</summary>
    static Node[] Compact(List<Node> nodes, ref int start)
    {
        var map = new int[nodes.Count];
        Array.Fill(map, -1);
        var order = new List<int>();
        var q = new Queue<int>();
        map[start] = 0; order.Add(start); q.Enqueue(start);
        void Visit(int t)
        {
            if (t < 0 || map[t] >= 0) return;
            map[t] = order.Count; order.Add(t); q.Enqueue(t);
        }
        while (q.Count > 0)
        {
            var node = nodes[q.Dequeue()];
            if (node.Op is Op.Halt or Op.Loop) continue;
            Visit(node.Next);
            Visit(node.Alt);
            Visit(node.Alt2);
        }
        var result = new Node[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            var node = nodes[order[i]];
            if (node.Next >= 0) node.Next = map[node.Next];
            if (node.Alt >= 0) node.Alt = map[node.Alt];
            if (node.Alt2 >= 0) node.Alt2 = map[node.Alt2];
            result[i] = node;
        }
        start = 0;
        return result;
    }
}
