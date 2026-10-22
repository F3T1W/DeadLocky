using DeadLocky.Shared.Ui;
using ObjCRuntime;

namespace DeadLocky.App.Ui;

internal static class ApplicationMenu
{
    private static NSMenu? _applicationMenu;

    public static void ConfigureReports(Action show)
    {
        _applicationMenu?.InsertItem(new NSMenuItem("Report a problem…", "", (_, _) => show()), 0);
    }

    public static void ConfigureEnvironmentSetup(Func<Task> setup)
    {
        _applicationMenu?.InsertItem(new NSMenuItem("Game environment…", "",
            (_, _) => _ = UiAction.RunAsync(NSApplication.SharedApplication.MainWindow, setup)), 0);
    }

#if DEBUG
    public static void ConfigureSetupTest(Func<Task> test)
    {
        _applicationMenu?.InsertItem(new NSMenuItem("Test environment setup…", "",
            (_, _) => _ = UiAction.RunAsync(NSApplication.SharedApplication.MainWindow, test)), 1);
    }
#endif

    public static void Install(Func<Task> quit)
    {
        var menu = new NSMenu();
        var applicationMenu = new NSMenu("DeadLocky");
        _applicationMenu = applicationMenu;
        applicationMenu.AddItem(new NSMenuItem("Quit DeadLocky", "q",
            (_, _) => _ = UiAction.RunAsync(NSApplication.SharedApplication.MainWindow, quit)));
        menu.AddItem(new NSMenuItem { Submenu = applicationMenu });

        var editMenu = new NSMenu("Edit");
        editMenu.AddItem(new NSMenuItem("Cut", new Selector("cut:"), "x"));
        editMenu.AddItem(new NSMenuItem("Paste", new Selector("paste:"), "v"));
        editMenu.AddItem(new NSMenuItem("Copy", new Selector("copy:"), "c"));
        editMenu.AddItem(new NSMenuItem("Select All", new Selector("selectAll:"), "a"));
        menu.AddItem(new NSMenuItem { Title = "Edit", Submenu = editMenu });
        NSApplication.SharedApplication.MainMenu = menu;
    }
}
