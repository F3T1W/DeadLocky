using DeadLocky.Entities.Mod.Model;

namespace DeadLocky.Features.ManageMods.Ui;

internal sealed class CameraModPicker : NSView
{
    private readonly NSButton _all = new(new CGRect(0, 324, 500, 26)) { Title = "Select all" };
    private readonly List<NSButton> _checks = [];
    private readonly InstalledMod[] _packages;
    private readonly NSButton _repair;
    private readonly NSButton _restore;
    private readonly NSScrollView _scroll;
    private readonly NSStackView _stack;

    public CameraModPicker(InstalledMod[] packages, NSButton repair, NSButton restore)
        : base(new CGRect(0, 0, 500, 354))
    {
        _packages = packages;
        _repair = repair;
        _restore = restore;
        _all.SetButtonType(NSButtonType.Switch);
        _all.Activated += OnSelectAll;
        _stack = new FlippedStack
        {
            Frame = new CGRect(0, 0, 500, packages.Length * 52),
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 4
        };
        foreach (InstalledMod package in packages)
        {
            var check = new NSButton
            {
                Title = package.Name + (package.CameraRepairOriginalPackageId.HasValue ? " · repaired" : ""),
                ToolTip = package.Name + " — " + package.DownloadName,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            check.SetButtonType(NSButtonType.Switch);
            check.WidthAnchor.ConstraintEqualTo(480).Active = true;
            check.HeightAnchor.ConstraintEqualTo(44).Active = true;
            check.Activated += OnToggle;
            _checks.Add(check);
            _stack.AddArrangedSubview(check);
        }

        _scroll = new NSScrollView(new CGRect(0, 0, 500, 314))
        {
            DocumentView = _stack,
            HasVerticalScroller = true
        };
        AddSubview(_all);
        AddSubview(_scroll);
        UpdateSelection();
    }

    public Guid[] Selected =>
        [.. _packages.Where((_, index) => _checks[index].State == NSCellStateValue.On).Select(static package => package.Id)];

    private void OnSelectAll(object? sender, EventArgs args)
    {
        foreach (NSButton check in _checks) { check.State = _all.State; }
        UpdateSelection();
    }

    private void OnToggle(object? sender, EventArgs args)
    {
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        Guid[] selected = Selected;
        _all.State = selected.Length == _checks.Count ? NSCellStateValue.On : NSCellStateValue.Off;
        _repair.Enabled = selected.Length > 0;
        _repair.Title = $"Repair selected ({selected.Length})";
        _restore.Enabled = selected.Length > 0 && _packages.Where(package => selected.Contains(package.Id))
            .All(static package => package.CameraRepairOriginalPackageId.HasValue);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _all.Activated -= OnSelectAll;
            foreach (NSButton check in _checks)
            {
                check.Activated -= OnToggle;
                check.Dispose();
            }

            _all.Dispose();
            _scroll.Dispose();
            _stack.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class FlippedStack : NSStackView
    {
        public override bool IsFlipped => true;
    }
}
