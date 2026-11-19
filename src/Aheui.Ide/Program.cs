using System.Text;

namespace Aheui.Ide;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // `aheui-ide --run <파일> [옵션]`: IDE가 네이티브 실행을 위해 자기 자신을 별도 프로세스로 띄울 때 쓰는 모드
        if (args.Length > 0 && args[0] == "--run") return Cli.Run(args[1..]);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault(a => !a.StartsWith('-'))));
        return 0;
    }
}
