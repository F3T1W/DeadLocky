using DeadLocky.Features.ReportProblem.Api;
using DeadLocky.Features.ReportProblem.Model;
using DeadLocky.Shared.Api.Diagnostics;
using DeadLocky.Shared.Ui;
using UniformTypeIdentifiers;

namespace DeadLocky.Features.ReportProblem.Ui;

public sealed class ReportWindowController : NSWindowController
{
    private readonly ReportArchive _archive;
    private readonly ReportClient _client;
    private readonly ReportMetadata _metadata;
    private readonly NSTextView _message = new() { RichText = false, Font = NSFont.SystemFontOfSize(13)! };
    private readonly NSTextField _email = new() { PlaceholderString = "Email for a reply (optional)", StringValue = "" };
    private readonly NSTextField _status = LauncherStyle.Label("", 12, NSFontWeight.Regular, LauncherStyle.SecondaryText);
    private readonly NSButton _send = LauncherStyle.Button("Send report", true);
    private readonly NSButton _export = LauncherStyle.Button("Export ZIP");
    private readonly NSButton _cancel = LauncherStyle.Button("Cancel");
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private ProblemReport? _draft;
    private bool _disposed;
    private NSSavePanel? _savePanel;

    public ReportWindowController(ReportArchive archive, ReportClient client, ReportMetadata metadata)
        : base(new NSWindow(new CGRect(0, 0, 620, 520),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable,
            NSBackingStore.Buffered, false))
    {
        _archive = archive;
        _client = client;
        _metadata = metadata;
        Window.Title = "Report a problem";
        Window.MinSize = new CGSize(580, 480);
        var content = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 14,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        NSView root = Window.ContentView!;
        root.AddSubview(content);
        NSTextField privacy = LauncherStyle.Label(
            "The ZIP contains your description, optional reply email, app/system versions and redacted recent technical logs. "
            + "Send report transfers it through Google Apps Script to the developer (f3t1w@icloud.com). "
            + "Avoid including passwords or personal information in your description.", 12, NSFontWeight.Regular);
        privacy.PreferredMaxLayoutWidth = 572;
        privacy.MaximumNumberOfLines = 0;
        privacy.UsesSingleLineMode = false;
        privacy.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            BorderType = NSBorderType.BezelBorder,
            DocumentView = _message,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _message.Frame = new CGRect(0, 0, 560, 180);
        _message.AutoresizingMask = NSViewResizingMask.WidthSizable;
        _message.VerticallyResizable = true;
        _message.HorizontallyResizable = false;
        _message.TextContainer!.WidthTracksTextView = true;
        _email.TranslatesAutoresizingMaskIntoConstraints = false;
        _status.PreferredMaxLayoutWidth = 572;
        _status.MaximumNumberOfLines = 0;
        _status.UsesSingleLineMode = false;
        _status.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        var buttons = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Horizontal, Spacing = 10 };
        foreach (NSButton button in new[] { _send, _export, _cancel }) { buttons.AddArrangedSubview(button); }
        foreach (NSView view in new NSView[]
                 { LauncherStyle.Label("Describe what happened", 16, NSFontWeight.Semibold), privacy, scroll, _email, buttons, _status })
        {
            content.AddArrangedSubview(view);
        }

        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 24),
            content.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -24),
            content.TopAnchor.ConstraintEqualTo(root.TopAnchor, 24),
            content.BottomAnchor.ConstraintLessThanOrEqualTo(root.BottomAnchor, -24),
            privacy.WidthAnchor.ConstraintEqualTo(content.WidthAnchor),
            scroll.WidthAnchor.ConstraintEqualTo(content.WidthAnchor), scroll.HeightAnchor.ConstraintEqualTo(180),
            _email.WidthAnchor.ConstraintEqualTo(content.WidthAnchor),
            _status.WidthAnchor.ConstraintEqualTo(content.WidthAnchor)
        ]);
        _send.Activated += OnSend;
        _export.Activated += OnExport;
        _cancel.Activated += OnCancel;
        Window.WillClose += OnClosed;
        Render();
    }

    public bool IsBusy { get; private set; }
    public bool IsOpen { get; private set; }
    public event EventHandler? StateChanged;

    public void ShowReport()
    {
        IsOpen = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        Window.Center();
        ShowWindow(this);
        Window.MakeKeyAndOrderFront(this);
        _ = Window.MakeFirstResponder(_message);
    }

    private void OnSend(object? sender, EventArgs args) { Start(false); }
    private void OnExport(object? sender, EventArgs args) { Start(true); }
    private void OnCancel(object? sender, EventArgs args)
    {
        if (IsBusy) { _cancellation?.Cancel(); }
        else { Window.Close(); }
    }
    private void OnClosed(object? sender, EventArgs args)
    {
        IsOpen = false;
        _cancellation?.Cancel();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Start(bool export)
    {
        if (IsBusy || _disposed) { return; }
        string message = _message.Value.Trim();
        string email = _email.StringValue.Trim();
        try { ProblemReport.Validate(message, email); }
        catch (ArgumentException exception) { _status.StringValue = exception.Message; return; }
        if (_draft is null || _draft.Message != message || _draft.Email != email)
        {
            _draft = new ProblemReport(Guid.NewGuid(), message, email, _metadata);
        }

        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        Render();
        StateChanged?.Invoke(this, EventArgs.Empty);
        _operation = RunAsync(_draft, export, _cancellation.Token);
    }

    private async Task RunAsync(ProblemReport report, bool export, CancellationToken token)
    {
        string? path = null;
        try
        {
            _status.StringValue = "Preparing a private diagnostic snapshot…";
            path = await AppLog.RunAsync("report.archive", () => _archive.CreateAsync(report, token));
            if (export)
            {
                using NSSavePanel panel = NSSavePanel.SavePanel;
                panel.AllowedContentTypes = [UTTypes.Zip];
                panel.NameFieldStringValue = Path.GetFileName(path);
                panel.CanCreateDirectories = true;
                var completion = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
                panel.BeginSheet(Window, response => _ = completion.TrySetResult(response));
                _savePanel = panel;
                await using CancellationTokenRegistration registration = token.Register(CancelSavePanel);
                nint result = await completion.Task;
                token.ThrowIfCancellationRequested();
                if (result != (nint)NSModalResponse.OK) { throw new OperationCanceledException(token); }
                string destination = panel.Url.Path ?? throw new IOException("No export destination selected.");
                await ReportArchive.SaveAsync(path, destination, token);
                _status.StringValue = "ZIP saved. It stays at the location you selected until you delete it.";
                AppLog.Event("report.export", "saved");
            }
            else
            {
                _status.StringValue = "Sending through Google…";
                await AppLog.RunAsync("report.send", () => _client.SendAsync(report, path, token));
                _status.StringValue = $"Report {report.Id:D} accepted for email sending. Inbox delivery is not guaranteed.";
            }
        }
        catch (OperationCanceledException)
        {
            _status.StringValue = "Cancelled. If sending had started, delivery is unconfirmed. A retry keeps the same report ID.";
            AppLog.Event("report.operation", "cancelled");
        }
        catch (Exception exception)
        {
            _status.StringValue = $"Report {report.Id:D}: {exception.Message} Your text has been kept.";
            // Form contents and payloads intentionally never enter the ordinary log.
            AppLog.Event("report.operation", "failed", exception);
        }
        finally
        {
            _savePanel = null;
            if (path is not null) { await Task.Run(() => ReportArchive.Delete(path), CancellationToken.None); }
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
            if (!_disposed) { Render(); StateChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    private void CancelSavePanel()
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_cancellation?.IsCancellationRequested == true) { _savePanel?.Cancel(this); }
        });
    }

    private void Render()
    {
        _send.Enabled = !IsBusy && _client.IsConfigured;
        _export.Enabled = !IsBusy;
        _message.Editable = !IsBusy;
        _email.Enabled = !IsBusy;
        _cancel.Title = IsBusy ? "Cancel operation" : "Close";
        if (!_client.IsConfigured && _status.StringValue.Length == 0)
        { _status.StringValue = "Sending is not configured in this build. Export ZIP is available."; }
    }

    public async Task StopAsync()
    {
        if (_cancellation is not null) { await _cancellation.CancelAsync(); }
        await _operation;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _send.Activated -= OnSend;
            _export.Activated -= OnExport;
            _cancel.Activated -= OnCancel;
            Window.WillClose -= OnClosed;
            _client.Dispose();
            StateChanged = null;
        }
        base.Dispose(disposing);
    }
}
