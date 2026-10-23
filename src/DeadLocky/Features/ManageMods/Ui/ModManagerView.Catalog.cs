using System.Net;
using System.Text.RegularExpressions;
using DeadLocky.Entities.Mod.Model;
using DeadLocky.Features.ManageMods.Model;
using DeadLocky.Shared.Api.Diagnostics;
using DeadLocky.Shared.Ui;

namespace DeadLocky.Features.ManageMods.Ui;

public sealed partial class ModManagerView
{
    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed)
        {
            return;
        }

        _refreshing = true;
        _refresh.Enabled = false;
        _status.StringValue = "Refreshing deadlockmods.app catalog…";
        try
        {
            _mods = await _catalog.RefreshAsync(_lifetime.Token);
            PopulateFilters();
            _page = 0;
            RenderCatalog();
            _status.TextColor = LauncherStyle.SecondaryText;
            _status.StringValue = $"{_mods.Length:N0} mods from deadlockmods.app · Changes apply on Play";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Event("catalog.ui", "failed", exception);
            _status.TextColor = NSColor.SystemRed;
            _status.StringValue = (_mods.Length > 0 ? "Showing cached catalog. " : "") + exception.Message;
        }
        finally
        {
            _refreshing = false;
            if (!_disposed)
            {
                _refresh.Enabled = true;
            }
        }
    }

    private void PopulateFilters()
    {
        Set(_categories, "All categories", _mods.Select(static mod => mod.Category));
        Set(_heroes, "All heroes", _mods.Select(static mod => mod.Hero ?? ""));
        return;

        static void Set(NSPopUpButton popup, string all, IEnumerable<string> values)
        {
            popup.RemoveAllItems();
            popup.AddItems([
                all,
                .. values.Where(static value => value.Length > 0).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ]);
        }
    }

    private void OnRefresh(object? sender, EventArgs args)
    {
        _ = RefreshAsync();
    }

    private void OnSearch(object? sender, EventArgs args)
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        _searchDelay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = SearchAfterDelayAsync(_searchDelay.Token);
    }

    private async Task SearchAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            _page = 0;
            RenderCatalog();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnFilter(object? sender, EventArgs args)
    {
        _page = 0;
        RenderCatalog();
    }

    private void OnPrevious(object? sender, EventArgs args)
    {
        _page--;
        RenderCatalog();
    }

    private void OnNext(object? sender, EventArgs args)
    {
        _page++;
        RenderCatalog();
    }

    private void RenderCatalog()
    {
        _images.Cancel();
        _images.Dispose();
        _images = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Clear(_catalogRows);
        var query = new ModQuery(_search.StringValue,
            _categories.IndexOfSelectedItem > 0 ? _categories.TitleOfSelectedItem : "",
            _heroes.IndexOfSelectedItem > 0 ? _heroes.TitleOfSelectedItem : "", (int)_sort.IndexOfSelectedItem,
            _nsfw.State == NSCellStateValue.On);
        CatalogMod[] filtered = query.Filter(_mods);
        int pages = Math.Max(1, (filtered.Length + ModQuery.PageSize - 1) / ModQuery.PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);
        _previous.Enabled = _page > 0;
        _next.Enabled = _page + 1 < pages;
        _pageLabel.StringValue = $"Page {_page + 1} of {pages} · {filtered.Length:N0} mods";
        AppLog.Event("catalog.render", "filtered", fields: AppLog.Data(("catalog.total", _mods.Length),
            ("catalog.matches", filtered.Length), ("catalog.page", _page + 1), ("catalog.pages", pages), ("catalog.hasSearch", _search.StringValue.Length > 0)));
        if (filtered.Length == 0)
        {
            AddRow(_catalogRows,
                Wrap(
                    _mods.Length == 0
                        ? "Loading the mod catalog…"
                        : "No mods match these filters. Try another search or category.", 14));
        }

        foreach (CatalogMod mod in filtered.Skip(_page * ModQuery.PageSize).Take(ModQuery.PageSize))
        {
            AddRow(_catalogRows, CatalogRow(mod, _images.Token));
        }
    }

    private NSView CatalogRow(CatalogMod mod, CancellationToken token)
    {
        var row = new NSView
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        NSImageView image = LauncherStyle.Symbol("shippingbox.fill", LauncherStyle.Accent);
        image.ImageScaling = NSImageScale.ProportionallyUpOrDown;
        NSTextField name = LauncherStyle.Label(mod.Name, 15, NSFontWeight.Semibold);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.ToolTip = mod.Name;
        NSTextField meta = LauncherStyle.Label($"{mod.Author} · {mod.Category}", 12, NSFontWeight.Regular,
            LauncherStyle.SecondaryText);
        meta.LineBreakMode = NSLineBreakMode.TruncatingTail;
        NSTextField stats =
            LauncherStyle.Label(
                $"{mod.DownloadCount:N0} downloads · {mod.Likes:N0} likes" + (mod.Hero is null ? "" : " · " + mod.Hero),
                11, NSFontWeight.Regular, LauncherStyle.SecondaryText);
        stats.LineBreakMode = NSLineBreakMode.TruncatingTail;
        ModProfile profile = _library.ActiveProfile;
        bool installed = profile.Mods.Any(entry =>
            _library.Packages.Any(package => package.Id == entry.PackageId && package.RemoteId == mod.RemoteId));
        NSButton install = LauncherStyle.Button(installed ? "Reinstall…" : "Install…", true);
        install.Enabled = !IsBusy && !_gameBusy && !profile.IsVanilla && mod.Downloadable;
        install.ToolTip = profile.IsVanilla ? "Create or select a custom profile to install mods." : null;
        install.Activated += (_, _) => _ = OperateAsync(tokenValue => InstallAsync(mod, profile.Id, tokenValue),
            $"{mod.Name} installed in {profile.Name}.");
        NSButton details = LauncherStyle.Button("Details");
        details.Activated += (_, _) => ShowDetails(mod);
        foreach (NSView view in new NSView[]
                 {
                     image,
                     name,
                     meta,
                     stats,
                     install,
                     details
                 }
                )
        {
            row.AddSubview(view);
        }

        NSLayoutConstraint.ActivateConstraints([
            row.HeightAnchor.ConstraintEqualTo(110), image.LeadingAnchor.ConstraintEqualTo(row.LeadingAnchor),
            image.CenterYAnchor.ConstraintEqualTo(row.CenterYAnchor), image.WidthAnchor.ConstraintEqualTo(84),
            image.HeightAnchor.ConstraintEqualTo(84), name.LeadingAnchor.ConstraintEqualTo(image.TrailingAnchor, 14),
            name.TopAnchor.ConstraintEqualTo(row.TopAnchor, 16),
            name.TrailingAnchor.ConstraintLessThanOrEqualTo(install.LeadingAnchor, -12),
            meta.LeadingAnchor.ConstraintEqualTo(name.LeadingAnchor),
            meta.TopAnchor.ConstraintEqualTo(name.BottomAnchor, 7),
            meta.TrailingAnchor.ConstraintLessThanOrEqualTo(install.LeadingAnchor, -12),
            stats.LeadingAnchor.ConstraintEqualTo(name.LeadingAnchor),
            stats.TopAnchor.ConstraintEqualTo(meta.BottomAnchor, 6),
            stats.TrailingAnchor.ConstraintLessThanOrEqualTo(install.LeadingAnchor, -12),
            install.TrailingAnchor.ConstraintEqualTo(row.TrailingAnchor, -4),
            install.TopAnchor.ConstraintEqualTo(row.TopAnchor, 16), install.WidthAnchor.ConstraintEqualTo(100),
            details.TrailingAnchor.ConstraintEqualTo(install.TrailingAnchor),
            details.TopAnchor.ConstraintEqualTo(install.BottomAnchor, 6),
            details.WidthAnchor.ConstraintEqualTo(install.WidthAnchor)
        ]);
        string? url = mod.ThumbnailUrl ?? mod.Images.FirstOrDefault();
        if (url is not null)
        {
            _ = LoadImageAsync(url, image, token);
        }

        return row;
    }

    private async Task LoadImageAsync(string url, NSImageView image, CancellationToken token)
    {
        bool acquired = false;
        try
        {
            await _imageGate.WaitAsync(token);
            acquired = true;
            if (!_imageCache.TryGetValue(url, out byte[]? bytes))
            {
                bytes = await _catalog.ImageAsync(url, token);
                if (_imageCache.Count >= 64)
                {
                    _imageCache.Clear();
                }

                _imageCache[url] = bytes;
            }

            if (!_disposed && !token.IsCancellationRequested)
            {
                using var data = NSData.FromArray(bytes);
                image.Image = new NSImage(data);
                image.ContentTintColor = null;
            }
            else { AppLog.Event("catalog.thumbnail", "discarded stale result"); }
        }
        catch (Exception exception) when
            (exception is OperationCanceledException or HttpRequestException or IOException)
        {
            AppLog.Event("catalog.ui", "failed", exception);
            // A missing thumbnail does not prevent browsing or installing the mod.
        }
        finally
        {
            if (acquired)
            {
                _ = _imageGate.Release();
            }
        }
    }

    private void ShowDetails(CatalogMod mod)
    {
        string description = WebUtility.HtmlDecode(HtmlTags().Replace(mod.Description ?? "", " "));
        using var alert = new NSAlert();
        alert.MessageText = mod.Name;
        alert.InformativeText =
            $"By {mod.Author} · {mod.Category}\n\n" + description[..Math.Min(description.Length, 3000)];
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Close"));
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Open GameBanana page"));
        if (alert.RunSheetModal(_owner) == 1001 && Uri.TryCreate(mod.RemoteUrl, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            _ = NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(uri.AbsoluteUri));
        }
    }

    private async Task InstallAsync(CatalogMod mod, Guid profileId, CancellationToken token)
    {
        ModRequirements.Validate(_library, profileId, mod);
        ModDownload[] downloads = await _catalog.DownloadsAsync(mod.RemoteId, token);
        if (downloads.Length == 0)
        {
            throw new IOException("The author has not provided a downloadable file.");
        }

        ModDownload download = ChooseDownload(downloads) ??
                               throw new OperationCanceledException("Installation cancelled.", token);
        var progress = new Progress<long>(bytes =>
        {
            if (!_disposed)
            {
                _status.StringValue = $"Downloading {download.Name}: {bytes / 1048576d:F1} MiB" +
                                      (download.Size > 0 ? $" / {download.Size / 1048576d:F1} MiB" : "");
            }
        });
        PreparedMod prepared = await _workspace.PrepareAsync(_catalog, download, progress, token);
        try
        {
            _status.StringValue = "Choose VPK files for this mod…";
            string[] selected = ChooseVpks(prepared.Roots);
            if (selected.Length == 0)
            {
                throw new OperationCanceledException("Installation cancelled.", token);
            }

            await _workspace.InstallAsync(profileId, mod, download, prepared, selected, token);
        }
        finally
        {
            _workspace.Discard(prepared);
        }
    }

    private ModDownload? ChooseDownload(ModDownload[] downloads)
    {
        using var popup = new NSPopUpButton(new CGRect(0, 0, 420, 32), false);
        LauncherStyle.ApplyButtonStyle(popup);
        popup.AddItems([.. downloads.Select(static file => $"{file.Name} ({file.Size / 1048576d:F1} MiB)")]);
        using var alert = new NSAlert();
        alert.MessageText = "Choose a download";
        alert.AccessoryView = popup;
        alert.InformativeText = "Select the author's file or variant you want to install.";
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Download"));
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Cancel"));
        return alert.RunSheetModal(_owner) == 1000 ? downloads[(int)popup.IndexOfSelectedItem] : null;
    }

    private string[] ChooseVpks(string[] roots)
    {
        if (roots.Length == 1)
        {
            return roots;
        }

        using var stack = new FlippedStack();
        stack.Frame = new CGRect(0, 0, 440, roots.Length * 28);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 5;
        List<NSButton> buttons = [];
        foreach (string root in roots)
        {
            var check = new NSButton
            {
                Title = root,
                ToolTip = root
            };
            check.SetButtonType(NSButtonType.Switch);
            buttons.Add(check);
            stack.AddArrangedSubview(check);
        }

        using var scroll = new NSScrollView(new CGRect(0, 0, 440, Math.Min(300, roots.Length * 28)));
        scroll.DocumentView = stack;
        scroll.HasVerticalScroller = true;
        using var alert = new NSAlert();
        alert.MessageText = "Choose VPKs for this variant";
        alert.AccessoryView = scroll;
        alert.InformativeText = "Archives may contain alternative skins. Select the VPKs you want to enable together." +
                                " Referenced multipart data files are included automatically.";
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Install selected"));
        LauncherStyle.ApplyButtonStyle(alert.AddButton("Cancel"));
        return alert.RunSheetModal(_owner) == 1000
            ? [.. roots.Where((_, index) => buttons[index].State == NSCellStateValue.On)]
            : [];
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTags();
}
