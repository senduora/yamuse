using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Velopack;
using Velopack.NuGet;

namespace YMusic.Native;

internal static class UninstallSupport
{
    internal const string RequestArgument = "--uninstall";
    internal const string CompleteArgument = "--complete-full-uninstall";
    private const string PendingReceiptFileName = "full-uninstall.pending.json";
    private const int MaximumReceiptPathLength = 32767;
    // Three Windows paths may each expand to six JSON bytes per UTF-16 unit.
    private const int MaximumReceiptBytes = 640 * 1024;
    private const string StartupPending = "Полное удаление Yamuse уже запрошено или не завершено.";
    private const string StartupMarkerUnavailable = "Не удалось проверить состояние удаления Yamuse. Приложение пока не откроет данные. Проверьте доступ к папке установки и повторите запуск.";
    private const string RecoveryDone = "Запрос сброшен. Теперь можно снова открыть Yamuse. Для полного удаления потребуется новое подтверждение.";
    private const string ConsumedRecovery = "Данные Yamuse уже очищены. Завершите штатное удаление приложения из параметров Windows. Если запись приложения отсутствует или установка повреждена, восстановите установку перед повтором.";
    private const string RecoveryTitle = "Восстановление запуска Yamuse";
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YMusic.Native";
    private static string RequestPipeName
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            using var identity = WindowsIdentity.GetCurrent();
            return $"YMusic.Native.FullUninstall.{process.SessionId}.{identity.User!.Value}";
        }
    }

    internal static string ConfirmationText(UninstallPaths paths) =>
        "Полностью удалить Yamuse?\n\nБудут удалены:\n" +
        "• приложение и его ярлыки;\n" +
        "• сохранённый вход, настройки, кэш и диагностика:\n" + paths.Data + "\n" +
        "• вся скачанная приложением музыка и локальные плейлисты:\n" + paths.Music +
        (UninstallPathGuard.EntryExists(paths.LegacyData) ? "\n• данные предыдущей версии:\n" + paths.LegacyData : "") +
        (UninstallPathGuard.EntryExists(paths.LegacyMusic) ? "\n• скачанная музыка предыдущей версии:\n" + paths.LegacyMusic : "") +
        "\n\nПосторонние файлы и папки, а также изменённая музыка сохранятся. Восстановить удалённое нельзя.";

    // Called by ordinary startup after taking the application mutex, without
    // requiring Update.exe/current to remain readable during native removal.
    internal static string? GetNormalStartupBlockReason()
    {
        try
        {
            return HasStartupMarker(LocalDataDirectory()) ? StartupPending : null;
        }
        catch (Exception error) when (IsUninstallFailure(error)) { return StartupMarkerUnavailable; }
    }

    // Program invokes this after releasing its startup probe mutex. A long
    // warning/confirmation must never prevent the admitted cleanup from exiting.
    internal static void ShowNormalStartupBlocked(string reason)
    {
        if (reason != StartupPending)
        {
            MessageBox.Show(reason, RecoveryTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var localData = LocalDataDirectory();
            var candidate = InspectStartupMarkerForReset(localData);
            if (candidate is null)
            {
                MessageBox.Show("Запрос уже отсутствует. Запустите Yamuse снова.", RecoveryTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (candidate.Consumed)
            {
                MessageBox.Show(ConsumedRecovery, RecoveryTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ConfirmRecovery(reason + "\n\nСбросить запрос, чтобы снова открыть приложение? Сброс затрагивает только файл запроса. Чтобы продолжить удаление, выберите «Нет» и откройте удаление Yamuse в параметрах Windows.")) return;

            // Reopen the exact fixed marker under the application mutex. If it
            // changed while the dialog was open, the previous answer is stale.
            string message;
            using (var main = App.TryAcquireProfileInstance())
            {
                message = main is not null && TryResetStartupMarker(localData, candidate)
                    ? RecoveryDone : "Состояние запроса изменилось или выполняется другая операция. Повторите запуск Yamuse.";
            }
            MessageBox.Show(message, RecoveryTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) when (IsUninstallFailure(error))
        {
            MessageBox.Show("Не удалось проверить или сбросить запрос удаления.\n\n" + error.Message,
                RecoveryTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static bool HasStartupMarker(string localDataDirectory)
        => UninstallPathGuard.EntryExists(Path.Combine(InstallFromLocalData(localDataDirectory), PendingReceiptFileName));

    private static string LocalDataDirectory() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string InstallFromLocalData(string localDataDirectory)
        => Path.Combine(UninstallPathGuard.Canonical(localDataDirectory), BrandPaths.LegacyPackageId);
    private static bool ConfirmRecovery(string message)
        => MessageBox.Show(message, RecoveryTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private static bool IsUninstallFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException
        or Win32Exception or JsonException;

    // Called before constructing App or running the Velopack startup hooks.
    // Only explicit Yamuse uninstall commands are handled here.
    internal static bool TryHandleCommand(string[] args)
    {
        if (!args.Contains(RequestArgument, StringComparer.OrdinalIgnoreCase)
            && !args.Contains(CompleteArgument, StringComparer.OrdinalIgnoreCase))
            return false;
        try
        {
            if (args.Length == 1 && args[0].Equals(RequestArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (TryForwardToOpenApplication() is { } forwarded)
                {
                    if (forwarded != ForwardedUninstall.Accepted) throw new IOException(ForwardedUninstallMessage(forwarded));
                    return true;
                }
                using var main = App.TryAcquireProfileInstance();
                if (main is null)
                    throw new IOException("Yamuse открыт. Закройте его обычной командой «Выйти» и повторите удаление.");
                var localData = LocalDataDirectory();
                var install = InstallFromLocalData(localData);
                RequireUpdaterFinished(install);
                if (IsConsumedMarkerAtInstall(install))
                {
                    ConfirmConsumedRetry(localData);
                    return true;
                }
                UninstallPaths paths;
                try { paths = CurrentInstalledPaths(); }
                catch (Exception error) when (IsUninstallFailure(error))
                {
                    var recovery = RecoverWithoutInstallation(localData, () => ConfirmRecovery(
                        "Не удалось подготовить штатное удаление Yamuse.\n\nСбросить сохранённый запрос, чтобы снова открыть приложение? Сброс затрагивает только файл запроса."));
                    if (recovery == PendingReceiptState.Normal) throw;
                    if (recovery == PendingReceiptState.Consumed) ConfirmConsumedRetry(localData);
                    if (recovery == PendingReceiptState.Recovered) ShowRecoveryDone();
                    return true;
                }
                var receiptState = InspectPendingReceipt(paths, () => ConfirmRecovery(
                    "Сохранённый запрос полного удаления Yamuse повреждён или относится к прежним папкам.\n\nСбросить запрос, чтобы снова открыть приложение? Сброс затрагивает только файл запроса."));
                if (receiptState == PendingReceiptState.Recovered)
                    ShowRecoveryDone();
                if (receiptState is PendingReceiptState.Recovered or PendingReceiptState.RecoveryDeclined) return true;
                if (receiptState == PendingReceiptState.Consumed)
                {
                    ConfirmConsumedRetry(paths.LocalData);
                    return true;
                }
                var documents = CaptureDocumentsIdentity(paths);
                if (MessageBox.Show(ConfirmationText(paths), "Полное удаление Yamuse", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    // Cancelling a stale request only removes our own marker.
                    // No account, settings, cache or audio path is touched.
                    CancelPendingReceipt(paths);
                    return true;
                }
                RequireUpdaterFinished(paths.Install);
                var nonce = CreateReceipt(paths, documents);
                try { StartAfterExit(paths, Environment.ProcessId, paths.Executable, [CompleteArgument, nonce]); }
                catch { DeleteReceipt(paths, nonce); throw; }
                return true;
            }
            if (args.Length == 2 && args[0].Equals(CompleteArgument, StringComparison.OrdinalIgnoreCase))
            {
                Finish(args[1]);
                return true;
            }
            throw new IOException("Некорректная команда удаления. Запустите удаление из настроек Windows или Yamuse.");
        }
        catch (Exception error) when (IsUninstallFailure(error))
        {
            MessageBox.Show("Удаление не завершено.\n\n" + error.Message, "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        }
    }
    private static void ShowRecoveryDone() => MessageBox.Show(RecoveryDone, RecoveryTitle, MessageBoxButton.OK, MessageBoxImage.Information);

    internal static void RequireUpdaterFinished(string install, Func<IReadOnlyList<string>, uint, bool>? wait = null)
    {
        // A normal start waiter launches the cleanup process and promptly exits.
        // An updater still applying a package must finish before either stage opens data.
        if (!(wait ?? Program.WaitForUpdaterExit)([Path.Combine(install, "Update.exe")], 5000))
            throw new IOException("Дождитесь завершения обновления Yamuse и повторите удаление.");
    }

    private static void ConfirmConsumedRetry(string localDataDirectory)
    {
        try
        {
            ScheduleConsumedRetry(localDataDirectory, Environment.ProcessPath!, Environment.ProcessId, () =>
                MessageBox.Show("Данные Yamuse уже очищены. Завершить штатное удаление приложения?\n\nПри отказе запрос останется сохранённым.",
                    "Завершение удаления Yamuse", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        }
        catch (Exception error) when (IsUninstallFailure(error))
        { throw new IOException(error.Message + "\n\n" + ConsumedRecovery, error); }
    }

    internal static UninstallPaths CurrentInstalledPaths()
    {
        UninstallPaths paths;
        try { paths = UninstallPaths.ForCurrentUser(); }
        catch (IOException error) { throw new IOException("Не удалось определить локальную папку «Документы» для полного удаления Yamuse.", error); }
        VerifyInstalledFiles(paths.Install, Environment.ProcessPath!);
        return paths;
    }

    internal static void ValidateInstallation(string localDataDirectory, string executable)
        => VerifyInstalledFiles(InstallFromLocalData(localDataDirectory), executable);

    internal static void ValidateCurrentInstallationImage()
        => ValidateInstallationImage(LocalDataDirectory(), Environment.ProcessPath!);

    internal static void ValidateInstallationImage(string localDataDirectory, string executable,
        Func<string, FileAttributes>? readCurrentAttributes = null)
        => VerifyInstalledImage(InstallFromLocalData(localDataDirectory), executable, readCurrentAttributes: readCurrentAttributes);

    internal static void ValidateInstalledUpdateFiles(string install, string executable, string content,
        string? updater, SemanticVersion version)
    {
        VerifyInstalledImage(install, executable, current =>
        {
            if (!content.Equals(current, StringComparison.OrdinalIgnoreCase)
                && !content.Equals(Path.Combine(install, "current"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Папка обновления не соответствует установленному Yamuse.");
            using var updaterParents = updater is null ? null : UninstallPathGuard.HoldExistingAncestors(updater);
            using var update = updater is null ? null : UninstallPathGuard.OpenOwnedFile(updater, read: true, deleteAccess: false);
            using var manifest = OpenInstalledManifest(current);
            if (!version.Equals(ValidateInstalledManifest(manifest)))
                throw new IOException("Версия установленного Yamuse изменилась.");
        });
    }

    internal static bool HasInstalledUpdateLayout(string executable, bool requireManifest = true, string? localData = null,
        Func<string, FileAttributes>? readCurrentAttributes = null)
    {
        try
        {
            var image = Program.NormalizeImagePath(executable);
            if (Path.GetDirectoryName(image) is not { } current
                || !Path.GetFileName(current).Equals("current", StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(current) is not { } install) return false;
            if (requireManifest) VerifyInstalledFiles(install, image, requireNativeHandoff: false);
            else
            {
                var fixedInstall = !string.IsNullOrEmpty(localData)
                    && install.Equals(Program.NormalizeImagePath(InstallFromLocalData(localData)), StringComparison.OrdinalIgnoreCase);
                VerifyInstalledImage(install, image, content =>
                {
                    try
                    {
                        using var updater = UninstallPathGuard.OpenOwnedFile(Path.Combine(install, "Update.exe"), read: false, deleteAccess: false);
                    }
                    catch (Exception error) when (IsUnavailableInstallEvidence(error))
                    {
                        // The exact installed image and manifest can identify
                        // the candidate while the updater is unavailable.
                        try
                        {
                            using var file = OpenInstalledManifest(content);
                            ValidateInstalledManifest(file);
                        }
                        catch (Exception manifestError) when (fixedInstall && IsUnavailableInstallEvidence(manifestError))
                        {
                            // Only the fixed installation keeps a retry when
                            // neither companion file can be inspected.
                        }
                    }
                }, allowUnavailableImage: fixedInstall, readCurrentAttributes: readCurrentAttributes);
            }
            return true;
        }
        catch (Exception error) when (IsUninstallFailure(error) || error is ArgumentException) { return false; }
    }

    internal static bool IsUnavailableInstallEvidence(Exception error)
        => error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
            || NativeIoErrorCode(error) is 2 or 3 or 5 or 32 or 33;

    private static int NativeIoErrorCode(Exception error)
    {
        if (error is Win32Exception native) return native.NativeErrorCode;
        if (error is not IOException) return 0;
        var code = error.HResult;
        return unchecked((uint)code) >> 16 == 0x8007 ? code & 0xffff : code;
    }

    // Starting a listener needs a held image. Only startup of the fixed
    // installation can defer a transient failure until the next update check.
    private static void VerifyInstalledImage(string install, string executable, Action<string>? onValidated = null,
        bool allowUnavailableImage = false, Func<string, FileAttributes>? readCurrentAttributes = null)
    {
        var processPath = UninstallPathGuard.Canonical(Program.NormalizeImagePath(executable));
        if (!UninstallPathGuard.Contains(install, processPath)
            || !Path.GetFileName(processPath).Equals("YMusic.Native.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Путь исполняемого файла не соответствует установленному Yamuse.");
        var expectedDirectory = Path.Combine(install, "current");
        var exactLauncher = processPath.Equals(Path.Combine(expectedDirectory, "YMusic.Native.exe"), StringComparison.OrdinalIgnoreCase);
        UninstallPathGuard.AncestorLease ancestors;
        try { ancestors = UninstallPathGuard.HoldExistingAncestors(install); }
        catch (Exception error) when (allowUnavailableImage && exactLauncher && IsUnavailableInstallEvidence(error)
            && CanRetryUnavailableInstallRoot(install, expectedDirectory)) { return; }
        using var heldAncestors = ancestors;
        SafeFileHandle root;
        try { root = UninstallPathGuard.HoldDirectory(install); }
        catch (Exception error) when (allowUnavailableImage && exactLauncher && IsUnavailableInstallEvidence(error)
            && CanRetryUnavailableInstallRoot(install, expectedDirectory)) { return; }
        using var heldRoot = root;
        if (UninstallPathGuard.EntryExists(Path.Combine(install, ".portable")))
            throw new IOException("Полное удаление доступно для установленного Yamuse.");
        string current;
        try { current = ResolvedContent(install, expectedDirectory, readCurrentAttributes); }
        catch (Exception error) when (allowUnavailableImage && exactLauncher && IsUnavailableInstallEvidence(error)
            && UninstallPathGuard.CanInspectOrdinaryDirectory(expectedDirectory)) { return; }
        var applicationPath = Path.Combine(current, "YMusic.Native.exe");
        if (!processPath.Equals(applicationPath, StringComparison.OrdinalIgnoreCase)
            && !exactLauncher)
            throw new IOException("Путь исполняемого файла не соответствует установленному Yamuse.");
        UninstallPathGuard.AncestorLease currentParents;
        try { currentParents = UninstallPathGuard.HoldExistingAncestors(applicationPath); }
        catch (Exception error) when (allowUnavailableImage && exactLauncher && IsUnavailableInstallEvidence(error)) { return; }
        using var heldParents = currentParents;
        SafeFileHandle application;
        try { application = UninstallPathGuard.OpenOwnedFile(applicationPath, read: true, deleteAccess: false); }
        catch (Exception error) when (allowUnavailableImage && IsUnavailableInstallEvidence(error)) { return; }
        using (application) onValidated?.Invoke(current);
    }

    private static bool CanRetryUnavailableInstallRoot(string install, string expectedDirectory)
    {
        if (!OrdinaryDirectories(install) || UninstallPathGuard.EntryExists(Path.Combine(install, ".portable"))) return false;
        var current = ResolvedContent(install, expectedDirectory);
        return OrdinaryDirectories(current);

        static bool OrdinaryDirectories(string path)
        {
            const FileAttributes kind = FileAttributes.Directory | FileAttributes.ReparsePoint;
            // Attribute reads only preserve the chance to retry. Walk from
            // the drive root so a detected link never directs a child probe.
            var ancestors = new Stack<string>();
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                ancestors.Push(directory.FullName);
            return ancestors.All(directory => (File.GetAttributes(directory) & kind) == FileAttributes.Directory);
        }
    }

    // The SDK locator also initializes the cache. Registration and uninstall
    // need held, read-only evidence from the fixed local installation.
    private static void VerifyInstalledFiles(string install, string executable, Action? onValidated = null, bool requireNativeHandoff = true,
        Func<string, FileAttributes>? readCurrentAttributes = null)
    {
        if (requireNativeHandoff) EnsureNativeInstallPath(install);
        VerifyInstalledImage(install, executable, current =>
        {
            if (requireNativeHandoff && (Directory.EnumerateDirectories(install, "app-*").Any() || HasLegacyStaging(install)))
                throw new IOException("Формат установки Yamuse требует обновления. Полное удаление не начато.");
            // Require real read sharing through the handoff. Reading only the
            // attributes would allow simultaneous changes to the updater bytes.
            using var update = UninstallPathGuard.OpenOwnedFile(Path.Combine(install, "Update.exe"), read: true, deleteAccess: false);
            using var manifestFile = OpenInstalledManifest(current);
            ValidateInstalledManifest(manifestFile);
            onValidated?.Invoke();
        }, readCurrentAttributes: readCurrentAttributes);
    }

    private static FileStream OpenInstalledManifest(string content)
    {
        var handle = UninstallPathGuard.OpenOwnedFile(Path.Combine(content, "sq.version"), read: true, deleteAccess: false);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    private static SemanticVersion ValidateInstalledManifest(FileStream file)
    {
        if (file.Length is <= 0 or > 65536)
            throw new IOException("Манифест установленного Yamuse повреждён.");
        InstalledManifest manifest;
        try { manifest = new InstalledManifest(file); }
        catch (Exception error) when (error is InvalidDataException or XmlException or FormatException or ArgumentException)
        { throw new IOException("Манифест установленного Yamuse повреждён.", error); }
        if (manifest.Id != BrandPaths.LegacyPackageId || manifest.Version is not { } version)
            throw new IOException("Манифест не соответствует установленному Yamuse.");
        return version;
    }

    private static bool HasLegacyStaging(string install)
    {
        var staging = Path.Combine(install, "staging");
        if (!UninstallPathGuard.EntryExists(staging)) return false;
        using var directory = UninstallPathGuard.HoldDirectory(staging);
        return Directory.EnumerateDirectories(staging, "app-*").Any();
    }

    private sealed class InstalledManifest : PackageManifest
    {
        internal InstalledManifest(Stream file) => ReadManifest(file);
    }

    private static void EnsureNativeInstallPath(string install)
    {
        // The pinned native updater uses shell expansion during self-removal.
        if (install.Contains('%'))
            throw new IOException("Штатное удаление Yamuse недоступно, если путь установки содержит знак «%». Обратитесь в поддержку, чтобы завершить удаление.");
    }

    private static string ResolvedContent(string install, string directory, Func<string, FileAttributes>? readAttributes = null)
    {
        var path = UninstallPathGuard.Canonical(directory);
        if (!UninstallPathGuard.Contains(install, path) || path.Equals(install, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Папка приложения находится за пределами установки.");
        // Velopack may use a current junction to another version in the same
        // package root. Only that one junction is allowed, and its final target
        // must remain inside this exact root with no further redirected parent.
        if (((readAttributes ?? File.GetAttributes)(path) & FileAttributes.ReparsePoint) != 0)
        {
            if (!path.Equals(Path.Combine(install, "current"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Неожиданная ссылка в установленном приложении.");
            path = UninstallPathGuard.Canonical(new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("Не удалось проверить папку установленного приложения."));
        }
        if (!UninstallPathGuard.Contains(install, path) || path.Equals(install, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ссылка установленного приложения ведёт за пределы установки.");
        return path;
    }

    // This changes only the two commands in the existing, exact package key.
    // No key is created and unrelated/custom registry values are untouched.
    internal static bool RegisterWindowsUninstall()
        => CheckWindowsUninstallRegistration() == WindowsUninstallRegistration.Registered;

    internal static bool RegisterWindowsUninstall(Func<string> localDataDirectory, string executable,
        Func<string, string, string, bool> updateExistingKey)
        => CheckWindowsUninstallRegistration(localDataDirectory, executable, updateExistingKey) == WindowsUninstallRegistration.Registered;

    internal enum WindowsUninstallRegistration { Inapplicable, Retryable, Registered }

    internal static WindowsUninstallRegistration CheckWindowsUninstallRegistration()
        => CheckWindowsUninstallRegistration(LocalDataDirectory, Environment.ProcessPath!, RegisterWindowsUninstallKey);

    internal static WindowsUninstallRegistration CheckWindowsUninstallRegistration(Func<string> localDataDirectory, string executable,
        Func<string, string, string, bool> updateExistingKey, Func<string, FileAttributes>? readCurrentAttributes = null)
    {
        var verified = false;
        try
        {
            var install = InstallFromLocalData(localDataDirectory());
            var native = Quote(Path.Combine(install, "Update.exe")) + " --uninstall";
            var wrapper = Quote(Path.Combine(install, "current", "YMusic.Native.exe")) + " " + RequestArgument;
            var registered = false;
            VerifyInstalledFiles(install, executable, () =>
            {
                verified = true;
                registered = updateExistingKey(install, native, wrapper);
            }, requireNativeHandoff: false, readCurrentAttributes: readCurrentAttributes);
            return registered ? WindowsUninstallRegistration.Registered : WindowsUninstallRegistration.Inapplicable;
        }
        catch (Exception error) when (!verified && IsUnavailableInstallEvidence(error))
        { return WindowsUninstallRegistration.Retryable; }
        catch (Exception error) when (verified && IsUnavailableRegistryRegistration(error))
        { return WindowsUninstallRegistration.Retryable; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or SecurityException)
        { return WindowsUninstallRegistration.Inapplicable; }
    }

    private static bool IsUnavailableRegistryRegistration(Exception error)
        => error is UnauthorizedAccessException or SecurityException or RegistryRepairIncompleteException
            || NativeIoErrorCode(error) is 2 or 5 or 6 or 32 or 33 or 1018;

    private static bool RegisterWindowsUninstallKey(string install, string native, string wrapper)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        if (key is null) return false;
        return RepairWindowsUninstallCommands(install, native, wrapper, name =>
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            // GetValue also returns the default for unsupported types.
            if (value is null && !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return default;
            var kind = key.GetValueKind(name);
            if (value is null && kind == RegistryValueKind.String)
                throw new RegistryRepairIncompleteException();
            return (kind, value);
        }, (name, value) => key.SetValue(name, value, RegistryValueKind.String),
            name => key.DeleteValue(name, throwOnMissingValue: false));
    }

    internal static bool RepairWindowsUninstallCommands(string install, string native, string wrapper,
        Func<string, (RegistryValueKind? Kind, object? Value)> read, Action<string, string> writeString, Action<string> delete)
    {
        bool KnownQuiet((RegistryValueKind? Kind, object? Value) value)
            => value == default || value is (RegistryValueKind.String, string quietCommand) && quietCommand == native + " --silent";

        if (read("InstallLocation") is not (RegistryValueKind.String, string location)
            || !UninstallPathGuard.Canonical(location).Equals(install, StringComparison.OrdinalIgnoreCase)) return false;
        if (read("UninstallString") is not (RegistryValueKind.String, string command) || command != native && command != wrapper) return false;
        var quietValue = read("QuietUninstallString");
        if (!KnownQuiet(quietValue)) return false;
        if (command != wrapper) writeString("UninstallString", wrapper);
        if (quietValue != default)
        {
            quietValue = read("QuietUninstallString");
            if (!KnownQuiet(quietValue)) return false;
            if (quietValue != default) delete("QuietUninstallString");
        }
        if (read("UninstallString") is not (RegistryValueKind.String, string written) || written != native && written != wrapper)
            return false;
        var remainingQuiet = read("QuietUninstallString");
        if (!KnownQuiet(remainingQuiet)) return false;
        if (written == wrapper && remainingQuiet == default) return true;
        // RegistryKey.DeleteValue can return without surfacing a native error.
        // A known partial registration is still safe to recheck later.
        throw new RegistryRepairIncompleteException();
    }

    private sealed class RegistryRepairIncompleteException() : IOException("Не удалось подтвердить регистрацию удаления Yamuse.");

    internal enum ForwardedUninstall : byte { Accepted = 1, Maintenance, Busy, Closing }

    internal static string ForwardedUninstallMessage(ForwardedUninstall result) => result switch
    {
        ForwardedUninstall.Maintenance => "Дождитесь завершения очистки музыки или кэша и повторите удаление.",
        ForwardedUninstall.Busy => "Yamuse уже обрабатывает запрос на удаление. Переключитесь на его окно или повторите действие позже.",
        _ => "Открытое окно Yamuse не приняло запрос на удаление. Дождитесь закрытия окна или повторите действие."
    };

    internal static IDisposable Listen(Window owner, Action<Action<ForwardedUninstall>> request,
        string? pipeName = null, TimeSpan? dispatchTimeout = null,
        Func<NamedPipeServerStream, CancellationToken, Task>? waitForConnection = null)
        => new Listener(owner.Dispatcher, request, pipeName ?? RequestPipeName, dispatchTimeout ?? TimeSpan.FromSeconds(2),
            waitForConnection ?? ((pipe, token) => pipe.WaitForConnectionAsync(token)));

    internal static ForwardedUninstall? TryForwardToOpenApplication(string? pipeName = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName ?? RequestPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { pipe.Connect(400); }
        catch (TimeoutException) { return null; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = new byte[1];
        try
        {
            if (pipe.ReadAsync(reply.AsMemory(), deadline.Token).AsTask().GetAwaiter().GetResult() == 1
                && Enum.IsDefined((ForwardedUninstall)reply[0])) return (ForwardedUninstall)reply[0];
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        return ForwardedUninstall.Closing;
    }

    internal static UninstallFileIdentity CaptureDocumentsIdentity(UninstallPaths paths, Func<SafeFileHandle, UninstallFileIdentity>? identify = null)
    {
        try
        {
            using var parents = paths.HoldDocumentsAncestors();
            using var documents = UninstallPathGuard.HoldDirectory(paths.Documents);
            return UninstallPathGuard.RequireDocumentsIdentity(documents, identify);
        }
        catch (Exception error) when (error is not DocumentsIdentityNotSupportedException && IsUninstallFailure(error))
        { throw new IOException("Не удалось проверить папку «Документы» для полного удаления Yamuse.", error); }
    }

    internal static string CreateReceipt(UninstallPaths paths, UninstallFileIdentity? confirmedDocuments = null)
    {
        EnsureNativeInstallPath(paths.Install);
        if (paths.Install.Length > MaximumReceiptPathLength || paths.Data.Length > MaximumReceiptPathLength
            || paths.Music.Length > MaximumReceiptPathLength)
            throw new IOException("Пути слишком длинные для записи запроса полного удаления Yamuse.");
        using var documentsParents = paths.HoldDocumentsAncestors();
        using var documents = UninstallPathGuard.HoldDirectory(paths.Documents);
        var documentsIdentity = UninstallPathGuard.RequireDocumentsIdentity(documents);
        if (confirmedDocuments is not null && documentsIdentity != confirmedDocuments)
            throw new IOException("Папка «Документы» изменилась. Подтвердите полное удаление заново.");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(Path.Combine(paths.Install, "receipt"));
        using var origin = Process.GetCurrentProcess();
        var receipt = new Receipt(nonce, paths.Install, paths.Data, paths.Music, WindowsIdentity.GetCurrent().User!.Value,
            DateTimeOffset.UtcNow, origin.Id, origin.StartTime.ToUniversalTime().Ticks, DocumentsIdentity: documentsIdentity);
        var encoded = EncodeReceipt(receipt);
        if (!CancelPendingReceipt(paths)) throw new IOException("Очистка уже выполнена. Завершите штатное удаление Yamuse через параметры Windows.");
        WriteReceipt(paths, nonce, encoded, overwrite: false);
        return nonce;
    }

    internal static void ValidateReceipt(UninstallPaths paths, string nonce)
        => _ = ReadReceipt(paths, nonce, requireFresh: true);

    private static Receipt ReadReceipt(UninstallPaths paths, string nonce, bool requireFresh)
    {
        _ = ReceiptPath(paths, nonce);
        var receipt = ReadAnyReceipt(paths);
        var now = DateTimeOffset.UtcNow;
        if (receipt.Consumed || receipt.Nonce != nonce
            || requireFresh && (receipt.CreatedUtc < now.AddMinutes(-30) || receipt.CreatedUtc > now.AddMinutes(1)))
            throw new IOException("Подтверждение удаления истекло или уже использовано. Повторите удаление из настроек Windows.");
        if (requireFresh && receipt.DocumentsIdentity is null)
            throw new IOException("Запрос не содержит идентификатор папки «Документы». Подтвердите полное удаление заново.");
        return receipt;
    }

    private static Receipt ReadAnyReceipt(UninstallPaths paths)
    {
        using var marker = OpenPendingReceipt(paths.Install, deleteAccess: false);
        if (marker is not null && ReadMarkerReceipt(marker.File, paths.Install) is { } receipt && MatchesReceiptScope(paths, receipt))
            return receipt;
        throw new IOException("Запрос удаления отсутствует, повреждён или папки изменились. Откройте удаление Yamuse в параметрах Windows.");
    }

    private static Receipt? ReadMarkerReceipt(FileStream file, string install)
    {
        if (file.Length > MaximumReceiptBytes) return null;
        Receipt? receipt;
        try { receipt = JsonSerializer.Deserialize<Receipt>(file); }
        catch (JsonException) { return null; }
        if (receipt is null || receipt.Nonce is not { Length: 64 } nonce || !nonce.All(Uri.IsHexDigit)
            || receipt.OriginPid <= 0 || receipt.OriginStartedUtcTicks <= 0 || receipt.CreatedUtc == default
            || string.IsNullOrWhiteSpace(receipt.Data) || string.IsNullOrWhiteSpace(receipt.Music)
            || receipt.Install != install || receipt.User != WindowsIdentity.GetCurrent().User!.Value) return null;
        return receipt;
    }

    private static bool MatchesReceiptScope(UninstallPaths paths, Receipt receipt)
    {
        // Consumed receipts only permit the native uninstall retry, which has no data paths.
        return receipt.Consumed || MatchesReceiptDataPaths(paths, receipt.Data, receipt.Music);
    }

    internal enum PendingReceiptState { Normal, Consumed, RecoveryDeclined, Recovered }

    // The Windows uninstall entry holds the application mutex across this
    // decision. Keep the exact marker and its parents pinned through consent.
    internal static PendingReceiptState InspectPendingReceipt(UninstallPaths paths, Func<bool> confirmRecovery)
        => InspectPendingReceipt(paths.Install, paths, confirmRecovery);

    internal static PendingReceiptState RecoverWithoutInstallation(string localDataDirectory, Func<bool> confirmRecovery)
        => InspectPendingReceipt(InstallFromLocalData(localDataDirectory), null, confirmRecovery);

    private static PendingReceiptState InspectPendingReceipt(string install, UninstallPaths? paths, Func<bool> confirmRecovery)
    {
        using var marker = OpenPendingReceipt(install, deleteAccess: true);
        if (marker is null) return PendingReceiptState.Normal;
        var receipt = ReadMarkerReceipt(marker.File, install);
        if (receipt?.Consumed == true) return PendingReceiptState.Consumed;
        if (receipt is not null && paths is not null && MatchesReceiptScope(paths, receipt)) return PendingReceiptState.Normal;
        if (!confirmRecovery()) return PendingReceiptState.RecoveryDeclined;
        ArchivePendingReceipt(install, marker.File);
        return PendingReceiptState.Recovered;
    }

    internal sealed record StartupMarkerReset(bool Consumed, ulong Volume, ulong IdLow, ulong IdHigh, long Length, DateTime LastWriteUtc);

    internal static StartupMarkerReset? InspectStartupMarkerForReset(string localDataDirectory)
    {
        var install = InstallFromLocalData(localDataDirectory);
        using var marker = OpenPendingReceipt(install, deleteAccess: false);
        if (marker is null) return null;
        return SnapshotMarker(marker.File, ReadMarkerReceipt(marker.File, install)?.Consumed == true);
    }

    // Called under the application mutex after the startup recovery dialog.
    internal static bool TryResetStartupMarker(string localDataDirectory, StartupMarkerReset candidate)
    {
        var install = InstallFromLocalData(localDataDirectory);
        using var marker = OpenPendingReceipt(install, deleteAccess: true);
        if (marker is null || candidate.Consumed || ReadMarkerReceipt(marker.File, install)?.Consumed == true
            || SnapshotMarker(marker.File, consumed: false) != candidate) return false;
        ArchivePendingReceipt(install, marker.File);
        return true;
    }

    private static StartupMarkerReset SnapshotMarker(FileStream file, bool consumed)
    {
        var handle = file.SafeFileHandle;
        var id = UninstallPathGuard.GetStableFileIdentity(handle);
        return new(consumed, id.Volume, id.Low, id.High, file.Length, File.GetLastWriteTimeUtc(handle));
    }

    private static void ArchivePendingReceipt(string install, FileStream file)
    {
        var archived = Path.Combine(install, "full-uninstall.archived-" + Guid.NewGuid().ToString("N") + ".json");
        ManagedCacheCleanup.RenameByHandle(file.SafeFileHandle, archived);
    }

    private static PendingReceiptFile? OpenPendingReceipt(string install, bool deleteAccess)
    {
        var path = Path.Combine(install, PendingReceiptFileName);
        var ancestors = UninstallPathGuard.HoldAncestors(path);
        try
        {
            if (ancestors.Complete)
            {
                var handle = UninstallPathGuard.OpenOwnedFile(path, read: true, deleteAccess: deleteAccess);
                try { return new PendingReceiptFile(new FileStream(handle, FileAccess.Read), ancestors); }
                catch { handle.Dispose(); throw; }
            }
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { }
        catch { ancestors.Dispose(); throw; }
        ancestors.Dispose();
        return null;
    }

    // A pending request from the previous release may be inspected/cancelled
    // after update. It never authorizes cleanup of the new branded locations.
    private static bool MatchesReceiptDataPaths(UninstallPaths paths, string data, string music) =>
        data == paths.Data && music == paths.Music || data == paths.LegacyData && music == paths.LegacyMusic;

    internal static void DeleteReceipt(UninstallPaths paths, string nonce)
    {
        // Never remove a newer request when an older waiter fails later.
        if (!HasPendingReceipt(paths)) return;
        _ = ReadReceipt(paths, nonce, requireFresh: false);
        CancelPendingReceipt(paths);
    }

    internal static bool HasPendingReceipt(UninstallPaths paths)
        => UninstallPathGuard.EntryExists(Path.Combine(paths.Install, PendingReceiptFileName));

    internal static bool IsReceiptConsumed(UninstallPaths paths)
        => IsConsumedMarkerAtInstall(paths.Install);

    private static bool IsConsumedMarkerAtInstall(string install)
    {
        using var marker = OpenPendingReceipt(install, deleteAccess: false);
        return marker is not null && ReadMarkerReceipt(marker.File, install)?.Consumed == true;
    }

    internal static bool OriginProcessExited(UninstallPaths paths, string nonce)
        => OriginProcessExited(ReadReceipt(paths, nonce, requireFresh: true));

    private static bool OriginProcessExited(Receipt receipt)
    {
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
        var path = Path.Combine(paths.Install, PendingReceiptFileName);
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(path);
        if (!UninstallPathGuard.EntryExists(path)) return true;
        if (ReadAnyReceipt(paths).Consumed) return false;
        using var file = UninstallPathGuard.OpenOwnedFile(path, read: false);
        UninstallPathGuard.DeleteByHandle(file);
        return true;
    }

    // This command only needs LocalAppData. The consumed receipt attests that
    // the cleanup already ran, and the checked handle pins it through consent.
    internal static void ScheduleConsumedRetry(string localDataDirectory, string executable, int waitPid,
        Func<bool> confirm, Func<ProcessStartInfo, Process?>? start = null)
    {
        var install = InstallFromLocalData(localDataDirectory);
        using var marker = OpenPendingReceipt(install, deleteAccess: false);
        if (marker is null || ReadMarkerReceipt(marker.File, install)?.Consumed != true)
            throw new IOException("Очистка данных ещё не завершена. Повторите полное удаление из настроек Windows.");
        VerifyInstalledFiles(install, executable);
        if (!confirm()) return;
        StartAfterExit(install, executable, waitPid, Path.Combine(install, "Update.exe"), ["--uninstall"], start);
    }

    // Only called by Finish after cleanup under both mutexes. The age check
    // already ran at admission and must not expire during cleanup or its dialogs.
    internal static void ScheduleNativeHandoffAfterCleanup(UninstallPaths paths, string nonce, int waitPid,
        Func<ProcessStartInfo, Process?>? start = null, Action? onCleanupRecorded = null)
    {
        EnsureNativeInstallPath(paths.Install);
        var receipt = ReadReceipt(paths, nonce, requireFresh: false);
        WriteReceipt(paths, nonce, EncodeReceipt(receipt with { Consumed = true }), overwrite: true);
        // Cleanup is complete even if the installed updater cannot start. Keep
        // the marker so a second request only retries the native stage.
        onCleanupRecorded?.Invoke();
        StartAfterExit(paths, waitPid, paths.Updater, ["--uninstall"], start);
    }

    private static byte[] EncodeReceipt(Receipt receipt)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(receipt);
        if (encoded.Length > MaximumReceiptBytes)
            throw new IOException("Запрос полного удаления Yamuse слишком велик.");
        return encoded;
    }

    private static void WriteReceipt(UninstallPaths paths, string nonce, byte[] receipt, bool overwrite)
    {
        using var ancestors = UninstallPathGuard.HoldExistingAncestors(ReceiptPath(paths, nonce));
        var temporary = Path.Combine(paths.Install, "full-uninstall-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(receipt); file.Flush(flushToDisk: true); }
            File.Move(temporary, ReceiptPath(paths, nonce), overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static ProcessStartInfo BuildWaitStart(UninstallPaths paths, int waitPid, string target, IReadOnlyList<string> arguments)
        => BuildWaitStart(paths.Install, waitPid, target, arguments);

    private static ProcessStartInfo BuildWaitStart(string install, int waitPid, string target, IReadOnlyList<string> arguments)
    {
        EnsureNativeInstallPath(install);
        var updater = Path.Combine(install, "Update.exe");
        var application = Path.Combine(install, "current", "YMusic.Native.exe");
        if (waitPid <= 0 || target != application && target != updater) throw new IOException("Некорректный этап удаления.");
        var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install };
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
        => StartAfterExit(paths.Install, paths.Executable, waitPid, target, arguments, start);

    private static void StartAfterExit(string install, string executable, int waitPid, string target, IReadOnlyList<string> arguments,
        Func<ProcessStartInfo, Process?>? start)
    {
        var specification = BuildWaitStart(install, waitPid, target, arguments);
        VerifyInstalledFiles(install, executable, () =>
        {
            using var process = (start ?? Process.Start)(specification) ?? throw new IOException("Не удалось запустить штатный деинсталлятор.");
            // A start waiter must remain alive while its parent is still alive.
            // Catch immediate argument/path/startup failures without running a shell.
            if (process.WaitForExit(200)) throw new IOException("Штатный деинсталлятор завершился до начала удаления. Повторите действие.");
        });
    }

    private static void Finish(string nonce)
    {
        // Hold the ordinary application mutex as well: a fresh normal launch
        // must not recreate data while the confirmed cleanup is in progress.
        using var main = App.TryAcquireProfileInstance();
        if (main is null) throw new IOException("Yamuse ещё открыт. Закройте приложение и повторите удаление.");
        RequireUpdaterFinished(InstallFromLocalData(LocalDataDirectory()));
        var paths = CurrentInstalledPaths();
        using var single = new Mutex(true, @"Local\YMusic.Native.FullUninstall.Cleanup." + WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) throw new IOException("Полное удаление уже выполняется.");
        var initialReceipt = ReadReceipt(paths, nonce, requireFresh: true);
        if (initialReceipt.Data != paths.Data || initialReceipt.Music != paths.Music)
            throw new IOException("Запрос удаления создан предыдущей версией. Повторно запустите удаление Yamuse в параметрах Windows и подтвердите актуальные папки.");
        // Pinned Velopack's native waitPid logs and continues after 60 seconds.
        // Independently verify full origin process exit before touching data.
        if (!OriginProcessExited(initialReceipt)) throw new IOException("Процесс Yamuse ещё не завершился. Данные не удалены. Дождитесь закрытия приложения и повторите удаление.");
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
                    var result = await Task.Run(() => UninstallCleanup.Execute(paths, initialReceipt.DocumentsIdentity));
                    if (result.Complete)
                    {
                        ScheduleNativeHandoffAfterCleanup(paths, nonce, Environment.ProcessId, onCleanupRecorded: () =>
                        {
                            if (result.PreservedForeignFiles == 0) return;
                            var remaining = new[] { paths.Data, paths.LegacyData, paths.Music, paths.LegacyMusic }.Where(Directory.Exists);
                            MessageBox.Show(progress, $"Посторонних или изменённых файлов и папок сохранено: {result.PreservedForeignFiles}.\n\nПапки с сохранёнными данными:\n" +
                                string.Join("\n", remaining) + "\n\nДалее Yamuse передаст удаление приложения штатному деинсталлятору.",
                                "Удаление Yamuse", MessageBoxButton.OK, MessageBoxImage.Information);
                        });
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
        return Path.Combine(paths.Install, PendingReceiptFileName);
    }
    private static string Quote(string path) => "\"" + path + "\"";
    private sealed record Receipt(string Nonce, string Install, string Data, string Music, string User, DateTimeOffset CreatedUtc,
        int OriginPid, long OriginStartedUtcTicks, bool Consumed = false, UninstallFileIdentity? DocumentsIdentity = null);
    private sealed class PendingReceiptFile(FileStream file, UninstallPathGuard.AncestorLease ancestors) : IDisposable
    {
        internal FileStream File => file;
        public void Dispose() { try { file.Dispose(); } finally { ancestors.Dispose(); } }
    }
    private sealed class Listener : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _server;
        private int _disposed;

        internal Listener(Dispatcher dispatcher, Action<Action<ForwardedUninstall>> request, string pipeName, TimeSpan dispatchTimeout,
            Func<NamedPipeServerStream, CancellationToken, Task> waitForConnection)
        {
            _server = ServeAsync(CreatePipe(pipeName), pipeName, dispatcher, request, dispatchTimeout, waitForConnection, _lifetime.Token);
        }

        private static NamedPipeServerStream CreatePipe(string pipeName) => new(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private static async Task ServeAsync(NamedPipeServerStream pipe, string pipeName, Dispatcher dispatcher,
            Action<Action<ForwardedUninstall>> request, TimeSpan dispatchTimeout,
            Func<NamedPipeServerStream, CancellationToken, Task> waitForConnection, CancellationToken lifetime)
        {
            const int noData = unchecked((int)0x800700E8);
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var connected = false;
                    try
                    {
                        await waitForConnection(pipe, lifetime).ConfigureAwait(false);
                        connected = true;
                        using var admission = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                        admission.CancelAfter(dispatchTimeout);
                        try
                        {
                            await dispatcher.InvokeAsync(() => request(result => pipe.WriteByte((byte)result)),
                                DispatcherPriority.Normal, admission.Token).Task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                        { pipe.WriteByte((byte)ForwardedUninstall.Closing); }
                        // Disconnect discards buffered replies. The wrapper closes
                        // its end immediately after reading the result.
                        using var read = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                        read.CancelAfter(TimeSpan.FromSeconds(2));
                        await pipe.ReadAsync(new byte[1].AsMemory(), read.Token).ConfigureAwait(false);
                    }
                    catch (IOException error) when (!connected && error.HResult == noData)
                    {
                        // The client closed before ConnectNamedPipe completed.
                        // Managed state never reached Connected. Recreate the native endpoint.
                        pipe.Dispose();
                        if (lifetime.IsCancellationRequested) return;
                        pipe = CreatePipe(pipeName);
                    }
                    catch (IOException error) when (!connected)
                    {
                        Trace.TraceWarning("Yamuse uninstall pipe accept: {0:X8}", error.HResult);
                        return;
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { }
                    finally
                    {
                        if (connected)
                            try { pipe.Disconnect(); }
                            catch (IOException) { }
                    }
                }
            }
            finally { pipe.Dispose(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            _ = _server.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        }
    }
}
