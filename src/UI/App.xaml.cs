using Microsoft.UI.Xaml;
namespace Pome.UI;
public partial class App : Application
{
    private Window? window;
    public App()
    {
        UnhandledException += (_, e) => RecordFailure(e.Exception);
        try { InitializeComponent(); }
        catch (Exception e) { RecordFailure(e); throw; }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception e) { RecordFailure(e); throw; }
    }
    private static void RecordFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "startup-error.txt"), Pome.Infrastructure.Redaction.Clean(exception.ToString()));
        }
        catch { System.Diagnostics.Debug.WriteLine(exception); }
    }
}
