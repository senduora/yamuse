using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckUpdateApplyPreferences0317()
    {
        await Run("Update preferences0317: explicit opt-out during a pending restart remains applied and persisted", UpdatePreferenceOptOut);
        await Run("Update preferences0317: an unrelated live change during a blocked restart preserves the favorites choice", UpdatePreferenceUnrelatedChange);
    }
    private static void UpdatePreferenceWireFavorites(R4UpdateFixture fixture)
    {
        foreach (var name in new[] { "StatusText", "WaveStatus", "HomeStatus", "ArtistStatus" }) R4Set(fixture.Window, name, new TextBlock());
        var native = new CheckBox { IsChecked = true };
        native.Checked += (sender, args) => R4Call(fixture.Window, "AutoFavorites_Changed", sender!, args);
        native.Unchecked += (sender, args) => R4Call(fixture.Window, "AutoFavorites_Changed", sender!, args);
        R4Set(fixture.Window, "AutoFavoritesCheckBox", native);
        var settings = new CheckBox { IsChecked = true };
        settings.Checked += (sender, args) => R4Call(fixture.Window, "SettingsCheck_Changed", sender!, args);
        settings.Unchecked += (sender, args) => R4Call(fixture.Window, "SettingsCheck_Changed", sender!, args);
        R4Set(fixture.Window, "SettingsAutoFavorites", settings);
    }
    private static async Task UpdatePreferenceOptOut()
    {
        using var fixture = new R4UpdateFixture("update-preferences-opt-out"); UpdatePreferenceWireFavorites(fixture);
        var store = R4Get<PreferencesStore>(fixture.Window, "_preferenceStore");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        R4Set(fixture.Window, "_downloadTask", pending.Task);
        var restart = R4Task<bool>(fixture.Window, "ApplyAppUpdateAsync", fixture.Updates);
        Assert(!restart.IsCompleted && R4Get<bool>(fixture.Window, "_downloadsAutoSuspended") && fixture.Backend.Applies == 0,
            "the update did not suspend downloads and await their atomic completion");
        R4Get<CheckBox>(fixture.Window, "SettingsAutoFavorites").IsChecked = false;
        var during = store.Load().AutoDownloadFavorites;
        pending.TrySetResult(); var applied = await restart;
        var after = store.Load().AutoDownloadFavorites;
        File.WriteAllText(Path.Combine(_directory, "update-preferences-opt-out.json"), JsonSerializer.Serialize(new {
            during, after, runtimeAfter = R4Get<bool>(fixture.Window, "_autoFavoritesEnabled"), applied,
            fakeBackendApplies = fixture.Backend.Applies, realUpdate = false
        }));
        Assert(!during && !after && !R4Get<bool>(fixture.Window, "_autoFavoritesEnabled")
            && R4Get<CheckBox>(fixture.Window, "SettingsAutoFavorites").IsChecked == false
            && applied && fixture.Backend.Applies == 1,
            "the updater overwrote the user's opt-out or did not wait for one explicit fake restart");
    }
    private static async Task UpdatePreferenceUnrelatedChange()
    {
        using var fixture = new R4UpdateFixture("update-preferences-other-setting");
        var store = R4Get<PreferencesStore>(fixture.Window, "_preferenceStore");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        R4Set(fixture.Window, "_downloadTask", pending.Task);
        var restart = R4Task<bool>(fixture.Window, "ApplyAppUpdateAsync", fixture.Updates);
        var speed = new ComboBox(); speed.Items.Add(1d); speed.Items.Add(1.5d);
        R4Set(fixture.Window, "SettingsSpeed", speed);
        speed.SelectionChanged += (sender, args) => R4Call(fixture.Window, "SettingsSelection_Changed", sender!, args);
        speed.SelectedItem = 1.5d;
        var during = store.Load();
        R4Set(fixture.Window, "_cacheMaintenance", true);
        pending.TrySetResult(); var applied = await restart;
        var after = store.Load();
        File.WriteAllText(Path.Combine(_directory, "update-preferences-other-setting.json"), JsonSerializer.Serialize(new {
            autoDuring = during.AutoDownloadFavorites, autoAfter = after.AutoDownloadFavorites,
            speedAfter = after.PlaybackSpeed, runtimeAfter = R4Get<bool>(fixture.Window, "_autoFavoritesEnabled"),
            applied, fakeBackendApplies = fixture.Backend.Applies, realUpdate = false
        }));
        Assert(during.AutoDownloadFavorites && after.AutoDownloadFavorites && after.PlaybackSpeed == 1.5
            && R4Get<bool>(fixture.Window, "_autoFavoritesEnabled") && !applied && fixture.Backend.Applies == 0,
            "saving another setting leaked a temporary downloader state into preferences or bypassed maintenance");
    }
}
