using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckCompactTransfer016()
    {
        await Run("CompactTransfer016: thin playlist progress preserves pause and hides after successful resume", Compact016Playlist);
        await Run("CompactTransfer016: partial results keep concise error counts and detailed Downloads", Compact016Errors);
        await Run("CompactTransfer016: automatic success preserves important page status without technical summaries", Compact016Automatic);
    }

    private static async Task Compact016Playlist()
    {
        using var f = new DownloadCenterFixture("compact016-playlist", 2) { HoldFirstAttempts = true };
        var w = f.Window;
        SaInvoke(w, "ShowPlaylist", new PlaylistDetails("12345", "compact016", "Компактный прогресс", "", 0,
            [DcTrack("1"), DcTrack("2"), DcTrack("3")]));
        var task = Call(w, "DownloadPlaylistAsync");
        await Until(() => f.Queue.Entries.Any(entry => entry.State == DownloadEntryState.Completed) && f.Active > 0);
        SaInvoke(w, "FlushDownloadProgress"); w.UpdateLayout();
        var panel = Control<Border>(w, "PlaylistTransferPanel");
        var bar = Control<ProgressBar>(w, "PlaylistTransferBar");
        var details = Control<TextBlock>(w, "PlaylistTransferDetails");
        Assert(panel.Visibility == Visibility.Visible && panel.ActualHeight is >= 35 and <= 45 && bar.Height == 3,
            "active playlist progress did not use its compact height and thin bar");
        Assert(bar.Value is > 0 and < 100 && details.Text == "Скачано 1 из 3", "compact progress lost saved count or real work");
        Compact016NoTechnical(details.Text);
        App.Capture(w, Path.Combine(_directory, "compact-transfer016-running.png"));
        SaInvoke(w, "PauseDownloadQueue"); await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(panel.Visibility == Visibility.Visible && Control<TextBlock>(w, "PlaylistTransferTitle").Text.Contains("паузе")
            && f.Library.Read().Count == 1, "pause hid unfinished progress or lost completed music");
        f.HoldFirstAttempts = false;
        await Call(w, "ResumeDownloadQueue").WaitAsync(TimeSpan.FromSeconds(5));
        Assert(panel.Visibility == Visibility.Collapsed && bar.Value == 100 && f.Library.Read().Count == 3,
            "fully successful playlist retained its transfer panel or lost saved music");
        Assert(Control<TextBlock>(w, "PlaylistDownloadText").Text == "Все треки доступны офлайн", "completion lost the existing offline header");
        Assert(Control<TextBlock>(w, "StatusText").Text == "Музыка скачана и доступна офлайн.", "manual completion retained a technical queue summary");
        var summary = SaVisuals<TextBlock>(w.DownloadCenterView).Single(text => text.Name == "DownloadCenterSummary").Text;
        Assert(summary.Contains("сохранено 3/3") && summary.Contains("активно 0")
            && summary.Contains(DownloadEntry.FormatBytes(3L * DownloadCenterFixture.Media.Length)), "Downloads lost full queue and storage information");
        File.WriteAllText(Path.Combine(_directory, "compact-transfer016-completed.json"), JsonSerializer.Serialize(new
        { panel = panel.Visibility.ToString(), bar.Value, downloaded = f.Library.Read().Count, pageStatus = Control<TextBlock>(w, "StatusText").Text, downloadsSummary = summary }));
    }

    private static async Task Compact016Errors()
    {
        using var f = new DownloadCenterFixture("compact016-errors", 2) { FailTrack = "2" };
        var w = f.Window;
        SaInvoke(w, "ShowPlaylist", new PlaylistDetails("12345", "compact016-errors", "Неполный результат", "", 0,
            [DcTrack("1"), DcTrack("2"), DcTrack("3", false)]));
        await Call(w, "DownloadPlaylistAsync"); w.UpdateLayout();
        var details = Control<TextBlock>(w, "PlaylistTransferDetails").Text;
        var status = Control<TextBlock>(w, "StatusText");
        Assert(Control<Border>(w, "PlaylistTransferPanel").Visibility == Visibility.Visible
            && Control<ProgressBar>(w, "PlaylistTransferBar").Value == 100 && details == "Скачано 1 из 3 · ошибок 1 · недоступно 1",
            "partial terminal result hid failures or falsely claimed that every track was saved");
        Compact016NoTechnical(details);
        Assert(status.Text.Contains("Не удалось скачать: 1") && status.Text.Contains("Недоступно: 1") && status.Text.Contains("«Загрузках»")
            && ((SolidColorBrush)status.Foreground).Color == Color.FromRgb(255, 154, 139), "important download problems lost their page warning");
        Compact016NoTechnical(status.Text);
        Assert(f.Queue.Entries[1].Error.Contains("Аудиосервер") && f.Queue.Entries[2].Error.Contains("недоступен"),
            "compact presentation discarded individual failure reasons");
        App.Capture(w, Path.Combine(_directory, "compact-transfer016-errors.png"));
    }

    private static async Task Compact016Automatic()
    {
        using var f = new DownloadCenterFixture("compact016-auto", 1);
        var w = f.Window;
        const string important = "Не удалось прочитать отметки скачанного. Аудиофайлы сохранены.";
        SaInvoke(w, "SetStatus", important, true);
        await Call(w, "StartDownloadBatchAsync", new[] { DcTrack("1") }, "Автоскачивание «Мне нравится»", null!, true);
        var status = Control<TextBlock>(w, "StatusText");
        Assert(f.Library.Read().Count == 1 && status.Text == important && ((SolidColorBrush)status.Foreground).Color == Color.FromRgb(255, 154, 139),
            "background success replaced an important page error");
        Assert(SaVisuals<TextBlock>(w.DownloadCenterView).Single(text => text.Name == "DownloadCenterSummary").Text.Contains("сохранено 1/1"),
            "automatic completion lost its detailed Downloads summary");
    }

    private static void Compact016NoTechnical(string text)
    {
        foreach (var detail in new[] { "активно", "в работе", "получено", "сохранено", "ошибок 0", " КБ", " МБ", " ГБ" })
            Assert(!text.Contains(detail, StringComparison.OrdinalIgnoreCase), "ordinary page retained technical download details: " + text);
    }
}
