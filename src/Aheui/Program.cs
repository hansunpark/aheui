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
