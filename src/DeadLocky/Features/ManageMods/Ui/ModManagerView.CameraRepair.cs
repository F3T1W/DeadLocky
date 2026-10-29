using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.ManageMods.Model;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.ManageMods.Ui;

public sealed partial class ModManagerView
{
    private const string HideCameraWarningKey = "HideExperimentalCameraRepairWarning";

    private void OnRepairCamera(object? sender, EventArgs args)
    {
        _ = OperateAsync(async token =>
        {
            string directory = _gameDirectory() ?? throw new IOException("Select your Deadlock installation first.");
            if (!await ConfirmCameraRepairAsync())
            {
                _status.StringValue = "Camera repair cancelled.";
                return;
            }

            (Guid[] selected, bool restore) = await ChooseCameraModsAsync();
            if (selected.Length == 0)
            {
                _status.StringValue = "Camera repair cancelled.";
                return;
            }

            _status.StringValue = restore ? "Restoring originals…" : "Checking and repairing selected mods…";
            if (restore)
            {
                await _workspace.RestoreCameraOriginalsAsync(directory, selected, token);
                _status.StringValue = "Original mods restored. Changes apply on Play.";
                return;
            }

            CameraRepairResult[] results = await _workspace.RepairCamerasAsync(directory, selected, token);
            int repaired = results.Count(static result => result.Repaired);
            _status.StringValue = $"Repaired {repaired} of {results.Length} mods. Changes apply on Play.";
            using var alert = new NSAlert();
            alert.MessageText = "Camera repair results";
            alert.InformativeText = "Original mods have been retained. Restart Deadlock through Play and test walking, " +
                                    "aiming and crouching in Hideout. Reinstalling a mod can replace the repair.";
            using NSScrollView report = CameraReport(results);
            alert.AccessoryView = report;
            LauncherStyle.ApplyButtonStyle(alert.AddButton("Done"));
            _ = await alert.BeginSheetAsync(_owner);
        }, "");
    }

    private async Task<bool> ConfirmCameraRepairAsync()
    {
        if (NSUserDefaults.StandardUserDefaults.BoolForKey(HideCameraWarningKey))
        {
            return true;
        }

        using var alert = new NSAlert();
        alert.AlertStyle = NSAlertStyle.Warning;
        alert.MessageText = "Experimental camera repair";
        alert.InformativeText = "Use this feature at your own risk. It attempts to repair old hero skins using camera " +
                                "data from your installed Deadlock version. Some mods are unsupported, and a successful " +
                                "file check does not guarantee correct behavior in game. Original mods are saved for " +
                                "restoring. Close Deadlock before continuing.";
        alert.ShowsSuppressionButton = true;
        alert.SuppressionButton.Title = "Don't show this warning again";
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Continue"));
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Cancel"));
        if ((nint)await alert.BeginSheetAsync(_owner) != 1000)
        {
            return false;
        }

        if (alert.SuppressionButton.State == NSCellStateValue.On)
        {
            NSUserDefaults.StandardUserDefaults.SetBool(true, HideCameraWarningKey);
        }

        return true;
    }

    private async Task<(Guid[] Selected, bool Restore)> ChooseCameraModsAsync()
    {
        InstalledMod[] packages =
        [
            .. _library.ActiveProfile.Mods.Select(mod => _library.Packages.Single(package => package.Id == mod.PackageId))
        ];
        using var alert = new NSAlert();
        alert.MessageText = "Choose installed mods";
        alert.InformativeText = $"Profile: {_library.ActiveProfile.Name}. Select one or more mods with a broken camera. " +
                                "Shared copies are updated in every profile that uses them. Originals remain available " +
                                "through Restore originals. Changes apply on the next Play.";
        NSButton repair = alert.AddButton("Repair selected");
        LauncherStyle.ApplyButtonStyle(repair);
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Cancel"));
        NSButton restore = alert.AddButton("Restore originals");
        LauncherStyle.ApplyButtonStyle(restore);
        using var picker = new CameraModPicker(packages, repair, restore);
        alert.AccessoryView = picker;
        nint response = (nint)await alert.BeginSheetAsync(_owner);
        return response is 1000 or 1002 ? (picker.Selected, response == 1002) : ([], false);
    }

    private static NSScrollView CameraReport(CameraRepairResult[] results)
    {
        var report = new NSTextView(new CGRect(0, 0, 500, Math.Max(300, results.Length * 100)))
        {
            Editable = false,
            Selectable = true,
            Font = NSFont.SystemFontOfSize(13)!,
            Value = string.Join("\n\n", results.Select(static result => result.Name + "\n" + result.Detail))
        };
        return new NSScrollView(new CGRect(0, 0, 500, 300))
        {
            DocumentView = report,
            HasVerticalScroller = true
        };
    }
}
