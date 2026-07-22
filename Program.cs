using UsageAI.Services;
using UsageAI.UI;

namespace UsageAI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--render-preview", StringComparer.OrdinalIgnoreCase))
        {
            RenderPreview(args);
            return;
        }

        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
        {
            RunDiagnosticsAsync().GetAwaiter().GetResult();
            return;
        }

        Application.Run(new UsageApplicationContext(new CodexUsageClient()));
    }

    private static async Task RunDiagnosticsAsync()
    {
        try
        {
            var snapshot = await new CodexUsageClient().GetUsageAsync();
            Console.WriteLine(snapshot.ToDiagnosticJson());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }
    }

    private static void RenderPreview(string[] args)
    {
        var flagIndex = Array.FindIndex(args, argument =>
            argument.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        var outputPath = flagIndex >= 0 && flagIndex + 1 < args.Length
            ? Path.GetFullPath(args[flagIndex + 1])
            : Path.Combine(Environment.CurrentDirectory, "usageai-preview.png");

        using var form = new UsagePopupForm();
        form.SetSnapshot(new Models.UsageSnapshot(
            "Plus",
            null,
            new Models.UsageWindow("Weekly", 3, DateTimeOffset.Now.AddDays(6).AddHours(23), 10_080),
            "0",
            3,
            DateTimeOffset.Now));
        form.Location = new Point(-10_000, -10_000);
        form.Show();
        Application.DoEvents();
        form.PerformLayout();
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
    }
}
