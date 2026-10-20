# Application icon

`AppIcon.png` is the source artwork, generated with the built-in imagegen tool.
`AppIcon.icns` contains the macOS icon sizes and is bundled through `BundleResource`.
Rebuild the ICNS after changing the PNG with `bash scripts/create-app-icon.sh`.
The conversion only resizes and packages the artwork, preserving its alpha channel.

Generation prompt:

> Create a finished macOS application icon for DeadLocky, an independent Deadlock game launcher and mod manager. Use case: logo-design, production icon. One square 1024x1024 asset, transparent outside the icon. A large clean macOS rounded-square charcoal glass tile, filling about 90 percent of the canvas, with a beautifully sculpted central ivory-and-champagne-gold padlock whose keyhole subtly resembles a friendly ghost face. Art Deco inspired, restrained supernatural-game atmosphere. Bold memorable silhouette legible at 32px, minimal large forms, polished physical glass and porcelain materials, warm gold rim lighting, subtle depth, crisp edges. Match a dark graphite and warm cream desktop UI. Frontal straight-on view, perfectly centered, no perspective tilt. No lettering, no words, no numbers, no extra icons, no scene or mockup, no watermark. The outer corners must be transparent, not a checkerboard pattern; preserve real alpha. This is the actual icon artwork, not a screenshot of an icon.
