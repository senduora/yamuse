using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Velopack.Locators;

namespace YMusic.Native;

internal static class UninstallSupport
{
    internal const string RequestArgument = "--uninstall";
    internal const string CompleteArgument = "--complete-full-uninstall";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YMusic.Native";
    private static string EventName => @"Local\YMusic.Native.FullUninstall." + WindowsIdentity.GetCurrent().User!.Value;

    internal static string ConfirmationText(UninstallPaths paths) =>
        "Полностью удалить Yamuse?\n\nБудут удалены:\n" +
        "• приложение и его ярлыки;\n" +
        "• сохранённый вход, настройки, кэш и диагностика:\n" + paths.Data + "\n" +
        "• вся скачанная приложением музыка и локальные плейлисты:\n" + paths.Music +
        (UninstallPathGuard.EntryExists(paths.LegacyData) ? "\n• данные предыдущей версии:\n" + paths.LegacyData : "") +
        (UninstallPathGuard.EntryExists(paths.LegacyMusic) ? "\n• скачанная музыка предыдущей версии:\n" + paths.LegacyMusic : "") +
        "\n\nПосторонние и изменённые аудиофайлы сохранятся. Восстановить удалённое нельзя.";

    // Called after Velopack.Run, before constructing App or acquiring its mutex.
    // Normal launches return false and never open account storage here.
    internal static bool TryHandleCommand(string[] args)
    {
        if (!args.Contains(RequestArgument, StringComparer.OrdinalIgnoreCase)
            && !args.Contains(CompleteArgument, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Check the fixed marker without requiring Update.exe/current
                // to remain readable: native uninstall may already be removing
                // them. Even a portable launch uses the same user data and
                // must not recreate it during this handover.
                var local = UninstallPathGuard.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (UninstallPathGuard.EntryExists(Path.Combine(local, "YMusic.Native", "full-uninstall.pending.json")))
                {
                    MessageBox.Show("Полное удаление Yamuse уже запрошено или не завершено.\n\nЧтобы завершить удаление или проверить оставшийся запрос, откройте удаление Yamuse в параметрах Windows. До этого приложение не будет открывать данные.",
                        "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Information);
                    return true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Не удалось проверить состояние удаления Yamuse. Приложение не будет открывать данные.\n\nПовторите запуск после завершения удаления или проверьте доступ к папке установки.",
                    "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
            return false;
        }
        try
        {
            var paths = CurrentInstalledPaths();
            if (args.Length == 1 && args[0].Equals(RequestArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (TryForwardToOpenApplication()) return true;
                using var main = new Mutex(true, BrandPaths.LegacyInstanceMutex, out var mainCreated);
                if (!mainCreated)
                    throw new IOException("Yamuse открыт. Закройте его обычной командой «Выйти» и повторите удаление.");
                if (IsReceiptConsumed(paths))
                {
                    if (MessageBox.Show("Данные Yamuse уже очищены, завершение штатного удаления запрошено.\n\nПовторно запустить удаление приложения?\n\nКнопка «Нет» сохранит защиту от запуска приложения: данные не будут созданы заново.",
                        "Завершение удаления Yamuse", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                        ScheduleConsumedRetry(paths, Environment.ProcessId);
                    return true;
                }
                if (MessageBox.Show(ConfirmationText(paths), "Полное удаление Yamuse", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    // Cancelling a stale request only removes our own marker.
                    // No account, settings, cache or audio path is touched.
                    CancelPendingReceipt(paths);
                    return true;
                }
                var nonce = CreateReceipt(paths);
                try { StartAfterExit(paths, Environment.ProcessId, paths.Executable, [CompleteArgument, nonce]); }
                catch { DeleteReceipt(paths, nonce); throw; }
                return true;
            }
            if (args.Length == 2 && args[0].Equals(CompleteArgument, StringComparison.OrdinalIgnoreCase))
            {
                Finish(paths, args[1]);
                return true;
            }
            throw new IOException("Некорректная команда удаления. Запустите удаление из настроек Windows или Yamuse.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or JsonException)
        {
            MessageBox.Show("Удаление не завершено.\n\n" + error.Message, "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
    }

    internal static UninstallPaths CurrentInstalledPaths()
    {
        var paths = UninstallPaths.ForCurrentUser();
        var locator = VelopackLocator.Current;
        ValidateInstallation(paths, locator.AppId, locator.IsPortable, locator.CurrentlyInstalledVersion is not null,
            locator.RootAppDir, locator.AppContentDir, locator.UpdateExePath, Environment.ProcessPath!);
        return paths;
    }

    internal static void ValidateInstallation(UninstallPaths paths, string? appId, bool portable, bool installed,
        string? installRoot, string? contentDirectory, string? updater, string executable)
    {
        if (portable || !installed || appId != BrandPaths.LegacyPackageId || installRoot is null || contentDirectory is null || updater is null
            || !UninstallPathGuard.Canonical(installRoot).Equals(paths.Install, StringComparison.OrdinalIgnoreCase)
            || !UninstallPathGuard.Canonical(updater).Equals(paths.Updater, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Полное удаление доступно только для установленного Yamuse в его штатной папке.");
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(paths.Install);
        using var root = UninstallPathGuard.HoldDirectory(paths.Install);
        if (Directory.EnumerateDirectories(paths.Install, "app-*").Any())
            throw new IOException("Формат установки Yamuse требует обновления. Полное удаление не начато.");
        using var update = UninstallPathGuard.OpenOwnedFile(paths.Updater, read: false, deleteAccess: false);
        var expectedDirectory = Path.Combine(paths.Install, "current");
        var current = ResolvedContent(paths, expectedDirectory);
        if (!ResolvedContent(paths, contentDirectory).Equals(current, StringComparison.OrdinalIgnoreCase)
            || !UninstallPathGuard.Canonical(executable).Equals(Path.Combine(current, "YMusic.Native.exe"), StringComparison.OrdinalIgnoreCase)
                && !UninstallPathGuard.Canonical(executable).Equals(paths.Executable, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Путь исполняемого файла не соответствует установленному Yamuse.");
        using var currentParents = UninstallPathGuard.HoldExistingAncestors(Path.Combine(current, "YMusic.Native.exe"));
        using var application = UninstallPathGuard.OpenOwnedFile(Path.Combine(current, "YMusic.Native.exe"), read: false, deleteAccess: false);
    }

    private static string ResolvedContent(UninstallPaths paths, string directory)
    {
        var path = UninstallPathGuard.Canonical(directory);
        if (!UninstallPathGuard.Contains(paths.Install, path) || path.Equals(paths.Install, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Папка приложения находится за пределами установки.");
        // Velopack may use a current junction to another version in the same
        // package root. Only that one junction is allowed, and its final target
        // must remain inside this exact root with no further redirected parent.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            if (!path.Equals(Path.Combine(paths.Install, "current"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Неожиданная ссылка в установленном приложении.");
            path = UninstallPathGuard.Canonical(new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("Не удалось проверить папку установленного приложения."));
        }
        if (!UninstallPathGuard.Contains(paths.Install, path) || path.Equals(paths.Install, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ссылка установленного приложения ведёт за пределы установки.");
        using var parents = UninstallPathGuard.HoldExistingAncestors(Path.Combine(path, "YMusic.Native.exe"));
        return path;
    }

    // This changes only the two commands in the existing, exact package key.
    // No key is created and unrelated/custom registry values are untouched.
    internal static bool RegisterWindowsUninstall()
    {
        try
        {
            var paths = CurrentInstalledPaths();
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key is null || key.GetValue("InstallLocation") is not string location
                || !UninstallPathGuard.Canonical(location).Equals(paths.Install, StringComparison.OrdinalIgnoreCase)) return false;
            var native = Quote(paths.Updater) + " --uninstall";
            var wrapper = Quote(paths.Executable) + " " + RequestArgument;
            if (key.GetValue("UninstallString") is not string command || command != native && command != wrapper) return false;
            if (key.GetValue("QuietUninstallString") is string quiet && quiet != native + " --silent") return false;
            key.SetValue("UninstallString", wrapper, RegistryValueKind.String);
            key.DeleteValue("QuietUninstallString", throwOnMissingValue: false);
            return Equals(key.GetValue("UninstallString"), wrapper) && key.GetValue("QuietUninstallString") is null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    internal static IDisposable Listen(Window owner, Func<Task> request)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            if (!owner.Dispatcher.HasShutdownStarted)
                owner.Dispatcher.BeginInvoke(new Action(async () => await request()));
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        return new Listener(signal, registration);
    }

    private static bool TryForwardToOpenApplication()
    {
        try { using var signal = EventWaitHandle.OpenExisting(EventName); signal.Set(); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    internal static string CreateReceipt(UninstallPaths paths)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(Path.Combine(paths.Install, "receipt"));
        if (!CancelPendingReceipt(paths)) throw new IOException("Очистка уже выполнена. Завершите штатное удаление Yamuse через параметры Windows.");
        using var origin = Process.GetCurrentProcess();
        var receipt = new Receipt(nonce, paths.Install, paths.Data, paths.Music, WindowsIdentity.GetCurrent().User!.Value,
            DateTimeOffset.UtcNow, origin.Id, origin.StartTime.ToUniversalTime().Ticks);
        var temporary = Path.Combine(paths.Install, "full-uninstall-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, receipt); file.Flush(flushToDisk: true); }
            File.Move(temporary, ReceiptPath(paths, nonce));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return nonce;
    }

    internal static void ValidateReceipt(UninstallPaths paths, string nonce)
        => _ = ReadReceipt(paths, nonce);

    private static Receipt ReadReceipt(UninstallPaths paths, string nonce)
    {
        _ = ReceiptPath(paths, nonce);
        var receipt = ReadAnyReceipt(paths);
        if (receipt.Consumed || receipt.Nonce != nonce || receipt.CreatedUtc < DateTimeOffset.UtcNow.AddMinutes(-30))
            throw new IOException("Подтверждение удаления истекло или уже использовано. Повторите удаление из настроек Windows.");
        return receipt;
    }

    private static Receipt ReadAnyReceipt(UninstallPaths paths)
    {
        var path = Path.Combine(paths.Install, "full-uninstall.pending.json");
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(path);
        using var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: false);
        using var file = new FileStream(handle, FileAccess.Read);
        if (file.Length > 4096) throw new IOException("Запрос удаления повреждён.");
        var receipt = JsonSerializer.Deserialize<Receipt>(file) ?? throw new IOException("Нет подтверждённого запроса удаления.");
        if (receipt.Nonce is null || receipt.Nonce.Length != 64 || !receipt.Nonce.All(Uri.IsHexDigit)
            || receipt.OriginPid <= 0 || receipt.OriginStartedUtcTicks <= 0
            || receipt.Install != paths.Install || !MatchesReceiptDataPaths(paths, receipt.Data, receipt.Music)
            || receipt.User != WindowsIdentity.GetCurrent().User!.Value || receipt.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new IOException("Запрос удаления повреждён или папки изменились. Защитный marker сохранён.");
        return receipt;
    }

    // A pending request from the previous release may be inspected/cancelled
    // after update. It never authorizes cleanup of the new branded locations.
    private static bool MatchesReceiptDataPaths(UninstallPaths paths, string data, string music) =>
        data == paths.Data && music == paths.Music || data == paths.LegacyData && music == paths.LegacyMusic;

    internal static void DeleteReceipt(UninstallPaths paths, string nonce)
    {
        // Never remove a newer request when an older waiter fails later.
        if (!HasPendingReceipt(paths)) return;
        var receipt = ReadReceipt(paths, nonce);
        CancelPendingReceipt(paths);
    }

    internal static bool HasPendingReceipt(UninstallPaths paths)
        => UninstallPathGuard.EntryExists(Path.Combine(paths.Install, "full-uninstall.pending.json"));

    internal static bool IsReceiptConsumed(UninstallPaths paths)
        => HasPendingReceipt(paths) && ReadAnyReceipt(paths).Consumed;

    internal static bool OriginProcessExited(UninstallPaths paths, string nonce)
    {
        var receipt = ReadReceipt(paths, nonce);
        try
        {
            using var origin = Process.GetProcessById(receipt.OriginPid);
            // A reused PID is not the process which authorized this request.
            return origin.HasExited || origin.StartTime.ToUniversalTime().Ticks != receipt.OriginStartedUtcTicks;
        }
        catch (ArgumentException) { return true; }
        // An access error is deliberately not interpreted as process exit.
    }

    internal static bool CancelPendingReceipt(UninstallPaths paths)
    {
        var path = Path.Combine(paths.Install, "full-uninstall.pending.json");
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(path);
        if (!UninstallPathGuard.EntryExists(path)) return true;
        if (ReadAnyReceipt(paths).Consumed) return false;
        using var file = UninstallPathGuard.OpenOwnedFile(path, read: false);
        UninstallPathGuard.DeleteByHandle(file);
        return true;
    }

    internal static void ScheduleConsumedRetry(UninstallPaths paths, int waitPid, Func<ProcessStartInfo, Process?>? start = null)
    {
        if (!IsReceiptConsumed(paths)) throw new IOException("Очистка данных ещё не завершена. Повторите полное удаление из настроек Windows.");
        StartAfterExit(paths, waitPid, paths.Updater, ["--uninstall"], start);
    }

    internal static void MarkReceiptConsumed(UninstallPaths paths, string nonce)
    {
        var receipt = ReadReceipt(paths, nonce);
        WriteReceipt(paths, nonce, receipt with { Consumed = true });
    }

    private static void WriteReceipt(UninstallPaths paths, string nonce, Receipt receipt)
    {
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(ReceiptPath(paths, nonce));
        var temporary = Path.Combine(paths.Install, "full-uninstall-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, receipt); file.Flush(flushToDisk: true); }
            File.Move(temporary, ReceiptPath(paths, nonce), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static ProcessStartInfo BuildWaitStart(UninstallPaths paths, int waitPid, string target, IReadOnlyList<string> arguments)
    {
        if (waitPid <= 0 || target != paths.Executable && target != paths.Updater) throw new IOException("Некорректный этап удаления.");
        var start = new ProcessStartInfo(paths.Updater) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.Install };
        start.ArgumentList.Add("start");
        start.ArgumentList.Add("--waitPid");
        start.ArgumentList.Add(waitPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(target);
        start.ArgumentList.Add("--");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static void StartAfterExit(UninstallPaths paths, int waitPid, string target, IReadOnlyList<string> arguments,
        Func<ProcessStartInfo, Process?>? start = null)
    {
        var specification = BuildWaitStart(paths, waitPid, target, arguments);
        using var process = (start ?? Process.Start)(specification) ?? throw new IOException("Не удалось запустить штатный деинсталлятор.");
        // A start waiter must remain alive while its parent is still alive.
        // Catch immediate argument/path/startup failures without running a shell.
        if (process.WaitForExit(200)) throw new IOException("Штатный деинсталлятор завершился до начала удаления. Повторите действие.");
    }

    private static void Finish(UninstallPaths paths, string nonce)
    {
        var initialReceipt = ReadReceipt(paths, nonce);
        if (initialReceipt.Data != paths.Data || initialReceipt.Music != paths.Music)
            throw new IOException("Запрос удаления создан предыдущей версией. Повторно запустите удаление Yamuse в параметрах Windows и подтвердите актуальные папки.");
        // Pinned Velopack's native waitPid logs and continues after 60 seconds.
        // Independently verify full origin process exit before touching data.
        if (!OriginProcessExited(paths, nonce)) throw new IOException("Процесс Yamuse ещё не завершился. Данные не удалены. Дождитесь закрытия приложения и повторите удаление.");
        // Hold the ordinary application mutex as well: a fresh normal launch
        // must not recreate data while the confirmed cleanup is in progress.
        using var main = new Mutex(true, BrandPaths.LegacyInstanceMutex, out var mainCreated);
        if (!mainCreated) throw new IOException("Yamuse ещё открыт. Закройте приложение и повторите удаление.");
        using var single = new Mutex(true, @"Local\YMusic.Native.FullUninstall.Cleanup." + WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) throw new IOException("Полное удаление уже выполняется.");
        var progress = new Window
        {
            Title = "Удаление Yamuse", Width = 430, Height = 160, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = "Удаляем данные и скачанную музыку…\nЭто может занять несколько минут.",
                Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
        };
        var working = true;
        progress.Closing += (_, e) => { if (working) e.Cancel = true; };
        progress.Loaded += async (_, _) =>
        {
            try
            {
                while (true)
                {
                    var result = await Task.Run(() => UninstallCleanup.Execute(paths));
                    if (result.Complete)
                    {
                        if (result.PreservedForeignFiles > 0)
                            MessageBox.Show(progress, $"Данные приложения и его неизменённые скачанные файлы удалены. Посторонних или изменённых файлов/папок сохранено: {result.PreservedForeignFiles}.\n\nПапки музыки:\n{paths.Music}" +
                                (Directory.Exists(paths.LegacyMusic) ? "\n" + paths.LegacyMusic : "") + "\n\nСейчас будет удалено приложение.",
                                "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Information);
                        var receipt = ReadReceipt(paths, nonce);
                        MarkReceiptConsumed(paths, nonce);
                        try { StartAfterExit(paths, Environment.ProcessId, paths.Updater, ["--uninstall"]); }
                        catch { WriteReceipt(paths, nonce, receipt); throw; }
                        // Keep the consumed marker until Velopack deletes the
                        // install root. It blocks new ordinary application
                        // launches during the final native handover.
                        return;
                    }
                    var details = string.Join("\n", result.Issues.Take(8).Select(issue => issue.Area + (issue.RelativePath.Length == 0 ? "" : ": " + issue.RelativePath) + " — " + issue.Reason));
                    if (MessageBox.Show(progress, "Полное удаление не завершено. Приложение пока не удалено.\n\n" + details +
                        "\n\nУже удалённые файлы восстановить нельзя. Устраните причину и нажмите «ОК» для повтора либо отмените удаление.",
                        "Удаление Yamuse", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                    { DeleteReceipt(paths, nonce); return; }
                }
            }
            catch (Exception error) { MessageBox.Show(progress, "Удаление не завершено.\n\n" + error.Message, "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { working = false; progress.Close(); }
        };
        progress.ShowDialog();
    }

    private static string ReceiptPath(UninstallPaths paths, string nonce)
    {
        if (nonce.Length != 64 || !nonce.All(Uri.IsHexDigit)) throw new IOException("Нет подтверждённого запроса удаления.");
        return Path.Combine(paths.Install, "full-uninstall.pending.json");
    }
    private static string Quote(string path) => "\"" + path + "\"";
    private sealed record Receipt(string Nonce, string Install, string Data, string Music, string User, DateTimeOffset CreatedUtc,
        int OriginPid, long OriginStartedUtcTicks, bool Consumed = false);
    private sealed class Listener(EventWaitHandle signal, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose() { registration.Unregister(null); signal.Dispose(); }
    }
}
