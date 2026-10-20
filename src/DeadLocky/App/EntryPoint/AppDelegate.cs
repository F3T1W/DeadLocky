using DeadLocky.App.Composition;
using DeadLocky.App.Ui;
using DeadLocky.Pages.Launcher.Ui;

namespace DeadLocky.App.EntryPoint;

[Register("AppDelegate")]
internal sealed class AppDelegate : NSApplicationDelegate
{
    private bool _terminationPending;
    private LauncherWindowController? _mainWindowController;

    public override void DidFinishLaunching(NSNotification notification)
    {
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        ApplicationMenu.Install();
        _mainWindowController = LauncherComposition.CreateLauncher();
        ApplicationMenu.ConfigureEnvironmentSetup(_mainWindowController.ShowRuntimeSetupAsync);

#if DEBUG
        ApplicationMenu.ConfigureSetupTest(_mainWindowController.TestRuntimeSetupAsync);
#endif

        _mainWindowController.ShowWindow(this);
        _mainWindowController.StartUpdates();
        NSApplication.SharedApplication.Activate();
    }

    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        if (_mainWindowController?.IsUpdating != true)
        {
            return NSApplicationTerminateReply.Now;
        }

        if (_terminationPending)
        {
            return NSApplicationTerminateReply.Later;
        }

        _terminationPending = true;
        _ = FinishTerminationAsync(sender);
        return NSApplicationTerminateReply.Later;
    }

    private async Task FinishTerminationAsync(NSApplication sender)
    {
        try
        {
            await _mainWindowController!.StopUpdatesAsync();
        }
        finally
        {
            sender.ReplyToApplicationShouldTerminate(true);
        }
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender)
    {
        return true;
    }

    public override void WillTerminate(NSNotification notification)
    {
        _mainWindowController?.Dispose();
        _mainWindowController = null;
    }
}
