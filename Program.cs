namespace WinFormsApp1;

/// <summary>
/// 程序入口。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var form = new MainForm();

        // 支持通过“打开方式”或命令行直接传入图片路径
        string? firstImage = args.FirstOrDefault(File.Exists);
        if (firstImage is not null)
        {
            form.InitialImagePath = firstImage;
        }

        Application.Run(form);
    }
}
