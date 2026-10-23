using System.Runtime.InteropServices;
using DeadLocky.App.Composition;
using DeadLocky.App.Ui;
using DeadLocky.Features.ReportProblem.Ui;
using DeadLocky.Pages.Launcher.Ui;
using DeadLocky.Shared.Api.Diagnostics;

namespace DeadLocky.App.EntryPoint;

[Register("AppDelegate")]
internal sealed class AppDelegate : NSApplicationDelegate
{
    private bool _terminationPending;
    private ReportWindowController? _reports;
    private LauncherWindowController? _mainWindowController;

    public static void InitializeDiagnostics()
    {
        AppLog.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "DeadLocky"));
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedException;
    }

    public override void DidFinishLaunching(NSNotification notification)
    {
        AppLog.Event("session.environment", $"app={NSBundle.MainBundle.InfoDictionary?["CFBundleShortVersionString"]}; "
            + $"build={NSBundle.MainBundle.InfoDictionary?["CFBundleVersion"]}; os={RuntimeInformation.OSDescription}; "
            + $"runtime={RuntimeInformation.FrameworkDescription}; architecture={RuntimeInformation.ProcessArchitecture}",
            fields: AppLog.Data(("app.bundlePath", NSBundle.MainBundle.BundlePath),
                ("app.nativeAot", !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported),
                ("host.osArchitecture", RuntimeInformation.OSArchitecture.ToString()),
                ("host.processors", Environment.ProcessorCount), ("host.is64BitProcess", Environment.Is64BitProcess),
                ("app.microphoneAuthorization", AVFoundation.AVCaptureDevice.GetAuthorizationStatus(AVFoundation.AVAuthorizationMediaType.Audio).ToString()),
                ("app.microphonePurposeConfigured", !string.IsNullOrWhiteSpace(NSBundle.MainBundle.InfoDictionary?["NSMicrophoneUsageDescription"]?.ToString()))));
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        ApplicationMenu.Install(RequestQuitAsync);
        _mainWindowController = LauncherComposition.CreateLauncher();
        _reports = LauncherComposition.CreateReporter();
        _reports.StateChanged += OnReportStateChanged;
        ApplicationMenu.ConfigureReports(_reports.ShowReport);
        ApplicationMenu.ConfigureEnvironmentSetup(_mainWindowController.ShowRuntimeSetupAsync);

#if DEBUG
        ApplicationMenu.ConfigureSetupTest(_mainWindowController.TestRuntimeSetupAsync);
#endif

        _mainWindowController.ShowWindow(this);
        _mainWindowController.StartUpdates();
        NSApplication.SharedApplication.Activate();
        AppLog.Event("ui.ready", "ready");
    }

    public override NSApplicationTerminateReply ApplicationShouldTerminate(NSApplication sender)
    {
        if (_terminationPending)
        {
            return NSApplicationTerminateReply.Later;
        }

        _terminationPending = true;
        _ = FinishTerminationAsync(sender);
        return NSApplicationTerminateReply.Later;
    }

    private async Task RequestQuitAsync()
    {
        // Cocoa's default terminate action does not dismiss an active save panel.
        if (_reports is not null) { await _reports.StopAsync(); }
        NSApplication.SharedApplication.Terminate(this);
    }

    private async Task FinishTerminationAsync(NSApplication sender)
    {
        try
        {
            if (_reports is not null) { await _reports.StopAsync(); }
            if (_mainWindowController is not null) { await _mainWindowController.StopUpdatesAsync(); }
            await AppLog.StopAsync();
        }
        finally
        {
            sender.ReplyToApplicationShouldTerminate(true);
        }
    }

    private void OnReportStateChanged(object? sender, EventArgs args)
    {
        _mainWindowController?.SetReportBusy(_reports is { IsOpen: true } or { IsBusy: true });
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        AppLog.Event("session.unhandled", "unhandled exception observed", args.ExceptionObject as Exception,
            fields: AppLog.Data(("session.terminating", args.IsTerminating)), level: Microsoft.Extensions.Logging.LogLevel.Critical);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            AppLog.FlushAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception) { /* Fatal shutdown must not wait indefinitely for a failing disk. */ }
    }

    private static void OnUnobservedException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        AppLog.Event("task.unobserved", "unobserved exception observed", args.Exception);
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender)
    {
        return true;
    }

    public override void WillTerminate(NSNotification notification)
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedException;
        if (_reports is not null)
        {
            _reports.StateChanged -= OnReportStateChanged;
            _reports.Dispose();
            _reports = null;
        }
        _mainWindowController?.Dispose();
        _mainWindowController = null;
    }
}
