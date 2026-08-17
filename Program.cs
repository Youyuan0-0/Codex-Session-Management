namespace CodexSessionHotSync;

static class Program
{
    [STAThread]
    static void Main()
    {
        SqliteRuntime.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => ShowFatalError(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ShowFatalError(args.ExceptionObject as Exception ?? new InvalidOperationException("未知错误"));
        Application.Run(new MainForm());
    }

    private static void ShowFatalError(Exception error)
    {
        MessageBox.Show(
            $"程序遇到未处理错误：\n\n{error.Message}",
            "Codex 会话热同步",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
