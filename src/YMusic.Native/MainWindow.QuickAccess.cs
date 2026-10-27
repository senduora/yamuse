using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace YMusic.Native;

public partial class MainWindow
{
    private readonly Dictionary<string, CatalogItem[]> _quickCollections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<TrackRow>> _quickPlaylistTracks = new(StringComparer.Ordinal);
    private SmartPlaylistStore? _smartPlaylistStore;
    private IReadOnlyList<SmartPlaylistRule>? _smartPlaylistRules;
    private string? _quickAccountUid;
    private Window? _quickAccessDialog, _smartPlaylistDialog, _smartRuleDialog;
    private Action? _quickRefresh, _smartRefresh;
    private bool _quickAccessInitialized;
    private readonly DispatcherTimer _quickRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };

    private void InitializeQuickAccess()
    {
        if (_quickAccessInitialized) return;
        _quickAccessInitialized = true;
        _quickRefreshTimer.Tick += (_, _) => { _quickRefreshTimer.Stop(); FlushQuickAccessCollection(); };
        _quickAccountUid = _account?.Uid;
        _smartPlaylistStore = new SmartPlaylistStore(_diagnosticsDirectory);
        if (NavigationScroll.Content is not StackPanel navigation) return;
        var quick = QuickNavigationButton("Быстрый поиск", "\uE721", "Быстрый поиск по загруженной коллекции · Ctrl+K");
        quick.Name = "QuickAccessNav";
        quick.Click += (_, _) => OpenQuickAccess();
        navigation.Children.Insert(navigation.Children.IndexOf(SearchNav) + 1, quick);
        var smart = QuickNavigationButton("Умные подборки", "\uE945", "Локальные подборки по вашим правилам");
        smart.Name = "SmartPlaylistsNav";
        smart.Click += (_, _) => OpenSmartPlaylists();
        navigation.Children.Insert(navigation.Children.IndexOf(PlaylistsNav) + 1, smart);
    }

    private Button QuickNavigationButton(string title, string glyph, string hint)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("NavIcon") });
        content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Style = (Style)FindResource("NavButton"), ToolTip = hint };
        AutomationProperties.SetName(button, title);
        return button;
    }

    // Called before the existing modifier guard in Window_KeyDown.
    private bool HandleQuickAccessKey(KeyEventArgs args)
    {
        if (_closed || args.Handled || args.Key != Key.K || args.KeyboardDevice.Modifiers != ModifierKeys.Control
            || args.KeyboardDevice.IsKeyDown(Key.LWin) || args.KeyboardDevice.IsKeyDown(Key.RWin)) return false;
        args.Handled = true;
        OpenQuickAccess();
        return true;
    }

    // Activation/logout hooks close any old-account dialog, including an editor
    // with an unsaved draft. No old result can be applied to the next account.
    private void ResetQuickAccessForAccount()
    {
        _quickRefresh = _smartRefresh = null;
        _quickRefreshTimer.Stop();
        _smartRuleDialog?.Close(); _smartPlaylistDialog?.Close(); _quickAccessDialog?.Close();
        _smartRuleDialog = _smartPlaylistDialog = _quickAccessDialog = null;
        _quickCollections.Clear(); _quickPlaylistTracks.Clear();
        _smartPlaylistRules = null;
        _quickAccountUid = _account?.Uid;
    }

    private void DisposeQuickAccess() => ResetQuickAccessForAccount();

    private void EnsureQuickAccessAccount()
    {
        if (_quickAccountUid != _account?.Uid) ResetQuickAccessForAccount();
    }

    private void QuickAccessCollectionLoaded(string kind, IReadOnlyList<CatalogItem> items)
    {
        EnsureQuickAccessAccount();
        if (_account is null || kind is not ("playlists" or "albums" or "artists")) return;
        _quickCollections[kind] = items.ToArray();
        if (kind == "playlists")
        {
            var keys = items.Select(QuickAccessIndex.CatalogKey).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _quickPlaylistTracks.Keys.Where(key => !keys.Contains(key)).ToArray()) _quickPlaylistTracks.Remove(key);
        }
        RefreshQuickAccessCollection();
    }

    private void QuickAccessPlaylistLoaded(PlaylistDetails playlist)
    {
        EnsureQuickAccessAccount();
        if (_account is null) return;
        var item = new CatalogItem(playlist.Kind, "playlist", playlist.Title, playlist.OwnerName ?? "",
            playlist.Owner, playlist.Kind, OwnerName: playlist.OwnerName);
        // A search/discovery playlist joins the collection only after the service
        // collection lists it; opening one does not silently mark it as owned.
        if (_quickCollections.Values.SelectMany(items => items).Any(candidate => QuickAccessIndex.CatalogKey(candidate) == QuickAccessIndex.CatalogKey(item)))
            _quickPlaylistTracks[QuickAccessIndex.CatalogKey(item)] = playlist.Tracks.ToArray();
        RefreshQuickAccessCollection();
    }

    // Invoke after favorites/like/download state changes. The visible local rule
    // list recomputes automatically; no saved list of track IDs can become stale.
    private void RefreshQuickAccessCollection()
    {
        if (_closed || _quickRefresh is null && _smartRefresh is null && _section != "smart-playlist-detail") return;
        // Coalesce download/like bursts into at most one recomposition per tick.
        // No timer runs while none of these local views are being inspected.
        if (!_quickRefreshTimer.IsEnabled) _quickRefreshTimer.Start();
    }

    private void FlushQuickAccessCollection()
    {
        if (_closed) return;
        try
        {
            _quickRefresh?.Invoke();
            _smartRefresh?.Invoke();
            RefreshSmartPlaylistView();
        }
        catch (Exception error) { ShowError(error); }
    }

    private TrackRow[] QuickAccessCollectionTracks()
    {
        // OfflineLibrary validates file existence, length and path safety. Use its
        // fresh result to update local marks without making a network request.
        var offline = _offline.Tracks();
        var local = offline.Where(row => row.LocalPath is not null).DistinctBy(row => row.Id, StringComparer.Ordinal)
            .ToDictionary(row => row.Id, StringComparer.Ordinal);
        var rows = _favoritesCache.Concat(_quickPlaylistTracks.Values.SelectMany(tracks => tracks)).Concat(offline)
            .DistinctBy(row => row.RequestId, StringComparer.Ordinal).ToArray();
        foreach (var row in rows)
        {
            var downloaded = local.GetValueOrDefault(row.Id);
            row.DownloadedPath = downloaded?.LocalPath;
            row.DownloadedQuality = downloaded?.LocalQuality;
        }
        ApplyTrackLikeStates(rows);
        return rows;
    }

    private IReadOnlySet<string> QuickAccessFavoriteIds() => _favoriteIdsKnown ? _favoriteTrackIds
        : _favoritesCache.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);

    private IReadOnlyList<SmartPlaylistRule> GetSmartPlaylistRules()
    {
        EnsureQuickAccessAccount();
        if (_account is null || _smartPlaylistStore is null) return [];
        return _smartPlaylistRules ??= _smartPlaylistStore.Load(_account.Uid);
    }

    private IReadOnlyList<TrackRow> EvaluateSmartPlaylist(SmartPlaylistRule rule)
        => SmartPlaylistEvaluator.Evaluate(rule, QuickAccessCollectionTracks(), QuickAccessFavoriteIds());

    private IReadOnlyList<QuickAccessEntry> BuildQuickAccessEntries()
    {
        var rows = QuickAccessCollectionTracks();
        var entries = QuickAccessIndex.Build(rows, _quickCollections.Values.SelectMany(items => items), _quickPlaylistTracks).ToList();
        var favorites = QuickAccessFavoriteIds();
        IReadOnlyList<SmartPlaylistRule> rules = [];
        try { rules = GetSmartPlaylistRules(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _diagnostics.LastErrorType = "SmartRules:" + error.GetType().Name; }
        foreach (var rule in rules)
            entries.Add(new("smart:" + rule.Id, "smart", rule.Name, rule.Description,
                SmartPlaylistEvaluator.Evaluate(rule, rows, favorites), SmartRuleId: rule.Id));
        return entries;
    }

    private void OpenQuickAccess()
    {
        if (_closed) return;
        EnsureQuickAccessAccount();
        if (_quickAccessDialog is { } existing) { if (existing.IsVisible) existing.Activate(); return; }
        try
        {
            var dialog = QuickDialog("Быстрый поиск", 760, 600);
            _quickAccessDialog = dialog;
            var generation = _accountGeneration; var account = _account?.Uid;
            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new StackPanel();
            head.Children.Add(QuickText("Ваша музыка — под рукой", 23, true));
            head.Children.Add(QuickText("Поиск по уже загруженной коллекции и скачанному", 12, muted: true));
            var query = new TextBox { Name = "QuickAccessQuery", Margin = new Thickness(0, 17, 0, 10), FontSize = 16,
                MaxLength = 300, ToolTip = "Название, исполнитель или альбом" };
            AutomationProperties.SetName(query, "Быстрый поиск в коллекции");
            head.Children.Add(query); root.Children.Add(head);
            var categories = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; Grid.SetRow(categories, 1); root.Children.Add(categories);
            var list = QuickList("QuickAccessResults"); list.ItemTemplate = QuickEntryTemplate();
            Grid.SetRow(list, 2); root.Children.Add(list);
            var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 3); root.Children.Add(footer);
            var note = QuickText("↑ ↓ — выбор · Enter — слушать · Shift+Enter — следующим · Ctrl+Enter — в очередь · Esc — закрыть", 11, muted: true);
            footer.Children.Add(note);
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; footer.Children.Add(buttons);
            var play = QuickButton("Слушать", "AccentButton"); var next = QuickButton("Следующим");
            var enqueue = QuickButton("В очередь"); var open = QuickButton("Открыть");
            foreach (var button in new[] { play, next, enqueue, open }) buttons.Children.Add(button);
            var kind = "all"; var index = BuildQuickAccessEntries();
            void Update()
            {
                if (!QuickAccessAccountCurrent(account, generation)) return;
                var key = (list.SelectedItem as QuickAccessEntry)?.Key;
                var results = QuickAccessIndex.Search(index, query.Text, kind);
                list.ItemsSource = results;
                list.SelectedItem = results.FirstOrDefault(entry => entry.Key == key) ?? results.FirstOrDefault();
                note.Text = results.Count == 0 ? "Ничего не найдено. Сначала загрузите нужный раздел коллекции."
                    : "↑ ↓ — выбор · Enter — слушать · Shift+Enter — следующим · Ctrl+Enter — в очередь · Esc — закрыть";
                UpdateActions();
            }
            void UpdateActions()
            {
                var selected = list.SelectedItem as QuickAccessEntry;
                var canResolve = selected is not null && (selected.Tracks.Any(row => row.CanPlay) || selected.Catalog is not null && _session is not null);
                play.IsEnabled = next.IsEnabled = enqueue.IsEnabled = canResolve;
                open.IsEnabled = selected is not null;
            }
            async Task Execute(string action)
            {
                if (list.SelectedItem is not QuickAccessEntry selected || !QuickAccessAccountCurrent(account, generation)) return;
                dialog.Close();
                await ExecuteQuickAccessEntryAsync(selected, action, account, generation);
            }
            foreach (var (id, label) in new[] { ("all", "Всё"), ("track", "Треки"), ("album", "Альбомы"), ("artist", "Исполнители"), ("playlist", "Плейлисты") })
            {
                var button = QuickButton(label, "QuietButton"); button.Padding = new Thickness(11, 6, 11, 6);
                button.Tag = id; categories.Children.Add(button);
                button.Click += (_, _) =>
                {
                    kind = id;
                    foreach (Button candidate in categories.Children) candidate.SetResourceReference(Control.ForegroundProperty,
                        Equals(candidate.Tag, kind) ? "AccentTextBrush" : "MutedBrush");
                    Update(); query.Focus();
                };
                button.SetResourceReference(Control.ForegroundProperty, id == kind ? "AccentTextBrush" : "MutedBrush");
            }
            play.Click += async (_, _) => await Execute("play"); next.Click += async (_, _) => await Execute("next");
            enqueue.Click += async (_, _) => await Execute("enqueue"); open.Click += async (_, _) => await Execute("open");
            list.MouseDoubleClick += async (_, args) =>
            {
                if (ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) is ListBoxItem) await Execute("play");
            };
            list.SelectionChanged += (_, _) => UpdateActions();
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            debounce.Tick += (_, _) => { debounce.Stop(); Update(); };
            query.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
            dialog.PreviewKeyDown += async (_, args) =>
            {
                if (args.Handled) return;
                var searchInput = query.IsKeyboardFocusWithin || list.IsKeyboardFocusWithin
                    || ReferenceEquals(args.OriginalSource, query) || ReferenceEquals(args.OriginalSource, list);
                if (args.Key is Key.Down or Key.Up && searchInput)
                {
                    args.Handled = true;
                    if (list.Items.Count > 0)
                    {
                        list.SelectedIndex = Math.Clamp(list.SelectedIndex + (args.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1);
                        list.ScrollIntoView(list.SelectedItem);
                    }
                }
                else if (args.Key == Key.Enter && searchInput
                    && args.KeyboardDevice.Modifiers is ModifierKeys.None or ModifierKeys.Control or ModifierKeys.Shift)
                {
                    args.Handled = true; debounce.Stop(); Update();
                    await Execute(args.KeyboardDevice.Modifiers == ModifierKeys.Control ? "enqueue" : args.KeyboardDevice.Modifiers == ModifierKeys.Shift ? "next" : "play");
                }
            };
            _quickRefresh = () => { index = BuildQuickAccessEntries(); Update(); };
            dialog.Closed += (_, _) =>
            {
                debounce.Stop();
                if (ReferenceEquals(_quickAccessDialog, dialog)) { _quickAccessDialog = null; _quickRefresh = null; }
            };
            dialog.Content = root; Update();
            dialog.Loaded += (_, _) => query.Focus();
            ShowQuickDialog(dialog);
        }
        catch (Exception error) { _quickAccessDialog?.Close(); ShowError(error); }
    }

    private bool QuickAccessAccountCurrent(string? uid, int generation)
        => !_closed && uid == _account?.Uid && generation == _accountGeneration;

    private async Task ExecuteQuickAccessEntryAsync(QuickAccessEntry entry, string action, string? uid, int generation)
    {
        if (!QuickAccessAccountCurrent(uid, generation)) return;
        try
        {
            var rows = entry.SmartRuleId is { } id
                ? GetSmartPlaylistRules().FirstOrDefault(rule => rule.Id == id) is { } rule ? EvaluateSmartPlaylist(rule) : []
                : entry.Tracks;
            if (action == "open" && entry.Catalog is { } catalog)
            {
                if (catalog.Kind == "offline-playlist") ShowOfflinePlaylist(catalog);
                else if (_session is not null) await OpenCatalogAsync(catalog);
                else ShowQuickAccessRows(entry.Title, rows, entry.SmartRuleId);
                return;
            }
            if (rows.Count == 0 && entry.Catalog is { } unloaded && _session is not null)
            {
                await OpenCatalogAsync(unloaded);
                if (!QuickAccessAccountCurrent(uid, generation)) return;
                var resolved = unloaded.Kind switch
                {
                    "playlist" => _activePlaylist is { } playlist && playlist.Owner == unloaded.Owner && playlist.Kind == (unloaded.PlaylistKind ?? unloaded.Id),
                    "album" => _activeAlbumDetails?.Id == unloaded.Id,
                    "artist" => _artistDetails?.Id == unloaded.Id,
                    _ => false
                };
                if (!resolved) return;
                rows = unloaded.Kind == "artist" ? _artistDetails!.TopTracks : _tracks.ToArray();
            }
            if (!QuickAccessAccountCurrent(uid, generation)) return;
            if (action == "open") { ShowQuickAccessRows(entry.Title, rows, entry.SmartRuleId); return; }
            var playable = rows.Where(row => row.CanPlay).ToArray();
            if (playable.Length == 0) { SetStatus("В этой подборке пока нет доступных треков."); return; }
            if (action == "play")
            {
                ShowQuickAccessRows(entry.Title, rows, entry.SmartRuleId);
                TracksList.SelectedItem = playable[0];
                await PlaySelectedAsync();
            }
            else
            {
                if (action == "next" && _playbackQueue.Current is null)
                {
                    // Without Current, the first Enqueue creates it. Prepend the
                    // whole ordered batch once and keep existing unavailable rows.
                    // SetItems selects a cursor; it never starts the native player.
                    _playbackQueue.SetItems(playable.Concat(_queue).ToArray());
                }
                else
                {
                    // Inserting repeatedly after Current reverses order, so reverse
                    // the loop only while a current occurrence already exists.
                    foreach (var row in action == "next" ? playable.Reverse() : playable) _playbackQueue.Enqueue(row, action == "next");
                }
                RefreshPlaybackView(); UpdateControls();
                SetStatus(action == "next" ? $"Следующими: {playable.Length:N0} треков" : $"Добавлено в очередь: {playable.Length:N0} треков");
            }
        }
        catch (Exception error) { if (QuickAccessAccountCurrent(uid, generation)) ShowError(error); }
    }

    private void ShowQuickAccessRows(string title, IReadOnlyList<TrackRow> rows, Guid? smartRuleId)
    {
        SetSection(smartRuleId is null ? "quick-access-detail" : "smart-playlist-detail", title);
        // BrowseSnapshot already captures Opened. Carry only the local rule ID
        // there, so Back/Forward recomputes the correct rule after any edits.
        _openedCatalogItem = smartRuleId is { } id ? new CatalogItem(id.ToString("D"), "smart", title, "Локальная подборка")
            : new CatalogItem(Guid.NewGuid().ToString("D"), "quick", title, "Локальная коллекция");
        ShowRows(rows);
        FilterBox.IsEnabled = true;
        SetStatus(smartRuleId is null ? "Треки из загруженной коллекции" : "Локальная подборка · состав обновляется по текущей коллекции");
    }

    // Returns true when the current section was refreshed locally.
    private bool RefreshSmartPlaylistView()
    {
        if (_section == "quick-access-detail")
        {
            ApplyOfflineStates(_tracks); UpdateCount();
            return true;
        }
        if (_section != "smart-playlist-detail") return false;
        try
        {
            if (_openedCatalogItem is not { Kind: "smart" } opened || !Guid.TryParse(opened.Id, out var id)) return true;
            var rule = GetSmartPlaylistRules().FirstOrDefault(rule => rule.Id == id);
            var selected = (TracksList.SelectedItem as TrackRow)?.RequestId;
            ShowRows(rule is null ? [] : EvaluateSmartPlaylist(rule));
            if (selected is not null) TracksList.SelectedItem = _tracks.FirstOrDefault(row => row.RequestId == selected);
            CollectionTitle.Text = rule?.Name ?? "Подборка удалена";
            FilterBox.IsEnabled = true;
        }
        catch (Exception error) { ShowError(error); }
        return true;
    }

    private void OpenSmartPlaylists()
    {
        if (_closed) return;
        EnsureQuickAccessAccount();
        if (_account is null) { SetStatus("Войдите в Яндекс, чтобы создать локальные подборки для этого аккаунта."); return; }
        if (_smartPlaylistDialog is { } existing) { if (existing.IsVisible) existing.Activate(); return; }
        try
        {
            var uid = _account.Uid; var generation = _accountGeneration;
            var dialog = QuickDialog("Умные подборки", 780, 620); _smartPlaylistDialog = dialog;
            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new StackPanel();
            head.Children.Add(QuickText("Умные подборки", 24, true));
            head.Children.Add(QuickText("Правила хранятся на этом компьютере для текущего аккаунта.\nСостав вычисляется по загруженной коллекции и скачанным трекам.", 12, muted: true));
            var toolbar = new WrapPanel { Margin = new Thickness(0, 14, 0, 12) }; head.Children.Add(toolbar);
            var create = QuickButton("＋ Создать", "AccentButton"); var edit = QuickButton("Изменить / переименовать"); var delete = QuickButton("Удалить");
            toolbar.Children.Add(create); toolbar.Children.Add(edit); toolbar.Children.Add(delete); root.Children.Add(head);
            var list = QuickList("SmartPlaylistsList"); list.ItemTemplate = SmartRuleTemplate(); Grid.SetRow(list, 1); root.Children.Add(list);
            var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(bottom, 2); root.Children.Add(bottom);
            var preview = QuickText("Создайте правило «Скачанные любимые», выберите исполнителей или длительность трека.", 12, muted: true); bottom.Children.Add(preview);
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; bottom.Children.Add(actions);
            var open = QuickButton("Открыть"); var play = QuickButton("Слушать", "AccentButton"); var next = QuickButton("Следующими"); var enqueue = QuickButton("В очередь");
            foreach (var button in new[] { open, play, next, enqueue }) actions.Children.Add(button);
            void Preview()
            {
                var rule = list.SelectedItem as SmartPlaylistRule;
                edit.IsEnabled = delete.IsEnabled = open.IsEnabled = rule is not null;
                var rows = rule is null ? [] : EvaluateSmartPlaylist(rule);
                play.IsEnabled = next.IsEnabled = enqueue.IsEnabled = rows.Any(row => row.CanPlay);
                preview.Text = rule is null ? "Создайте правило «Скачанные любимые», выберите исполнителей или длительность трека."
                    : $"{rows.Count:N0} треков сейчас · {rule.Description}";
            }
            void Reload(Guid? selected = null)
            {
                if (!QuickAccessAccountCurrent(uid, generation)) return;
                var id = selected ?? (list.SelectedItem as SmartPlaylistRule)?.Id;
                var rules = GetSmartPlaylistRules(); list.ItemsSource = rules;
                list.SelectedItem = rules.FirstOrDefault(rule => rule.Id == id) ?? rules.FirstOrDefault();
                Preview();
            }
            async Task Execute(string action)
            {
                if (list.SelectedItem is not SmartPlaylistRule selected || !QuickAccessAccountCurrent(uid, generation)) return;
                var entry = new QuickAccessEntry("smart:" + selected.Id, "smart", selected.Name, selected.Description, [], SmartRuleId: selected.Id);
                dialog.Close(); await ExecuteQuickAccessEntryAsync(entry, action, uid, generation);
            }
            create.Click += (_, _) => EditSmartPlaylist(null, dialog, Reload);
            edit.Click += (_, _) => { if (list.SelectedItem is SmartPlaylistRule rule) EditSmartPlaylist(rule, dialog, Reload); };
            delete.Click += (_, _) =>
            {
                if (list.SelectedItem is not SmartPlaylistRule selected || !QuickAccessAccountCurrent(uid, generation)) return;
                var confirmation = QuickDialog("Удаление локальной подборки", 540, 245, dialog);
                var panel = new StackPanel { Margin = new Thickness(24) };
                panel.Children.Add(QuickText("Удалить «" + selected.Name + "»?", 19, true));
                panel.Children.Add(QuickText("Будет удалено только локальное правило. Музыкальные файлы сохранятся.", 12, muted: true));
                var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
                var remove = QuickButton("Удалить правило", "AccentButton"); var cancel = QuickButton("Отмена");
                buttons.Children.Add(remove); buttons.Children.Add(cancel); panel.Children.Add(buttons); confirmation.Content = panel;
                cancel.Click += (_, _) => confirmation.Close();
                remove.Click += (_, _) =>
                {
                    if (!QuickAccessAccountCurrent(uid, generation)) { confirmation.Close(); return; }
                    try
                    {
                        _smartPlaylistRules = _smartPlaylistStore!.Remove(uid, selected.Id);
                        confirmation.Close(); Reload(); RefreshQuickAccessCollection();
                    }
                    catch (Exception error) { ShowError(error); }
                };
                ShowQuickDialog(confirmation);
            };
            open.Click += async (_, _) => await Execute("open"); play.Click += async (_, _) => await Execute("play");
            next.Click += async (_, _) => await Execute("next"); enqueue.Click += async (_, _) => await Execute("enqueue");
            list.MouseDoubleClick += async (_, args) =>
            { if (ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) is ListBoxItem) await Execute("open"); };
            list.SelectionChanged += (_, _) => Preview();
            _smartRefresh = Preview;
            dialog.Closed += (_, _) => { if (ReferenceEquals(_smartPlaylistDialog, dialog)) { _smartPlaylistDialog = null; _smartRefresh = null; } };
            dialog.Content = root; Reload(); ShowQuickDialog(dialog);
        }
        catch (Exception error) { _smartPlaylistDialog?.Close(); ShowError(error); }
    }

    private void EditSmartPlaylist(SmartPlaylistRule? previous, Window owner, Action<Guid?> saved)
    {
        if (_account is null) return;
        var uid = _account.Uid; var generation = _accountGeneration;
        var dialog = QuickDialog(previous is null ? "Новая локальная подборка" : "Правила подборки", 640, 680, owner);
        _smartRuleDialog = dialog;
        var root = new DockPanel { Margin = new Thickness(24) };
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var errorText = QuickText("", 12, muted: true); footer.Children.Add(errorText);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; footer.Children.Add(buttons);
        var save = QuickButton("Сохранить правила", "AccentButton"); var cancel = QuickButton("Отмена"); buttons.Children.Add(save); buttons.Children.Add(cancel);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll); var form = new StackPanel(); scroll.Content = form;
        form.Children.Add(QuickText("Правила подборки", 24, true));
        form.Children.Add(QuickText("Все включённые условия применяются вместе.", 12, muted: true));
        form.Children.Add(QuickText("Название", 13, true));
        var name = new TextBox { Name = "SmartRuleName", Text = previous?.Name ?? "Скачанные любимые", MaxLength = 100, Margin = new Thickness(0, 7, 0, 14) };
        AutomationProperties.SetName(name, "Название локальной подборки"); form.Children.Add(name);
        var favorites = new CheckBox { Name = "SmartRuleFavorites", Content = "Только любимые треки", IsChecked = previous?.FavoritesOnly ?? true, Margin = new Thickness(0, 2, 0, 10) };
        var downloaded = new CheckBox { Name = "SmartRuleDownloaded", Content = "Только скачанные треки", IsChecked = previous?.DownloadedOnly ?? true, Margin = new Thickness(0, 2, 0, 14) };
        form.Children.Add(favorites); form.Children.Add(downloaded);
        form.Children.Add(QuickText("Максимальная длительность каждого трека, минут", 13, true));
        var minutes = new TextBox { Name = "SmartRuleMinutes", Text = previous?.MaximumMinutes?.ToString(CultureInfo.InvariantCulture) ?? "", MaxLength = 4, Margin = new Thickness(0, 7, 0, 6), Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(minutes, "Максимальная длительность каждого трека в минутах"); form.Children.Add(minutes);
        form.Children.Add(QuickText("Пусто — без ограничения. При ограничении треки с неизвестной длительностью исключаются.", 11, muted: true));
        form.Children.Add(QuickText("Исполнители из загруженной коллекции", 13, true));
        form.Children.Add(QuickText("Ничего не выбрано — все исполнители. Несколько выбранных исполнителей объединяются.", 11, muted: true));
        var artists = QuickAccessCollectionTracks().SelectMany(row => row.Artists)
            .Concat(_quickCollections.GetValueOrDefault("artists")?.Select(item => new ArtistReference(item.Id, item.Title)) ?? [])
            .Where(artist => !string.IsNullOrWhiteSpace(artist.Id)).DistinctBy(artist => artist.Id, StringComparer.Ordinal)
            .OrderBy(artist => artist.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Retain IDs that are temporarily absent from a partially loaded collection.
        foreach (var id in previous?.ArtistIds ?? [])
            if (!artists.Any(artist => artist.Id == id)) artists.Add(new ArtistReference(id, "Исполнитель " + id + " · ещё не загружен"));
        var artistList = new StackPanel { Margin = new Thickness(0, 8, 0, 10) }; form.Children.Add(artistList);
        var checks = new List<(ArtistReference Artist, CheckBox Check)>();
        foreach (var artist in artists)
        {
            var check = new CheckBox { Content = artist.Name, IsChecked = previous?.ArtistIds?.Contains(artist.Id, StringComparer.Ordinal) == true,
                Margin = new Thickness(0, 3, 0, 5), ToolTip = artist.Name };
            artistList.Children.Add(check); checks.Add((artist, check));
        }
        if (artists.Count == 0) artistList.Children.Add(QuickText("Загрузите «Мне нравится» или раздел «Исполнители», чтобы выбрать их здесь.", 12, muted: true));
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            if (!QuickAccessAccountCurrent(uid, generation)) { dialog.Close(); return; }
            try
            {
                int? maximum = null;
                if (minutes.Text.Trim().Length > 0)
                {
                    if (!int.TryParse(minutes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                        throw new ArgumentException("Введите целое число минут от 1 до 1440 или оставьте поле пустым.");
                    maximum = parsed;
                }
                var rule = new SmartPlaylistRule(previous?.Id ?? Guid.NewGuid(), name.Text, favorites.IsChecked == true,
                    downloaded.IsChecked == true, checks.Where(pair => pair.Check.IsChecked == true).Select(pair => pair.Artist.Id).ToArray(), maximum);
                _smartPlaylistRules = _smartPlaylistStore!.Upsert(uid, rule);
                dialog.Close(); saved(rule.Id); RefreshQuickAccessCollection();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            { errorText.Text = error.Message; }
        };
        dialog.Closed += (_, _) => { if (ReferenceEquals(_smartRuleDialog, dialog)) _smartRuleDialog = null; };
        dialog.Content = root; dialog.Loaded += (_, _) => { name.Focus(); name.SelectAll(); }; ShowQuickDialog(dialog);
    }

    private Window QuickDialog(string title, double width, double height, Window? owner = null)
    {
        var dialog = new Window
        {
            Title = title + " · Yamuse", Width = width, Height = height,
            MinWidth = Math.Min(width, 560), MinHeight = Math.Min(height, 460),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResizeWithGrip, UseLayoutRounding = true
        };
        dialog.SetResourceReference(BackgroundProperty, "PanelBrush");
        dialog.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        var parent = owner ?? this;
        if (parent.IsVisible) dialog.Owner = parent;
        dialog.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; dialog.Close(); } };
        return dialog;
    }

    private void ShowQuickDialog(Window dialog)
    {
        // Unshown fixture windows can inspect and render the same WPF tree without
        // creating a native handle or interacting with the user's desktop.
        if (IsVisible) dialog.ShowDialog();
    }

    private TextBlock QuickText(string text, double size = 13, bool bold = false, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, bold ? 9 : 5, 0, 3) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "ForegroundBrush");
        return block;
    }

    private Button QuickButton(string content, string? style = null)
    {
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 7, 6), Padding = new Thickness(13, 8, 13, 8) };
        if (style is not null) button.Style = (Style)FindResource(style);
        AutomationProperties.SetName(button, content);
        return button;
    }

    private ListBox QuickList(string name)
    {
        var list = new ListBox { Name = name, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemContainerStyle = (Style)FindResource("FlatListItem") };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        AutomationProperties.SetName(list, name == "QuickAccessResults" ? "Результаты быстрого поиска" : "Сохранённые локальные подборки");
        return list;
    }

    private static DataTemplate QuickEntryTemplate() => QuickTwoLineTemplate("Title", "Subtitle", true);
    private static DataTemplate SmartRuleTemplate() => QuickTwoLineTemplate("Name", "Description", false);
    private static DataTemplate QuickTwoLineTemplate(string titlePath, string subtitlePath, bool type)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 9, 12, 9));
        var heading = new FrameworkElementFactory(typeof(TextBlock));
        heading.SetBinding(TextBlock.TextProperty, new Binding(titlePath));
        heading.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        heading.SetValue(TextBlock.FontSizeProperty, 14d);
        heading.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        heading.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundBrush"); panel.AppendChild(heading);
        var sub = new FrameworkElementFactory(typeof(TextBlock));
        sub.SetBinding(TextBlock.TextProperty, new Binding(subtitlePath));
        sub.SetValue(TextBlock.FontSizeProperty, 12d);
        sub.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
        sub.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.AppendChild(sub);
        if (type)
        {
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding("TypeLabel"));
            label.SetValue(TextBlock.FontSizeProperty, 10d); label.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
            label.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush"); panel.AppendChild(label);
        }
        return new DataTemplate { VisualTree = panel };
    }
}
