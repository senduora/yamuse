using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using YMusic.Native;

internal static partial class Program
{
    private static async Task CheckSelectorLabels040()
    {
        await Run("SelectorLabels040: update intervals render their actual labels closed and open in both themes", Sl040Updates);
        await Run("SelectorLabels040: source title artist album and duration sort choices show labels", Sl040Sort);
        await Run("SelectorLabels040: quality theme speed and parallel download values retain their presentation", Sl040OtherSettings);
    }

    private static string Sl040Collapsed(ComboBox combo)
    {
        combo.ApplyTemplate(); combo.UpdateLayout();
        var toggle = combo.Template.FindName("DropDownToggle", combo) as ToggleButton;
        Assert(toggle is not null, "the actual shared selector template was not applied");
        toggle!.ApplyTemplate(); combo.UpdateLayout();
        var content = SaVisuals<ContentPresenter>(toggle).Single(); content.ApplyTemplate(); combo.UpdateLayout();
        return string.Concat(SaVisuals<TextBlock>(content).Select(text => text.Text));
    }

    private static async Task Sl040Updates()
    {
        var savedTheme = ThemeManager.Mode;
        var facts = new List<object>();
        try
        {
            foreach (var theme in new[] { "Dark", "Light" })
            {
                using var f = new SaFixture("selector-labels040-updates-" + theme); var w = f.Window;
                ThemeManager.SetMode(theme);
                Assert(ThemeManager.Mode == theme, "the private fixture reset the requested theme");
                Su016Service(w, "selector-labels040-updates-" + theme, new Su016Backend { Version = null });
                var store = new PreferencesStore(Path.Combine(_directory, "selector-labels040-prefs-" + theme));
                U317Set(w, "_preferenceStore", store); U317Set(w, "_preferencesReady", true);
                SaClick(Control<Button>(w, "SettingsButton"));
                var combo = SaField<ComboBox>(w, "_updateFrequencyCombo");
                Assert(combo.Items.Count == 6, "an update interval disappeared");
                for (var index = 0; index < combo.Items.Count; index++)
                {
                    var option = combo.Items[index];
                    var expected = (string)option.GetType().GetProperty("Label")!.GetValue(option)!;
                    var value = (UpdateCheckFrequency)option.GetType().GetProperty("Value")!.GetValue(option)!;
                    combo.SelectedIndex = index; w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Loaded);
                    Assert(Sl040Collapsed(combo) == expected, theme + ": collapsed interval shows object metadata instead of " + expected + ": " + Sl040Collapsed(combo));
                    combo.IsDropDownOpen = true; await Dispatcher.Yield(DispatcherPriority.Loaded); w.UpdateLayout();
                    var row = combo.ItemContainerGenerator.ContainerFromIndex(index) as ComboBoxItem;
                    Assert(row is not null && string.Concat(SaVisuals<TextBlock>(row).Select(text => text.Text)) == expected,
                        "open interval differs from its collapsed caption");
                    combo.IsDropDownOpen = false;
                    Assert(combo.SelectedValue is UpdateCheckFrequency actual && actual == value && store.Load().UpdateCheckFrequency == value,
                        "caption fix changed the value or immediate persistence");
                    facts.Add(new { theme, label = expected, value = value.ToString() });
                }
                combo.BringIntoView(); w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                App.Capture(w, Path.Combine(_directory, "selector-labels040-" + theme + ".png"));
            }
        }
        finally { ThemeManager.SetMode(savedTheme); }
        File.WriteAllText(Path.Combine(_directory, "selector-labels040-facts.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Sl040Sort()
    {
        using var f = new SaFixture("selector-labels040-sort"); var w = f.Window;
        var rows = new[] { new TrackRow { Id = "s2", RequestId = "s2", Title = "B", Artist = "B", Album = "B", DurationMs = 30000, Available = true },
            new TrackRow { Id = "s1", RequestId = "s1", Title = "A", Artist = "A", Album = "A", DurationMs = 10000, Available = true } };
        SaInvoke(w, "ShowRows", (object)rows);
        var combo = Control<ComboBox>(w, "ListSortCombo");
        Assert(combo.Items.Count >= 5, "sort fixture did not expose the real options");
        for (var index = 0; index < combo.Items.Count; index++)
        {
            var option = combo.Items[index]; var expected = (string)option.GetType().GetProperty("Label")!.GetValue(option)!;
            combo.SelectedIndex = index; w.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.Loaded);
            Assert(Sl040Collapsed(combo) == expected, "collapsed sort shows an object instead of " + expected);
        }
    }

    private static Task Sl040OtherSettings()
    {
        using var f = new SaFixture("selector-labels040-primitives"); var w = f.Window;
        Su016Service(w, "selector-labels040-primitives", new Su016Backend { Version = null });
        SaClick(Control<Button>(w, "SettingsButton"));
        foreach (var name in new[] { "SettingsQuality", "SettingsTheme", "SettingsSpeed", "SettingsDownloads" })
        {
            var combo = Control<ComboBox>(w, name);
            foreach (var item in combo.Items.Cast<object>().ToArray())
            {
                combo.SelectedItem = item; w.UpdateLayout(); var text = Sl040Collapsed(combo);
                if (item is ComboBoxItem option) Assert(text == option.Content.ToString(), name + ": item content changed");
                else Assert((double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var n)
                    || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                    && Math.Abs(n - Convert.ToDouble(item, CultureInfo.CurrentCulture)) < .001, name + ": numeric value changed: " + text);
            }
        }
        return Task.CompletedTask;
    }
}
