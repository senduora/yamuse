using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YMusic.Native;

public partial class MainWindow
{
    private ArtistDetails? _artistDetails;
    private bool? _artistLiked;
    private ArtistMembershipRead? _artistMembershipRead;
    private sealed record ArtistMembershipRead(string Id, string OwnerUid, int AccountGeneration, CancellationTokenSource Cancellation);
    private List<ArtistCollectionChange>? _likedArtistsReadChanges;
    private sealed record ArtistCollectionChange(CatalogItem Artist, bool Liked);
    private readonly Dictionary<string, IReadOnlyList<ArtistReference>> _resolvedArtists = new(StringComparer.Ordinal);
    private int _resolvedArtistGeneration = -1;

    private async Task OpenArtistAsync(string id, string? name = null, bool remember = true)
    {
        if (_session is null) { SetStatus("Для открытия исполнителя подключитесь к Яндексу."); return; }
        if (remember) RememberBrowseView();
        _openedCatalogItem = new(id, "artist", name ?? "Исполнитель", "");
        NavigateSection("artist-detail", name ?? "Исполнитель", remember: false);
        ClearArtistDetails(name ?? "Исполнитель", "Загружаем музыку исполнителя…");
        await ReadArtistDetailAsync(id);
    }

    private Task ReadArtistDetailAsync(string id)
        => CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var details = await _api.GetArtistDetailsAsync(session, id, token);
            if (!isCurrent()) return;
            ShowArtistDetails(details);
            SetStatus("Популярные треки · " + details.TopTracks.Count.ToString("N0"));
            await RefreshArtistMembershipAsync(id, session, token);
        });

    private async Task RefreshArtistMembershipAsync(string id, Session? session = null, CancellationToken navigationToken = default)
    {
        if (_closed || _session is null || _account is not { } account || _section != "artist-detail" || _artistDetails?.Id != id) return;
        CancelArtistMembershipRead();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token, navigationToken);
        var read = new ArtistMembershipRead(id, account.Uid, _accountGeneration, cts);
        _artistMembershipRead = read;
        var view = _viewGeneration;
        bool Current() => !_closed && !cts.IsCancellationRequested && ReferenceEquals(_artistMembershipRead, read)
            && view == _viewGeneration && read.AccountGeneration == _accountGeneration && read.OwnerUid == _account?.Uid
            && _section == "artist-detail" && _artistDetails?.Id == id;
        try
        {
            session ??= await EnsureSessionAsync(cts.Token);
            if (!Current()) return;
            var items = await _api.GetLikedArtistsAsync(session, account.Uid, cts.Token);
            if (Current()) ApplyArtistMembership(id, items.Any(item => item.Id == id));
        }
        catch (Exception ex) { if (Current()) _diagnostics.LastErrorType = "ArtistLike:" + ex.GetType().Name; }
        finally { if (ReferenceEquals(_artistMembershipRead, read)) _artistMembershipRead = null; }
    }

    private void CancelArtistMembershipRead()
    {
        var read = _artistMembershipRead;
        _artistMembershipRead = null;
        read?.Cancellation.Cancel();
    }

    private async Task ReadLikedArtistsCollectionAsync(Session session, string uid, CancellationToken token,
        Func<bool> isCurrent, Action<IReadOnlyList<CatalogItem>> show)
    {
        var changes = new List<ArtistCollectionChange>();
        _likedArtistsReadChanges = changes;
        try
        {
            var rows = await _api.GetLikedArtistsAsync(session, uid, token);
            if (!isCurrent()) return;
            foreach (var change in changes) rows = ReconcileLikedArtists(rows, change);
            show(rows);
        }
        finally { if (ReferenceEquals(_likedArtistsReadChanges, changes)) _likedArtistsReadChanges = null; }
    }

    private static IReadOnlyList<CatalogItem> ReconcileLikedArtists(IReadOnlyList<CatalogItem> rows, ArtistCollectionChange change)
    {
        var exists = rows.Any(row => row.Kind == "artist" && row.Id == change.Artist.Id);
        if (exists == change.Liked) return rows;
        return change.Liked ? [change.Artist, .. rows] : rows.Where(row => row.Kind != "artist" || row.Id != change.Artist.Id).ToArray();
    }

    private void ClearArtistDetails(string name, string message)
    {
        _artistDetails = null; _artistLiked = null;
        ArtistName.Text = name;
        ArtistMonthlyText.Text = ""; ArtistMonthlyText.Visibility = Visibility.Collapsed;
        ArtistDescription.Text = ""; ArtistDescription.Visibility = Visibility.Collapsed;
        ArtistTopTracks.ItemsSource = ArtistAlbums.ItemsSource = ArtistSimilar.ItemsSource = null;
        ArtistAlbumsSection.Visibility = ArtistSimilarSection.Visibility = Visibility.Collapsed;
        ArtistPlayButton.IsEnabled = ArtistAllTracksButton.IsEnabled = ArtistLikeButton.IsEnabled = false;
        ArtistLikeButton.Content = "♡ В коллекцию";
        Artwork.SetSourceUri(ArtistPortrait, null);
        ArtistEmpty.Text = message; ArtistEmpty.Visibility = Visibility.Visible;
    }

    private void ShowArtistDetails(ArtistDetails artist)
    {
        _artistDetails = artist;
        _openedCatalogItem = new(artist.Id, "artist", artist.Name, "", CoverUri: artist.CoverUri);
        ArtistName.Text = CollectionTitle.Text = artist.Name;
        ArtistMonthlyText.Text = artist.MonthlyListeners is { } count ? $"{count:N0} слушателей за месяц" : "";
        ArtistMonthlyText.Visibility = artist.MonthlyListeners.HasValue ? Visibility.Visible : Visibility.Collapsed;
        ArtistDescription.Text = artist.Description;
        ArtistDescription.Visibility = string.IsNullOrWhiteSpace(artist.Description) ? Visibility.Collapsed : Visibility.Visible;
        Artwork.SetSourceUri(ArtistPortrait, artist.CoverUri);
        ApplyOfflineStates(artist.TopTracks);
        ArtistTopTracks.ItemsSource = artist.TopTracks;
        ArtistAlbums.ItemsSource = artist.Albums;
        ArtistSimilar.ItemsSource = artist.SimilarArtists;
        ArtistAlbumsSection.Visibility = artist.Albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ArtistSimilarSection.Visibility = artist.SimilarArtists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ArtistEmpty.Text = "Яндекс не вернул популярных треков этого исполнителя.";
        ArtistEmpty.Visibility = artist.TopTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArtistPlayButton.IsEnabled = artist.TopTracks.Any(row => row.CanPlay);
        ArtistAllTracksButton.IsEnabled = true;
        UpdateArtistLike(); UpdateBrowseButtons();
        ArtistScroll.ScrollToTop();
        Motion.Enter(ArtistPanel);
    }

    private async void ArtistName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        e.Handled = true;
        if (button.Tag is ArtistReference reference) { await OpenArtistAsync(reference.Id, reference.Name); return; }
        if (button.Tag is not TrackRow track || _session is null) return;
        var generation = _accountGeneration;
        var lookup = BeginTrackLinkLookup();
        if (_resolvedArtistGeneration != generation) { _resolvedArtists.Clear(); _resolvedArtistGeneration = generation; }
        try
        {
            if (!_resolvedArtists.TryGetValue(track.RequestId, out var artists))
            {
                var session = await EnsureSessionAsync(lookup.Token);
                if (!lookup.IsCurrent()) return;
                var rows = await _api.GetTracksAsync(session, [TrackReference(track)], 0, lookup.Token);
                if (!lookup.IsCurrent()) return;
                artists = rows.Single().Artists;
                if (_resolvedArtists.Count >= 256) _resolvedArtists.Remove(_resolvedArtists.Keys.First());
                _resolvedArtists[track.RequestId] = artists;
            }
            if (!ReferenceEquals(button.Tag, track) || !lookup.IsCurrent()) return;
            if (artists.Count == 1) await OpenArtistAsync(artists[0].Id, artists[0].Name);
            else if (artists.Count > 1)
            {
                ShowTrackArtistsMenu(button, track, artists);
            }
            else SetStatus("У этого трека Яндекс не указал карточку исполнителя.");
        }
        catch (Exception ex) { if (lookup.IsCurrent()) ShowError(ex); }
        finally { lookup.Finish(); }
    }

    private async void ArtistPlay_Click(object sender, RoutedEventArgs e) => await PlayArtistTopAsync(null);
    private async void ArtistCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogItem item }) await OpenCatalogAsync(item);
    }
    private async void ArtistTrackPlay_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: TrackRow row }) await PlayArtistTopAsync(row);
    }
    private async void ArtistTop_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton != MouseButton.Left || source is null || FromButton(source)
            || ItemsControl.ContainerFromElement(ArtistTopTracks, source) is not ListBoxItem item) return;
        var index = ArtistTopTracks.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0 || index >= ArtistTopTracks.Items.Count || ArtistTopTracks.Items[index] is not TrackRow row) return;
        e.Handled = true;
        await PlayArtistTopAsync(row);
    }
    private async Task PlayArtistTopAsync(TrackRow? selected)
    {
        if (_section != "artist-detail" || _artistDetails is not { } artist
            || selected is not null && !artist.TopTracks.Contains(selected)) return;
        selected ??= artist.TopTracks.FirstOrDefault(row => row.CanPlay);
        if (selected is null || !selected.CanPlay) return;
        _playbackQueue.SetItems(artist.TopTracks, selected);
        await PlayTrackAsync(selected);
    }

    private async void ArtistAllTracks_Click(object sender, RoutedEventArgs e)
        => await LoadArtistTracksAsync();
    private async Task LoadArtistTracksAsync(bool remember = true)
    {
        if (_openedCatalogItem is not { Kind: "artist" } artist || _section is not ("artist-detail" or "artist-tracks")) return;
        if (_section == "artist-detail" && _artistDetails is null) return;
        if (remember) RememberBrowseView();
        NavigateSection("artist-tracks", artist.Title + " · все треки", remember: false);
        _artistPage = 0;
        ShowRows([]);
        await ReadInitialArtistTracksAsync(artist.Id);
    }

    private bool _artistTracksLoaded;

    private Task ReadInitialArtistTracksAsync(string id)
    {
        _artistTracksLoaded = false;
        return CatalogReadAsync(async (session, token, isCurrent) =>
        {
            var rows = await _api.GetArtistTracksAsync(session, id, 0, token);
            if (!isCurrent()) return;
            _artistTracksLoaded = true;
            ShowRows(rows);
            ShowArtistPagination(rows.Count == 100);
            SetStatus($"Треки исполнителя · {rows.Count:N0}");
        });
    }

    private void ShowArtistPagination(bool hasMore)
    {
        PlaylistActions.Visibility = MoreArtistButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateArtistLike()
    {
        ArtistLikeButton.IsEnabled = _artistDetails is not null && _artistLiked.HasValue && !_catalogWriting && _session is not null;
        ArtistLikeButton.Content = _artistLiked == true ? "♥ В коллекции" : "♡ В коллекцию";
    }
    private void ApplyArtistMembership(string id, bool liked)
    {
        UpdateBrowseArtistMembership(id, liked);
        if (_section != "artist-detail" || _artistDetails?.Id != id) return;
        _artistLiked = liked;
        UpdateArtistLike();
    }
    private async void ArtistLike_Click(object sender, RoutedEventArgs e)
    {
        if (_artistDetails is not { } artist || !_artistLiked.HasValue || _account is not { } account) return;
        var liked = !_artistLiked.Value;
        var item = new CatalogItem(artist.Id, "artist", artist.Name, "Исполнитель", CoverUri: artist.CoverUri);
        var view = _viewGeneration;
        await CatalogMutationAsync(async (session, token) =>
        { await _api.SetEntityLikeAsync(session, account.Uid, item, liked, token); return liked; },
        result =>
        {
            if (_account?.Uid != account.Uid) return;
            // A page GET formed before this confirmed write may still return.
            // Invalidate only that artist/account read, without another request.
            if (_artistMembershipRead is { } read && read.Id == artist.Id && read.OwnerUid == account.Uid
                && read.AccountGeneration == _accountGeneration) CancelArtistMembershipRead();
            ApplyArtistMembership(artist.Id, result);
            ApplyArtistCollectionChange(new(item, result));
            var message = result ? "Исполнитель добавлен в коллекцию" : "Исполнитель убран из коллекции";
            SetStatus(view == _viewGeneration ? message : "Изменение сохранено в аккаунте.");
        }, requireSameView: false);
        UpdateArtistLike();
    }

    private void ApplyArtistCollectionChange(ArtistCollectionChange change)
    {
        _likedArtistsReadChanges?.Add(change);
        UpdateBrowseArtistCollection(change);
        if (_section != "artists" || !_catalogShowing) return;
        var existing = _catalogItems.FirstOrDefault(row => row.Kind == "artist" && row.Id == change.Artist.Id);
        if (change.Liked && existing is null) _catalogItems.Insert(0, change.Artist);
        else if (!change.Liked && existing is not null) _catalogItems.Remove(existing);
        UpdateCount();
    }
}
