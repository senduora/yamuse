using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YMusic.Native;

public partial class MainWindow
{
    private ArtistDetails? _artistDetails;
    private bool? _artistLiked;
    private readonly Dictionary<string, IReadOnlyList<ArtistReference>> _resolvedArtists = new(StringComparer.Ordinal);
    private int _resolvedArtistGeneration = -1;

    private async Task OpenArtistAsync(string id, string? name = null, bool remember = true)
    {
        if (_session is null) { SetStatus("Для открытия исполнителя подключитесь к Яндексу."); return; }
        if (remember) RememberBrowseView();
        _artistDetails = null; _artistLiked = null;
        _openedCatalogItem = new(id, "artist", name ?? "Исполнитель", "");
        NavigateSection("artist-detail", name ?? "Исполнитель", remember: false);
        ArtistName.Text = name ?? "Исполнитель";
        ArtistMonthlyText.Text = ""; ArtistMonthlyText.Visibility = Visibility.Collapsed;
        ArtistDescription.Text = ""; ArtistDescription.Visibility = Visibility.Collapsed;
        ArtistTopTracks.ItemsSource = null; ArtistAlbums.ItemsSource = null; ArtistSimilar.ItemsSource = null;
        ArtistAlbumsSection.Visibility = ArtistSimilarSection.Visibility = Visibility.Collapsed;
        ArtistPlayButton.IsEnabled = ArtistAllTracksButton.IsEnabled = ArtistLikeButton.IsEnabled = false;
        ArtistLikeButton.Content = "♡ В коллекцию";
        Artwork.SetSourceUri(ArtistPortrait, null);
        ArtistEmpty.Text = "Загружаем музыку исполнителя…"; ArtistEmpty.Visibility = Visibility.Visible;
        await CatalogReadAsync(async (session, token) =>
        {
            var details = await _api.GetArtistDetailsAsync(session, id, token);
            token.ThrowIfCancellationRequested();
            ShowArtistDetails(details);
            SetStatus("Популярные треки · " + details.TopTracks.Count.ToString("N0"));
            if (_account is { } account)
            {
                try
                {
                    var liked = await _api.GetLikedArtistsAsync(session, account.Uid, token);
                    token.ThrowIfCancellationRequested();
                    if (_section == "artist-detail" && _artistDetails?.Id == id)
                    { _artistLiked = liked.Any(item => item.Id == id); UpdateArtistLike(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { _diagnostics.LastErrorType = "ArtistLike:" + ex.GetType().Name; }
            }
        });
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
        var generation = _accountGeneration; var view = _viewGeneration;
        if (_resolvedArtistGeneration != generation) { _resolvedArtists.Clear(); _resolvedArtistGeneration = generation; }
        try
        {
            if (!_resolvedArtists.TryGetValue(track.RequestId, out var artists))
            {
                var token = _accountCts.Token;
                var session = await EnsureSessionAsync(token);
                var rows = await _api.GetTracksAsync(session, [TrackReference(track)], 0, token);
                token.ThrowIfCancellationRequested();
                if (generation != _accountGeneration || view != _viewGeneration || _closed) return;
                artists = rows.FirstOrDefault(row => row.RequestId == track.RequestId || row.Id == track.Id)?.Artists ?? [];
                if (_resolvedArtists.Count >= 256) _resolvedArtists.Remove(_resolvedArtists.Keys.First());
                _resolvedArtists[track.RequestId] = artists;
            }
            if (artists.Count == 1) await OpenArtistAsync(artists[0].Id, artists[0].Name);
            else if (artists.Count > 1)
            {
                ShowTrackArtistsMenu(button, artists);
            }
            else SetStatus("У этого трека Яндекс не указал карточку исполнителя.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
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
        if (FromButton(e.OriginalSource as DependencyObject)) return;
        if (ArtistTopTracks.SelectedItem is TrackRow row) await PlayArtistTopAsync(row);
    }
    private async Task PlayArtistTopAsync(TrackRow? selected)
    {
        if (_artistDetails is not { } artist) return;
        selected ??= artist.TopTracks.FirstOrDefault(row => row.CanPlay);
        if (selected is null || !selected.CanPlay) return;
        _playbackQueue.SetItems(artist.TopTracks, selected);
        await PlayTrackAsync(selected);
    }

    private async void ArtistAllTracks_Click(object sender, RoutedEventArgs e)
        => await LoadArtistTracksAsync();
    private async Task LoadArtistTracksAsync(bool remember = true)
    {
        if (_artistDetails is not { } artist) return;
        if (remember) RememberBrowseView();
        SetSection("artist-tracks", artist.Name + " · все треки");
        _openedCatalogItem = new(artist.Id, "artist", artist.Name, "", CoverUri: artist.CoverUri); _artistPage = 0;
        await CatalogReadAsync(async (session, token) =>
        {
            var rows = await _api.GetArtistTracksAsync(session, artist.Id, 0, token);
            token.ThrowIfCancellationRequested(); ShowRows(rows);
            PlaylistActions.Visibility = Visibility.Visible;
            MoreArtistButton.Visibility = rows.Count == 100 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus($"Треки исполнителя · {rows.Count:N0}");
        });
    }

    private void UpdateArtistLike()
    {
        ArtistLikeButton.IsEnabled = _artistDetails is not null && _artistLiked.HasValue && !_catalogWriting && _session is not null;
        ArtistLikeButton.Content = _artistLiked == true ? "♥ В коллекции" : "♡ В коллекцию";
    }
    private async void ArtistLike_Click(object sender, RoutedEventArgs e)
    {
        if (_artistDetails is not { } artist || !_artistLiked.HasValue || _account is not { } account) return;
        var liked = !_artistLiked.Value;
        var item = new CatalogItem(artist.Id, "artist", artist.Name, "", CoverUri: artist.CoverUri);
        await CatalogMutationAsync(async (session, token) =>
        { await _api.SetEntityLikeAsync(session, account.Uid, item, liked, token); return liked; },
        result => { _artistLiked = result; UpdateArtistLike(); SetStatus(result ? "Исполнитель добавлен в коллекцию" : "Исполнитель убран из коллекции"); });
        UpdateArtistLike();
    }
}
