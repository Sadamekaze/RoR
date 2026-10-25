using RuleOfRose.Translator;

// Entry point eksplisit (bukan top-level statements) supaya Main tetap
// [STAThread] — dialog WinForms membutuhkan thread STA.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Tampilkan exception UI thread sebagai dialog, jangan crash senyap.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        Application.Run(new TranslatorGui(args));
    }

    private static void ShowFatal(Exception? ex)
    {
        string message = ex?.Message ?? "Kesalahan tidak dikenal.";
        try
        {
            MessageBox.Show(message, "Rule of Rose Translator",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Jangan tutupi kegagalan asli bila MessageBox sendiri gagal.
        }
    }
}
