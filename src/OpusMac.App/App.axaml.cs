using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace OpusMac.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow(Program.Options);
            desktop.MainWindow = main;
            Program.Broker?.Start(async request =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!main.QueueOpenPaths(request.Paths, request.Activate))
                        throw new InvalidOperationException("The existing window is closing and did not accept the folder request.");
                });
            });
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, arguments) =>
                {
                    if (arguments is FileActivatedEventArgs files)
                        main.QueueOpenPaths(files.Files.Select(file => file.TryGetLocalPath()).OfType<string>());
                    else if (arguments is ProtocolActivatedEventArgs protocol && Uri.TryCreate(protocol.Uri.ToString(), UriKind.Absolute, out var uri) && uri.IsFile)
                        main.QueueOpenPaths([uri.LocalPath]);
                    else if (arguments.Kind == ActivationKind.Reopen) main.QueueOpenPaths([]);
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
