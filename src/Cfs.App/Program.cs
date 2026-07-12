using Cfs.Core;

namespace Cfs.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CfsDiagnostics.Logger.WriteStartup();
        AppLog.Write("Starting Cfs.App argumentCount=" + args.Length);
        ApplicationConfiguration.Initialize();

        var acknowledgement = new CfsBetaAcknowledgement(CfsBetaAcknowledgement.DefaultPath);
        if (acknowledgement.ShouldShow(CfsProductInfo.AcknowledgementKey))
        {
            var result = MessageBox.Show(
                CfsProductInfo.BetaSafetyWarning + "\n\nSelect OK to acknowledge this warning and continue. Select Cancel to exit CFS.",
                CfsProductInfo.DisplayName + " — Important Beta Safety Warning",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.OK)
            {
                AppLog.Write("Beta safety warning was not acknowledged; exiting before normal use.");
                return;
            }

            acknowledgement.Acknowledge(CfsProductInfo.AcknowledgementKey);
            AppLog.Write("Beta safety warning acknowledged for " + CfsProductInfo.AcknowledgementKey);
        }

        Application.Run(new MainForm(args.Length > 0 ? args[0] : null, openInitialArchiveInExplorer: args.Length > 0));
    }
}
