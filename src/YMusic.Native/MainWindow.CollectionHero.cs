using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace YMusic.Native;

public partial class MainWindow
{
    private AlbumDetails? _activeAlbumDetails;
    private HeroControlPlacement? _heroSortPlacement;
    private HeroControlPlacement? _heroDownloadPlacement;
    private HeroControlPlacement? _heroBackPlacement;
    private HeroControlPlacement? _heroFilterPlacement;
    private Visibility _heroSummaryVisibility;
    private Thickness _heroHostMargin, _heroContentMargin, _heroHeadingMargin, _heroToolbarMargin;
    private Panel? _heroHeadingPanel;
    private double _heroSortComboWidth;

    private void ShowCollectionHero(AlbumDetails album, string? fallbackTitle = null)
    {
        _activeAlbumDetails = album;
        ShowCollectionHero(BuildAlbumHero(album, fallbackTitle));
    }

    private void ShowCollectionHero(PlaylistDetails playlist)
    {
        _activeAlbumDetails = null;
        ShowCollectionHero(BuildPlaylistHero(playlist));
    }

    private void ShowCollectionHero(FrameworkElement content)
    {
        if (FindName("CollectionHero") is not ContentControl host) return;
        RestoreCollectionHeroLayout();
        _heroSortPlacement = HeroControlPlacement.Capture(ListSortControls);
        _heroDownloadPlacement = HeroControlPlacement.Capture(PlaylistDownloadText);
        _heroBackPlacement = HeroControlPlacement.Capture(BrowseBackButton);
        _heroFilterPlacement = HeroControlPlacement.Capture(TrackFilterPanel);
        _heroSummaryVisibility = CollectionSummary.Visibility;
        _heroHostMargin = host.Margin;
        _heroContentMargin = ContentGrid.Margin;
        _heroHeadingPanel = (Panel)host.Parent;
        _heroHeadingMargin = _heroHeadingPanel.Margin;
        _heroToolbarMargin = CollectionToolbar.Margin;
        _heroSortComboWidth = ListSortCombo.Width;
        // Detail actions and sorting share one existing row. The large title and
        // cover keep the full width instead of buying a separate sort-only row.
        MoveHeroControl(ListSortControls, CollectionToolbar);
        ListSortControls.Margin = new Thickness(12, 0, 0, 4);
        ListSortCombo.Width = 138;
        if (content is Grid heroSurface)
        {
            MoveHeroControl(BrowseBackButton, heroSurface);
            Grid.SetColumn(BrowseBackButton, 1);
            BrowseBackButton.HorizontalAlignment = HorizontalAlignment.Right;
            BrowseBackButton.VerticalAlignment = VerticalAlignment.Top;
            BrowseBackButton.Margin = new Thickness(0, 1, 0, 0);
            BrowseBackButton.Padding = new Thickness(12, 6, 12, 6);
            MoveHeroControl(TrackFilterPanel, heroSurface);
            Grid.SetColumn(TrackFilterPanel, 1);
            TrackFilterPanel.Width = 188;
            TrackFilterPanel.HorizontalAlignment = HorizontalAlignment.Right;
            TrackFilterPanel.VerticalAlignment = VerticalAlignment.Bottom;
            TrackFilterPanel.Margin = new Thickness(0, 0, 0, 1);
        }
        if (content is Grid hero && hero.Children.OfType<StackPanel>().SingleOrDefault() is { } text
            && text.Children.OfType<Grid>().SingleOrDefault(grid => grid.Name == "CollectionHeroCreatorRow") is { } credits)
        {
            MoveHeroControl(PlaylistDownloadText, credits);
            Grid.SetColumn(PlaylistDownloadText, 1);
            PlaylistDownloadText.Margin = new Thickness(12, 0, 0, 0);
            PlaylistDownloadText.MaxWidth = 220;
            PlaylistDownloadText.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        CollectionSummary.Visibility = Visibility.Collapsed;
        ContentGrid.Margin = new Thickness(_heroContentMargin.Left, 16, _heroContentMargin.Right, _heroContentMargin.Bottom);
        _heroHeadingPanel.Margin = new Thickness(_heroHeadingMargin.Left, _heroHeadingMargin.Top, _heroHeadingMargin.Right, 0);
        CollectionToolbar.Margin = new Thickness(_heroToolbarMargin.Left, 8, _heroToolbarMargin.Right, _heroToolbarMargin.Bottom);
        host.Margin = new Thickness(0);
        host.Content = content;
        host.Visibility = Visibility.Visible;
        CollectionTitle.Visibility = Visibility.Collapsed;
    }

    private void HideCollectionHero()
    {
        _activeAlbumDetails = null;
        RestoreCollectionHeroLayout();
        if (FindName("CollectionHero") is ContentControl host)
        {
            host.Content = null;
            host.Visibility = Visibility.Collapsed;
        }
        CollectionTitle.Visibility = Visibility.Visible;
    }

    private void RestoreCollectionHeroLayout()
    {
        if (_heroSortPlacement is null) return;
        _heroSortPlacement.Restore(ListSortControls);
        _heroDownloadPlacement?.Restore(PlaylistDownloadText);
        _heroBackPlacement?.Restore(BrowseBackButton);
        _heroFilterPlacement?.Restore(TrackFilterPanel);
        CollectionSummary.Visibility = _heroSummaryVisibility;
        ContentGrid.Margin = _heroContentMargin;
        if (_heroHeadingPanel is not null) _heroHeadingPanel.Margin = _heroHeadingMargin;
        CollectionToolbar.Margin = _heroToolbarMargin;
        ListSortCombo.Width = _heroSortComboWidth;
        if (FindName("CollectionHero") is ContentControl host) host.Margin = _heroHostMargin;
        _heroSortPlacement = _heroDownloadPlacement = null;
        _heroBackPlacement = _heroFilterPlacement = null;
        _heroHeadingPanel = null;
    }

    private static void MoveHeroControl(FrameworkElement control, Panel destination)
    {
        if (control.Parent is Panel parent) parent.Children.Remove(control);
        destination.Children.Add(control);
    }

    private sealed record HeroControlPlacement(Panel Parent, int Index, Thickness Margin, int Column,
        double Width, double MaxWidth, HorizontalAlignment Horizontal, VerticalAlignment Vertical,
        Thickness? Padding, TextTrimming TextTrimming = TextTrimming.None)
    {
        internal static HeroControlPlacement Capture(FrameworkElement control)
        {
            var parent = (Panel)control.Parent;
            return new(parent, parent.Children.IndexOf(control), control.Margin, Grid.GetColumn(control),
                control.Width, control.MaxWidth, control.HorizontalAlignment, control.VerticalAlignment,
                control is Control content ? content.Padding : null, control is TextBlock text ? text.TextTrimming : TextTrimming.None);
        }
        internal void Restore(FrameworkElement control)
        {
            if (control.Parent is Panel current) current.Children.Remove(control);
            Parent.Children.Insert(Math.Min(Index, Parent.Children.Count), control);
            control.Margin = Margin;
            control.Width = Width;
            control.MaxWidth = MaxWidth;
            control.HorizontalAlignment = Horizontal;
            control.VerticalAlignment = Vertical;
            if (control is Control content && Padding is { } padding) content.Padding = padding;
            Grid.SetColumn(control, Column);
            if (control is TextBlock text) text.TextTrimming = TextTrimming;
        }
    }

    internal static FrameworkElement BuildAlbumHero(AlbumDetails album, string? fallbackTitle = null)
        => BuildCollectionHero("АЛЬБОМ", string.IsNullOrWhiteSpace(album.Title) ? fallbackTitle ?? "Альбом" : album.Title,
            album.Artists, album.ReleaseDate is { } released ? released.ToString("d MMMM yyyy", CollectionHeroCulture)
                : album.Year?.ToString(CultureInfo.InvariantCulture), album.Tracks, album.CoverUri, "");

    internal static FrameworkElement BuildPlaylistHero(PlaylistDetails playlist)
        => BuildCollectionHero("ПЛЕЙЛИСТ", playlist.Title, playlist.OwnerName ?? "",
            playlist.Created is { } created ? "Создан " + created.ToString("d MMMM yyyy", CollectionHeroCulture)
                : playlist.Modified is { } modified ? "Обновлён " + modified.ToString("d MMMM yyyy", CollectionHeroCulture) : null,
            playlist.Tracks, playlist.CoverUri, playlist.Description);

    private static readonly CultureInfo CollectionHeroCulture = CultureInfo.GetCultureInfo("ru-RU");

    private static FrameworkElement BuildCollectionHero(string kind, string title, string creator, string? date,
        IReadOnlyList<TrackRow> tracks, string? coverUri, string description)
    {
        var hero = new Grid { Name = "CollectionHeroSurface", MinHeight = 120, MaxHeight = 128, ClipToBounds = true };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(hero, kind == "АЛЬБОМ" ? "Об альбоме" : "О плейлисте");
        var cover = new Border { Width = 120, Height = 120, CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        cover.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        var coverContents = new Grid();
        var placeholder = new TextBlock { Text = "\uE8D6", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 36,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        coverContents.Children.Add(placeholder);
        var image = new Image { Name = "CollectionHeroCover", Width = 120, Height = 120, Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, 120, 120), 12, 12) };
        AutomationProperties.SetName(image, "Обложка «" + title + "»");
        Artwork.SetPixelSize(image, 256);
        Artwork.SetSourceUri(image, coverUri);
        coverContents.Children.Add(image); cover.Child = coverContents; hero.Children.Add(cover);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
        Grid.SetColumn(text, 1); hero.Children.Add(text);
        var label = HeroText("CollectionHeroKind", kind, 10);
        label.FontWeight = FontWeights.SemiBold; label.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        text.Children.Add(label);
        var heading = HeroText("CollectionHeroTitle", title, 32);
        heading.Foreground = ThemeManager.Brush("#F3F1F0");
        heading.FontWeight = FontWeights.Bold; heading.Margin = new Thickness(0, 2, 94, 0);
        text.Children.Add(heading);
        var creditsRow = new Grid { Name = "CollectionHeroCreatorRow", Margin = new Thickness(0, string.IsNullOrWhiteSpace(creator) ? 0 : 2, 200, 0) };
        creditsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        creditsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (!string.IsNullOrWhiteSpace(creator))
        {
            var byline = HeroText("CollectionHeroCreator", creator, 13);
            creditsRow.Children.Add(byline);
        }
        text.Children.Add(creditsRow);
        var information = new List<string>();
        if (!string.IsNullOrWhiteSpace(date)) information.Add(date);
        information.Add(CollectionHeroTrackCount(tracks.Count));
        // An incomplete duration is explicitly marked; unknown durations never appear as a complete total.
        var knownDurations = tracks.Where(track => track.DurationMs > 0).ToArray();
        if (knownDurations.Length > 0)
        {
            var durationMs = knownDurations.Aggregate(0L, (sum, track) => sum > long.MaxValue - track.DurationMs ? long.MaxValue : sum + track.DurationMs);
            var minutes = durationMs / 60000;
            var duration = minutes >= 60 ? $"{minutes / 60} ч {minutes % 60} мин" : minutes > 0 ? $"{minutes} мин" : "меньше минуты";
            information.Add((knownDurations.Length < tracks.Count ? "не менее " : "") + duration);
        }
        var facts = HeroText("CollectionHeroFacts", string.Join(" · ", information), 12);
        var factsRow = new Grid { Name = "CollectionHeroFactsRow", Margin = new Thickness(0, 5, 200, 0) };
        factsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        factsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        factsRow.Children.Add(facts); text.Children.Add(factsRow);
        if (!string.IsNullOrWhiteSpace(description))
        {
            var note = HeroText("CollectionHeroDescription", description, 12);
            note.Margin = new Thickness(0, 7, 200, 0); text.Children.Add(note);
        }
        return hero;
    }

    private static TextBlock HeroText(string name, string value, double size)
    {
        var text = new TextBlock { Name = name, Text = value, FontSize = size,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return text;
    }

    private static string CollectionHeroTrackCount(int count)
    {
        var word = count % 100 is >= 11 and <= 14 ? "треков" : (count % 10) switch { 1 => "трек", 2 or 3 or 4 => "трека", _ => "треков" };
        return count.ToString("N0", CollectionHeroCulture) + " " + word;
    }
}
