using System.Runtime.CompilerServices;
using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.ManageMods.Api;
using DeadLocky.Features.ManageMods.Model;
using DeadLocky.Shared.Api.Diagnostics;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.ManageMods.Ui;

public sealed partial class ModManagerView : NSView
{
    private readonly ModCatalogClient _catalog;
    private readonly NSStackView _catalogRows = Stack();
    private readonly NSPopUpButton _categories = Popup();
    private readonly NSButton _create = LauncherStyle.Button("New profile");
    private readonly NSPopUpButton _heroes = Popup();
    private readonly Dictionary<string, byte[]> _imageCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _imageGate = new(4, 4);
    private readonly NSStackView _libraryRows = Stack();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly NSButton _next = LauncherStyle.Button("Next");

    private readonly NSButton _nsfw = new()
    {
        Title = "Include NSFW",
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly NSWindow _owner;

    private readonly NSTextField _pageLabel =
        LauncherStyle.Label("", 12, NSFontWeight.Regular, LauncherStyle.SecondaryText);

    private readonly NSButton _previous = LauncherStyle.Button("Previous");

    private readonly NSTextField _profileSummary =
        LauncherStyle.Label("", 12, NSFontWeight.Regular, LauncherStyle.SecondaryText);

    private readonly NSPopUpButton _profiles = Popup();
    private readonly NSButton _refresh = LauncherStyle.Button("Refresh");
    private readonly NSButton _rename = LauncherStyle.Button("Rename");

    private readonly NSSearchField _search = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly NSPopUpButton _sort = Popup();

    private readonly NSTextField _status =
        LauncherStyle.Label("Loading catalog…", 12, NSFontWeight.Regular, LauncherStyle.SecondaryText);

    private readonly ModWorkspace _workspace;
    private bool _disposed;
    private bool _gameBusy;
    private CancellationTokenSource _images = new();
    private ModLibrary _library = ModLibrary.Empty;
    private CatalogMod[] _mods;
    private int _page;
    private bool _refreshing;
    private CancellationTokenSource? _searchDelay;

    public ModManagerView(NSWindow owner, ModWorkspace workspace, ModCatalogClient catalog)
    {
        _owner = owner;
        _workspace = workspace;
        _catalog = catalog;
        TranslatesAutoresizingMaskIntoConstraints = false;
        BuildLayout();
        _profiles.Activated += OnProfileSelected;
        _create.Activated += OnCreate;
        _rename.Activated += OnRename;
        _refresh.Activated += OnRefresh;
        _search.Changed += OnSearch;
        _categories.Activated += OnFilter;
        _heroes.Activated += OnFilter;
        _sort.Activated += OnFilter;
        _nsfw.SetButtonType(NSButtonType.Switch);
        _nsfw.Activated += OnFilter;
        _previous.Activated += OnPrevious;
        _next.Activated += OnNext;
        _sort.AddItems(["Recently updated", "Most downloaded", "Most liked", "Name"]);
        _mods = catalog.ReadCached();
        ReloadLibrary();
        PopulateFilters();
        RenderCatalog();
        _ = RefreshAsync();
    }

    public bool IsBusy { get; private set; }

    public string ProfileSummary => _library.ActiveProfile.IsVanilla
        ? "Profile: Vanilla · No DeadLocky mods"
        : $"Profile: {_library.ActiveProfile.Name} · " +
          $"{_library.ActiveProfile.Mods.Count(static mod => mod.Enabled)} enabled";

    public event EventHandler? BusyChanged;

    public void SetGameBusy(bool busy)
    {
        if (_gameBusy == busy)
        {
            return;
        }

        _gameBusy = busy;
        RenderLibrary();
        RenderCatalog();
    }

    private void ReloadLibrary()
    {
        _library = _workspace.Read();
        _profiles.RemoveAllItems();
        _profiles.AddItems([.. _library.Profiles.Select(static profile => profile.Name)]);
        _profiles.SelectItem(Array.FindIndex(_library.Profiles, profile => profile.Id == _library.ActiveProfileId));
        RenderLibrary();
    }

    private async Task OperateAsync(Func<CancellationToken, Task> action, string success, [CallerMemberName] string operation = "")
    {
        if (IsBusy || _gameBusy || _disposed)
        {
            return;
        }

        IsBusy = true;
        BusyChanged?.Invoke(this, EventArgs.Empty);
        RenderLibrary();
        RenderCatalog();
        try
        {
            _status.TextColor = LauncherStyle.SecondaryText;
            _status.StringValue = "Working…";
            await AppLog.RunAsync("mods.ui." + operation, () => action(_lifetime.Token),
                AppLog.Data(("profile.id", _library.ActiveProfileId)));
            _status.StringValue = success;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                _status.StringValue = "Operation cancelled.";
            }
        }
        catch (Exception exception)
        {
            AppLog.Event("mods.ui.operation", "failed", exception);
            _status.TextColor = NSColor.SystemRed;
            _status.StringValue = exception.Message;
        }
        finally
        {
            IsBusy = false;
            if (!_disposed)
            {
                ReloadLibrary();
                RenderCatalog();
                BusyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnProfileSelected(object? sender, EventArgs args)
    {
        int index = (int)_profiles.IndexOfSelectedItem;
        if (index < 0 || index >= _library.Profiles.Length)
        {
            return;
        }

        Guid id = _library.Profiles[index].Id;
        _ = OperateAsync(token => _workspace.SelectProfileAsync(id, token),
            "Profile selected. Ready for next launch.");
    }

    private void OnCreate(object? sender, EventArgs args)
    {
        string? name = PromptName("New profile", "My profile");
        if (name is not null)
        {
            _ = OperateAsync(token => _workspace.CreateProfileAsync(name, token), "Profile created.");
        }
    }

    private void OnRename(object? sender, EventArgs args)
    {
        ModProfile profile = _library.ActiveProfile;
        string? name = PromptName("Rename profile", profile.Name);
        if (name is not null)
        {
            _ = OperateAsync(token => _workspace.RenameProfileAsync(profile.Id, name, token), "Profile renamed.");
        }
    }

    private string? PromptName(string title, string initial)
    {
        using var input = new NSTextField(new CGRect(0, 0, 320, 26));
        input.StringValue = initial;
        using var alert = new NSAlert();
        alert.MessageText = title;
        alert.InformativeText = "Choose a unique profile name.";
        alert.AccessoryView = input;
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Save"));
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Cancel"));
        return alert.RunSheetModal(_owner) == 1000 ? input.StringValue : null;
    }

    private void RenderLibrary()
    {
        Clear(_libraryRows);
        bool editable = !IsBusy && !_gameBusy;
        ModProfile profile = _library.ActiveProfile;
        _profiles.Enabled = editable;
        _create.Enabled = editable;
        _rename.Enabled = editable && !profile.IsVanilla;
        _profileSummary.StringValue = _gameBusy ? "Close Deadlock to edit profiles." :
            profile.IsVanilla ? "Always available. No DeadLocky mods." :
            $"{profile.Mods.Count(static mod => mod.Enabled)} of {profile.Mods.Length} enabled · Applies on Play";
        if (profile.Mods.Length == 0)
        {
            NSTextField empty =
                Wrap(
                    profile.IsVanilla
                        ? "Your clean starting point. Create a profile to install mods."
                        : "This profile is empty. Browse the catalog and install your first mod.", 13);
            AddRow(_libraryRows, empty);
        }

        for (int index = 0; index < profile.Mods.Length; index++)
        {
            ProfileMod entry = profile.Mods[index];
            InstalledMod package = _library.Packages.Single(item => item.Id == entry.PackageId);
            var row = new NSView
            {
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            var toggle = new NSButton
            {
                Title = package.Name,
                TranslatesAutoresizingMaskIntoConstraints = false,
                State = entry.Enabled ? NSCellStateValue.On : NSCellStateValue.Off,
                Enabled = editable,
                ToolTip = package.Name + " — " + package.DownloadName
            };
            toggle.SetButtonType(NSButtonType.Switch);
            toggle.Activated += (_, _) =>
                _ = OperateAsync(
                    token => _workspace.SetEnabledAsync(profile.Id, entry.PackageId,
                        toggle.State == NSCellStateValue.On, token), "Profile updated.");
            NSButton remove = LauncherStyle.Button("Remove");
            NSButton up = LauncherStyle.Button("↑");
            NSButton down = LauncherStyle.Button("↓");
            remove.Enabled = editable;
            up.Enabled = editable && index > 0;
            down.Enabled = editable && index < profile.Mods.Length - 1;
            remove.Activated += (_, _) =>
                _ = OperateAsync(token => _workspace.RemoveAsync(profile.Id, entry.PackageId, token),
                    "Removed from this profile.");
            up.Activated += (_, _) =>
                _ = OperateAsync(token => _workspace.MoveAsync(profile.Id, entry.PackageId, -1, token),
                    "Load order updated.");
            down.Activated += (_, _) =>
                _ = OperateAsync(token => _workspace.MoveAsync(profile.Id, entry.PackageId, 1, token),
                    "Load order updated.");
            NSTextField author =
                LauncherStyle.Label(package.Author, 11, NSFontWeight.Regular, LauncherStyle.SecondaryText);
            foreach (NSView view in new NSView[]
                     {
                         toggle,
                         author,
                         remove,
                         up,
                         down
                     }
                    )
            {
                row.AddSubview(view);
            }

            NSLayoutConstraint.ActivateConstraints([
                row.HeightAnchor.ConstraintEqualTo(94), toggle.TopAnchor.ConstraintEqualTo(row.TopAnchor, 4),
                toggle.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
                toggle.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
                author.TopAnchor.ConstraintEqualTo(toggle.BottomAnchor, 3),
                author.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor, 22),
                author.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
                up.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
                up.BottomAnchor.ConstraintEqualTo(row.BottomAnchor, -6),
                down.LeadingAnchor.ConstraintEqualTo(up.TrailingAnchor, 4),
                down.CenterYAnchor.ConstraintEqualTo(up.CenterYAnchor),
                remove.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor),
                remove.CenterYAnchor.ConstraintEqualTo(up.CenterYAnchor)
            ]);
            AddRow(_libraryRows, row);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _images.Cancel();
            _searchDelay?.Cancel();
            _catalog.Dispose();
            _ = _workspace.DisposeAsync().AsTask();
            BusyChanged = null;
            _lifetime.Dispose();
            _images.Dispose();
            _searchDelay?.Dispose();
        }

        base.Dispose(disposing);
    }
}
