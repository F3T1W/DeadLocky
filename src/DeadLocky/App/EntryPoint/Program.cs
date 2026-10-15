using DeadLocky.App.EntryPoint;

NSApplication.Init();
using var appDelegate = new AppDelegate();
NSApplication.SharedApplication.Delegate = appDelegate;
NSApplication.Main(args);
