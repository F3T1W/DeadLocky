using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.ManageMods.Ui;

public sealed partial class ModManagerView
{
    private void BuildLayout()
    {
        var sidebar = new NSView
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var catalog = new NSView
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        NSTextField heading = LauncherStyle.Label("Your profiles", 22, NSFontWeight.Semibold);
        NSTextField storeTitle = LauncherStyle.Label("Mods", 28, NSFontWeight.Semibold);
        _search.PlaceholderString = "Search mods, authors, heroes…";
        _status.UsesSingleLineMode = false;
        _status.MaximumNumberOfLines = 3;
        _status.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        _profileSummary.UsesSingleLineMode = false;
        _profileSummary.MaximumNumberOfLines = 2;
        NSScrollView library = Scroll(_libraryRows);
        NSScrollView results = Scroll(_catalogRows);
        AddSubview(sidebar);
        AddSubview(catalog);
        foreach (NSView view in new NSView[]
        {
            heading,
            _profiles,
            _create,
            _rename,
            _profileSummary,
            library
        }

        )
        {
            sidebar.AddSubview(view);
        }

        foreach (NSView view in new NSView[]
        {
            storeTitle,
            _refresh,
            _search,
            _categories,
            _heroes,
            _sort,
            _nsfw,
            results,
            _previous,
            _next,
            _pageLabel,
            _status
        }

        )
        {
            catalog.AddSubview(view);
        }

        NSLayoutConstraint.ActivateConstraints([sidebar.LeadingAnchor.ConstraintEqualTo(LeadingAnchor), sidebar.TopAnchor.ConstraintEqualTo(TopAnchor), sidebar.BottomAnchor.ConstraintEqualTo(BottomAnchor), sidebar.WidthAnchor.ConstraintEqualTo(264), catalog.LeadingAnchor.ConstraintEqualTo(sidebar.TrailingAnchor, 28), catalog.TrailingAnchor.ConstraintEqualTo(TrailingAnchor), catalog.TopAnchor.ConstraintEqualTo(TopAnchor), catalog.BottomAnchor.ConstraintEqualTo(BottomAnchor), heading.TopAnchor.ConstraintEqualTo(sidebar.TopAnchor, 4), heading.LeadingAnchor.ConstraintEqualTo(sidebar.LeadingAnchor), _profiles.TopAnchor.ConstraintEqualTo(heading.BottomAnchor, 18), _profiles.LeadingAnchor.ConstraintEqualTo(sidebar.LeadingAnchor), _profiles.TrailingAnchor.ConstraintEqualTo(sidebar.TrailingAnchor), _create.TopAnchor.ConstraintEqualTo(_profiles.BottomAnchor, 10), _create.LeadingAnchor.ConstraintEqualTo(sidebar.LeadingAnchor), _rename.TrailingAnchor.ConstraintEqualTo(sidebar.TrailingAnchor), _rename.CenterYAnchor.ConstraintEqualTo(_create.CenterYAnchor), _profileSummary.TopAnchor.ConstraintEqualTo(_create.BottomAnchor, 14), _profileSummary.LeadingAnchor.ConstraintEqualTo(sidebar.LeadingAnchor), _profileSummary.TrailingAnchor.ConstraintEqualTo(sidebar.TrailingAnchor), _profileSummary.HeightAnchor.ConstraintEqualTo(36), library.TopAnchor.ConstraintEqualTo(_profileSummary.BottomAnchor, 14), library.LeadingAnchor.ConstraintEqualTo(sidebar.LeadingAnchor), library.TrailingAnchor.ConstraintEqualTo(sidebar.TrailingAnchor), library.BottomAnchor.ConstraintEqualTo(sidebar.BottomAnchor), storeTitle.TopAnchor.ConstraintEqualTo(catalog.TopAnchor), storeTitle.LeadingAnchor.ConstraintEqualTo(catalog.LeadingAnchor), _refresh.TrailingAnchor.ConstraintEqualTo(catalog.TrailingAnchor), _refresh.CenterYAnchor.ConstraintEqualTo(storeTitle.CenterYAnchor), _search.TopAnchor.ConstraintEqualTo(storeTitle.BottomAnchor, 16), _search.LeadingAnchor.ConstraintEqualTo(catalog.LeadingAnchor), _search.TrailingAnchor.ConstraintEqualTo(catalog.TrailingAnchor), _search.HeightAnchor.ConstraintEqualTo(32), _categories.TopAnchor.ConstraintEqualTo(_search.BottomAnchor, 10), _categories.LeadingAnchor.ConstraintEqualTo(catalog.LeadingAnchor), _heroes.LeadingAnchor.ConstraintEqualTo(_categories.TrailingAnchor, 8), _heroes.WidthAnchor.ConstraintEqualTo(_categories.WidthAnchor), _heroes.CenterYAnchor.ConstraintEqualTo(_categories.CenterYAnchor), _sort.LeadingAnchor.ConstraintEqualTo(_heroes.TrailingAnchor, 8), _sort.TrailingAnchor.ConstraintEqualTo(catalog.TrailingAnchor), _sort.WidthAnchor.ConstraintEqualTo(_categories.WidthAnchor), _sort.CenterYAnchor.ConstraintEqualTo(_categories.CenterYAnchor), _nsfw.TopAnchor.ConstraintEqualTo(_categories.BottomAnchor, 8), _nsfw.LeadingAnchor.ConstraintEqualTo(catalog.LeadingAnchor), results.TopAnchor.ConstraintEqualTo(_nsfw.BottomAnchor, 12), results.LeadingAnchor.ConstraintEqualTo(catalog.LeadingAnchor), results.TrailingAnchor.ConstraintEqualTo(catalog.TrailingAnchor), results.BottomAnchor.ConstraintEqualTo(_previous.TopAnchor, -12), _previous.LeadingAnchor.ConstraintEqualTo(catalog.LeadingAnchor), _previous.BottomAnchor.ConstraintEqualTo(_status.TopAnchor, -10), _next.TrailingAnchor.ConstraintEqualTo(catalog.TrailingAnchor), _next.CenterYAnchor.ConstraintEqualTo(_previous.CenterYAnchor), _pageLabel.CenterXAnchor.ConstraintEqualTo(catalog.CenterXAnchor), _pageLabel.CenterYAnchor.ConstraintEqualTo(_previous.CenterYAnchor), _status.BottomAnchor.ConstraintEqualTo(catalog.BottomAnchor), _status.LeadingAnchor.ConstraintEqualTo(catalog.LeadingAnchor), _status.TrailingAnchor.ConstraintEqualTo(catalog.TrailingAnchor), _status.HeightAnchor.ConstraintEqualTo(48)]);
    }

    private static NSPopUpButton Popup()
    {
        return new NSPopUpButton
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    private static FlippedStack Stack()
    {
        return new FlippedStack
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    private static NSScrollView Scroll(NSStackView document)
    {
        var scroll = new NSScrollView
        {
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false,
            DocumentView = document
        };
        NSLayoutConstraint.ActivateConstraints([document.LeadingAnchor.ConstraintEqualTo(scroll.ContentView.LeadingAnchor), document.TopAnchor.ConstraintEqualTo(scroll.ContentView.TopAnchor), document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor)]);
        return scroll;
    }

    private static NSTextField Wrap(string text, nfloat size)
    {
        NSTextField label = LauncherStyle.Label(text, size, NSFontWeight.Regular, LauncherStyle.SecondaryText);
        label.UsesSingleLineMode = false;
        label.MaximumNumberOfLines = 3;
        label.LineBreakMode = NSLineBreakMode.ByWordWrapping;
        return label;
    }

    private static void AddRow(NSStackView stack, NSView row)
    {
        stack.AddArrangedSubview(row);
        row.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
    }

    private static void Clear(NSStackView stack)
    {
        foreach (NSView view in stack.ArrangedSubviews)
        {
            stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
    }

    private sealed class FlippedStack : NSStackView
    {
        public override bool IsFlipped => true;
    }
}
