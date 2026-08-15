using System.Windows;

namespace FishFillets.Editor;

/// <summary>
/// Entry point. The application object is created here rather than through an
/// App.xaml ApplicationDefinition, so the whole UI stays plain C#.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return application.Run(new EditorWindow());
    }
}
