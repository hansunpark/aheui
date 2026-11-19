using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aheui;

/// <summary>버퍼링된 UTF-8 출력.</summary>
public sealed class Output
{
    readonly Stream _s;
    readonly byte[] _buf = new byte[1 << 16];
    int _pos;

    public Output(Stream s) => _s = s;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Ensure(int n)
    {
        if (_pos + n > _buf.Length) Flush();
    }

    public void Flush()
    {
        if (_pos == 0) return;
        _s.Write(_buf, 0, _pos);
        _pos = 0;
        _s.Flush();
    }

    public void WriteLong(long v)
    {
        Ensure(24);
        v.TryFormat(_buf.AsSpan(_pos), out int w);
        _pos += w;
    }

    public void WriteBig(BigInteger v)
    {
        var s = v.ToString();
        Ensure(s.Length);
        if (s.Length > _buf.Length) { Flush(); _s.Write(System.Text.Encoding.ASCII.GetBytes(s)); return; }
        for (int i = 0; i < s.Length; i++) _buf[_pos++] = (byte)s[i];
    }

    /// <summary>유니코드 코드값 출력. 유효하지 않은 값은 U+FFFD로 출력한다.</summary>
    public void WriteCodepoint(long cp)
    {
        if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) cp = 0xFFFD;
        int c = (int)cp;
        Ensure(4);
        var b = _buf;
        if (c < 0x80) b[_pos++] = (byte)c;
        else if (c < 0x800)
        {
            b[_pos++] = (byte)(0xC0 | (c >> 6));
            b[_pos++] = (byte)(0x80 | (c & 0x3F));
        }
        else if (c < 0x10000)
        {
            b[_pos++] = (byte)(0xE0 | (c >> 12));
            b[_pos++] = (byte)(0x80 | ((c >> 6) & 0x3F));
            b[_pos++] = (byte)(0x80 | (c & 0x3F));
        }
        else
        {
            b[_pos++] = (byte)(0xF0 | (c >> 18));
            b[_pos++] = (byte)(0x80 | ((c >> 12) & 0x3F));
            b[_pos++] = (byte)(0x80 | ((c >> 6) & 0x3F));
            b[_pos++] = (byte)(0x80 | (c & 0x3F));
        }
    }
}

/// <summary>버퍼링된 UTF-8 입력.</summary>
public sealed class Input
{
    readonly Stream _s;
    readonly byte[] _buf = new byte[1 << 16];
    int _pos, _len;
    readonly Output? _flushBeforeRead;

    /// <summary>long 범위를 넘는 숫자 입력은 여기에 보관했다가 큰 정수 모드에서 꺼낸다.</summary>
    public BigInteger? Pending;

    public Input(Stream s, Output? flushBeforeRead)
    {
        _s = s;
        _flushBeforeRead = flushBeforeRead;
    }

    int Peek()
    {
        if (_pos < _len) return _buf[_pos];
        _flushBeforeRead?.Flush(); // 대화형 실행: 입력을 기다리기 전에 출력부터 보여준다
        _len = _s.Read(_buf, 0, _buf.Length);
        _pos = 0;
        return _len > 0 ? _buf[0] : -1;
    }

    int Next()
    {
        int b = Peek();
        if (b >= 0) _pos++;
        return b;
    }

    /// <summary>문자 하나를 읽어 코드값을 돌려준다. EOF면 -1.</summary>
    public long ReadChar()
    {
        int b0 = Next();
        if (b0 < 0) return -1;
        if (b0 < 0x80) return b0;
        int need, cp;
        if ((b0 & 0xE0) == 0xC0) { need = 1; cp = b0 & 0x1F; }
        else if ((b0 & 0xF0) == 0xE0) { need = 2; cp = b0 & 0x0F; }
        else if ((b0 & 0xF8) == 0xF0) { need = 3; cp = b0 & 0x07; }
        else return 0xFFFD;
        for (int i = 0; i < need; i++)
        {
            int b = Peek();
            if (b < 0 || (b & 0xC0) != 0x80) return 0xFFFD;
            _pos++;
            cp = (cp << 6) | (b & 0x3F);
        }
        return cp;
    }

    /// <summary>
    /// 정수 하나를 읽는다: 앞 공백을 건너뛰고 [+-]?[0-9]+.
    /// EOF이거나 숫자가 없으면 -1 (아희 표준에서 정의되지 않은 동작).
    /// long 범위를 넘으면 false를 돌려주고 값은 Pending에 둔다.
    /// </summary>
    public bool ReadNumber(out long value)
    {
        value = -1;
        int b;
        while ((b = Peek()) is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') _pos++;
        bool neg = false;
        if (b is '+' or '-')
        {
            neg = b == '-';
            _pos++;
            b = Peek();
        }
        if (b < '0' || b > '9') return true;
        var digits = new System.Text.StringBuilder();
        while ((b = Peek()) >= '0' && b <= '9') { digits.Append((char)b); _pos++; }
        var s = (neg ? "-" : "") + digits;
        if (long.TryParse(s, out value)) return true;
        Pending = BigInteger.Parse(s);
        return false;
    }
}

/// <summary>콘솔/파이프가 아닌 문자열 입출력이 필요할 때 (테스트, 벤치마크 등).</summary>
public static class Streams
{
    public static Stream FromString(string s) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(s));
}
