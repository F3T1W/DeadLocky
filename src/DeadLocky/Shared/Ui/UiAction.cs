namespace DeadLocky.Shared.Ui;

internal static class UiAction
{
    public static async Task RunAsync(NSWindow? owner, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            using var alert = new NSAlert();
            alert.MessageText = "Unable to complete the action";
            alert.InformativeText = exception.Message;
            LauncherStyle.ApplyButtonStyle(alert.AddButton("Close"));
            _ = owner is not null ? alert.RunSheetModal(owner) : alert.RunModal();
        }
    }
}
