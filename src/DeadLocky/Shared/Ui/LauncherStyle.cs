namespace DeadLocky.Shared.Ui;

internal static class LauncherStyle
{
    public static NSColor Background { get; } = NSColor.FromRgb(0.075f, 0.082f, 0.09f);

    private static NSColor Text { get; } = NSColor.FromRgb(0.94f, 0.93f, 0.9f);

    public static NSColor SecondaryText { get; } = NSColor.FromRgb(0.6f, 0.62f, 0.64f);

    public static NSColor Accent { get; } = NSColor.FromRgb(0.87f, 0.76f, 0.58f);

    public static NSButton Button(string title, bool primary = false)
    {
        var button = new NSButton
        {
            Title = title,
            BezelStyle = NSBezelStyle.Rounded,
            Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!,
            ControlSize = NSControlSize.Large,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ApplyButtonStyle(button, primary);
        return button;
    }

    public static void ApplyButtonStyle(NSButton button, bool primary = false)
    {
        button.BezelColor = primary ? Accent : null;
        button.BezelStyle = NSBezelStyle.Rounded;
        if (!OperatingSystem.IsMacOSVersionAtLeast(26))
        {
            return;
        }

        button.BezelStyle = NSBezelStyle.Glass;
        button.TintProminence = primary ? NSTintProminence.Primary : NSTintProminence.Secondary;
    }

    public static NSButton IconButton(string symbol, string label)
    {
        NSButton button = Button(label);
        button.Image = NSImage.GetSystemSymbol(symbol, null);
        button.ImagePosition = NSCellImagePosition.ImageOnly;
        button.ToolTip = label;
        NSLayoutConstraint.ActivateConstraints(
        [
            button.WidthAnchor.ConstraintEqualTo(36),
            button.HeightAnchor.ConstraintEqualTo(32)
        ]);
        return button;
    }

    public static NSTextField Label(string text, nfloat size, nfloat weight, NSColor? color = null)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(size, weight)!;
        label.TextColor = color ?? Text;
        label.TranslatesAutoresizingMaskIntoConstraints = false;
        return label;
    }

    public static NSImageView Symbol(string name, NSColor color)
    {
        return new NSImageView
        {
            Image = NSImage.GetSystemSymbol(name, null),
            ContentTintColor = color,
            ImageScaling = NSImageScale.ProportionallyUpOrDown,
            TranslatesAutoresizingMaskIntoConstraints = false,
            AccessibilityElement = false
        };
    }

    public static NSView Surface(NSView content)
    {
        NSView surface;
        if (OperatingSystem.IsMacOSVersionAtLeast(26))
        {
            surface = new NSGlassEffectView
            {
                ContentView = content,
                CornerRadius = 22,
                Style = NSGlassEffectViewStyle.Regular
            };
        }
        else
        {
            var effect = new NSVisualEffectView
            {
                Material = NSVisualEffectMaterial.HudWindow,
                BlendingMode = NSVisualEffectBlendingMode.WithinWindow,
                State = NSVisualEffectState.Active,
                WantsLayer = true
            };
            effect.Layer!.CornerRadius = 22;
            effect.Layer.MasksToBounds = true;
            effect.Layer.BorderWidth = 1;
            effect.Layer.BorderColor = NSColor.FromRgba(1f, 1f, 1f, 0.08f).CGColor;
            effect.AddSubview(content);
            NSLayoutConstraint.ActivateConstraints(
            [
                content.LeadingAnchor.ConstraintEqualTo(effect.LeadingAnchor),
                content.TrailingAnchor.ConstraintEqualTo(effect.TrailingAnchor),
                content.TopAnchor.ConstraintEqualTo(effect.TopAnchor),
                content.BottomAnchor.ConstraintEqualTo(effect.BottomAnchor)
            ]);
            surface = effect;
        }

        surface.TranslatesAutoresizingMaskIntoConstraints = false;
        return surface;
    }
}
