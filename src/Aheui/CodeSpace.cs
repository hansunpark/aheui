using System.Text;

namespace Aheui;

/// <summary>한글 음절 분해와 아희 명령 표.</summary>
public static class Hangul
{
    public const int First = 0xAC00, Last = 0xD7A3;

    public static bool IsSyllable(int c) => c >= First && c <= Last;

    public static (int cho, int jung, int jong) Decompose(int c)
    {
        int i = c - First;
        return (i / 588, i % 588 / 28, i % 28);
    }

    // 초성 번호
    public const int Giyeok = 0, SsangGiyeok = 1, Nieun = 2, Digeut = 3, SsangDigeut = 4, Rieul = 5,
        Mieum = 6, Bieup = 7, SsangBieup = 8, Siot = 9, SsangSiot = 10, Ieung = 11, Jieut = 12,
        SsangJieut = 13, Chieut = 14, Kieuk = 15, Tieut = 16, Pieup = 17, Hieut = 18;

    // 종성 번호 (0 = 받침 없음)
    public const int JongIeung = 21, JongHieut = 27;

    /// <summary>ㅂ 명령이 넣는 값 = 받침의 획수. ㅇ/ㅎ 받침은 입력 명령이라 쓰이지 않는다.</summary>
    public static readonly int[] Strokes =
    {
        0, 2, 4, 4, 2, 5, 5, 3, 5, 7, 9, 9, 7, 9, 9, 8, 4, 4, 6, 2, 4, 0, 3, 4, 3, 4, 4, 0,
    };

    /// <summary>
    /// 방향 코드: 하위 2비트 = 0 위, 1 아래, 2 왼쪽, 3 오른쪽 / 비트 2 = 2칸 이동.
    /// 반대 방향은 d ^ 1.
    /// </summary>
    public const int Up = 0, Down = 1, Left = 2, Right = 3, Fast = 4;

    public static bool IsHorizontal(int d) => (d & 2) != 0;

    /// <summary>홀소리(중성)가 현재 방향 d를 어떻게 바꾸는지.</summary>
    public static int ApplyVowel(int jung, int d) => jung switch
    {
        0 => Right,            // ㅏ
        2 => Right | Fast,     // ㅑ
        4 => Left,             // ㅓ
        6 => Left | Fast,      // ㅕ
        8 => Up,               // ㅗ
        12 => Up | Fast,       // ㅛ
        13 => Down,            // ㅜ
        17 => Down | Fast,     // ㅠ
        18 => IsHorizontal(d) ? d : d ^ 1, // ㅡ: 세로 이동만 반사
        20 => IsHorizontal(d) ? d ^ 1 : d, // ㅣ: 가로 이동만 반사
        19 => d ^ 1,           // ㅢ: 모두 반사
        _ => d,                // ㅐ ㅔ ㅒ ㅖ ㅘ ㅙ ㅚ ㅝ ㅞ ㅟ: 방향 유지
    };

    /// <summary>초성이 실제로 무언가를 하는 명령인지 (ㅅ은 컴파일 단계에서 정적으로 처리).</summary>
    public static bool IsCommand(int cho) => cho switch
    {
        Giyeok or SsangGiyeok or Ieung or SsangJieut or Kieuk or Siot => false,
        _ => true,
    };
}

/// <summary>코드 공간. 각 줄은 자기 길이를 가지며, 줄 밖의 칸은 빈칸(아무것도 안 함)으로 취급한다.</summary>
public sealed class CodeSpace
{
    public readonly int[][] Rows;
    public int Height => Rows.Length;
    public readonly int MaxWidth;

    public CodeSpace(string source)
    {
        if (source.Length > 0 && source[0] == '﻿') source = source[1..];
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        // 끝의 빈 줄들은 의미가 없으므로 제거 (파일 끝 개행 때문에 높이가 달라지지 않도록)
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        Rows = new int[lines.Count][];
        for (int y = 0; y < lines.Count; y++)
        {
            var cps = new List<int>(lines[y].Length);
            foreach (var r in lines[y].EnumerateRunes()) cps.Add(r.Value);
            Rows[y] = cps.ToArray();
            MaxWidth = Math.Max(MaxWidth, Rows[y].Length);
        }
    }

    public bool IsEmpty => Rows.Length == 0 || MaxWidth == 0;

    public int At(int x, int y)
    {
        var row = Rows[y];
        return x < row.Length ? row[x] : ' ';
    }

    /// <summary>
    /// 방향 d로 한 번 이동. 코드 공간 밖으로 나가면 반대편 끝으로 간다.
    /// 가로는 그 줄의 길이, 세로는 전체 줄 수 기준.
    /// </summary>
    public (int x, int y) Move(int x, int y, int d)
    {
        int step = (d & Hangul.Fast) != 0 ? 2 : 1;
        switch (d & 3)
        {
            case Hangul.Up:
                y -= step;
                if (y < 0) y = Height - 1;
                break;
            case Hangul.Down:
                y += step;
                if (y >= Height) y = 0;
                break;
            case Hangul.Left:
            {
                int w = Rows[y].Length;
                if (w == 0) break;
                x -= step;
                if (x < 0) x = w - 1;
                break;
            }
            default:
            {
                int w = Rows[y].Length;
                if (w == 0) break;
                x += step;
                if (x >= w) x = 0;
                break;
            }
        }
        return (x, y);
    }

    public static string CellToString(int c) => c < 0x10000 ? ((char)c).ToString() : char.ConvertFromUtf32(c);
}
