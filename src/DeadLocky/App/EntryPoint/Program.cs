using DeadLocky.App.EntryPoint;

AppDelegate.InitializeDiagnostics();
NSApplication.Init();
using var appDelegate = new AppDelegate();
NSApplication.SharedApplication.Delegate = appDelegate;
NSApplication.Main(args);
