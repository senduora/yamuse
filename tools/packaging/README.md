# Локальная упаковка Windows x64

Приложение использует **Velopack 1.2.161**, стабильный NuGet/vpk от 29.09.2026, лицензия MIT. Прямая зависимость закреплена точным диапазоном `[1.2.161]`, `src/YMusic.Native/packages.lock.json` содержит contentHash. `dotnet-tools.json` фиксирует ту же версию CLI. Для self-contained сборки закреплены .NET Core и Windows Desktop **8.0.32** от 13.10.2026; локально проверено с SDK **8.0.301**.

Публичный источник настроен на [senduora/yamuse](https://github.com/senduora/yamuse). `update-feed.json` копируется в выход сборки и publish через csproj; PAT, секретов, фонового процесса и собственного механизма замены exe нет. Первую проверку/загрузку реального обновления можно подтвердить только после появления корректных Release assets. Приватный feed не подключён и авторизация для него не реализована.

## Сборка

Нужны Windows x64, PowerShell и .NET SDK 8. Для окончательного пакета используйте уже проверенный self-contained publish и хеш DLL, прошедшего общие проверки. Указывайте новые пустые папки:

```powershell
& .\tools\packaging\Pack-Installer.ps1 `
  -Version 0.3.7 `
  -OutputFolder (Join-Path $PWD 'artifacts\setup-0.3.7') `
  -WorkFolder (Join-Path $PWD 'work\package-0.3.7') `
  -PackageCache (Join-Path $PWD 'work\verified-packages') `
  -PublishedFolder $checkedSelfContainedPublish `
  -ExpectedProductSha256 $checkedProductHash
```

Без `PublishedFolder` скрипт копирует исходники в отдельный staging, не трогает активный `app`, публикует self-contained WPF `win-x64`, затем вызывает закреплённый `vpk pack`. В staging создаётся RID-specific lock; основная блокировка зависимостей не переписывается. `PinnedRuntime.targets` отделяет версию Windows SDK projection 10.0.19041.31 от .NET runtime 8.0.32 — глобальная настройка RuntimeFrameworkVersion в SDK 8.0.301 иначе ошибочно применяется к обоим. Transitive framework downloads не отключаются: это нужно WPF profile для получения parent runtime pack. ASP.NET 8.0.32 запрашивается SDK в build cache, но не входит в publish этого WPF-приложения; это отдельно проверено холодным restore.

`-PublishedFolder <проверенная папка>` упаковывает копию без пересборки. Проверяются assembly version, наличие WPF/CoreCLR и точная версия bundled runtime. `-ExpectedProductSha256` связывает пакет с проверенным DLL; тот же хеш затем сравнивается с DLL внутри full.nupkg. В обеих ветках кладутся согласованный публичный `update-feed.json` и `Legal` notices, без подмены DLL.

Для получения одинакового product DLL при проверках и publish держите один project/obj/RID: сначала `build -c Release -r win-x64 --self-contained false`, затем restore/publish с тем же RID и `--self-contained true`. В обеих фазах импортируйте `-p:DirectoryBuildTargetsPath=<полный путь к PinnedRuntime.targets>`. Между фазами нельзя менять исходники; после publish сравните SHA-256 DLL с проверенным. На synthetic WPF с App.xaml и embedded PDB идентичность подтверждена, но это не заменяет сравнения реальной финальной сборки.

На выходе: `YMusic.Native-win-Setup.exe`, `YMusic.Native-<version>-full.nupkg`, `YMusic.Native-win-Portable.zip`, `releases.win.json`, контрольные метаданные и хеши. Скрипт **не** запускает Setup, не создаёт Release/репозиторий, не делает commit/push и не удаляет предыдущие сборки. `OutputFolder` и `WorkFolder` должны быть новыми/пустыми. Удаление промежуточных файлов не выполняется автоматически.

Набор self-contained содержит runtime для компьютеров без установленного .NET. Это увеличивает установщик и место на диске; оно не означает, что все файлы runtime одновременно загружаются в RAM. Отдельного updater-сервиса в фоне нет. RAM/startup реального клиента после упаковки отдельно не измерены.

## Установка, удаление и данные

Стандартный Setup.exe Velopack — per-user, с `%LOCALAPPDATA%\YMusic.Native` и уровнем `asInvoker`, без `--msi` и без загрузки prerequisites. Создаются ярлыки Desktop/StartMenuRoot и штатная запись удаления. Эти действия подтверждены контрактом Velopack и метаданными пакета; установка/удаление в живом профиле в этой работе не выполнялись.

Идентификатор пакета **YMusic.Native** намеренно отличается от существующей папки данных **YMusic.Native.Prototype**. При обновлении заменяется только установленное приложение; наше состояние остаётся в `%LOCALAPPDATA%\YMusic.Native.Prototype`, музыка — в `Документы\YMusic Native\Music`. Не добавлены uninstall hooks, очистка этих папок или импорт данных. При обычном штатном удалении Velopack затрагивает корень установленного пакета, поэтому внешние папки сохраняются. Пользовательский `--installto` в папку данных использовать нельзя.

Bootstrap расположен в `Program.Main`, выбран через StartupObject, и выполняет `VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).Run()` до создания `App`, `InitializeComponent` и `Run`. Fast install/update/uninstall hooks завершаются до открытия WPF/данных. Auto-apply при запуске явно выключен: скачанное обновление ожидает отдельного действия пользователя. При упаковке CLI проверил наличие `VelopackApp.Run` в entry point.

## API для настроек

`UpdateSupport.Current.State` возвращает неизменяемый `AppUpdateState`: `Status`, `FeedConfigured`, `IsInstalled`, `CurrentVersion` (из assembly), `AvailableVersion`, `ProgressPercent`, `Message` на русском. Статусы: `NotConfigured`, `Portable`, `Ready`, `Checking`, `NoUpdate`, `Available`, `Downloading`, `Downloaded`, `Error`.

- `CheckAsync(CancellationToken = default)` проверяет доступность; при уже скачанном локальном обновлении возвращает `Downloaded`.
- `DownloadAsync(IProgress<int>? = null, CancellationToken = default)` скачивает выбранное обновление. Повторный вызов после готовности не скачивает его заново. Операции сериализуются.
- `ApplyAndRestart()` разрешён только после `Downloaded`; вызывается отдельной кнопкой после сохранения состояния и остановки плеера. Только этот метод вызывает официальный SDK apply/restart.

Сам service не ставит таймер и не делает автоматических сетевых вызовов. Отсутствующий файл либо `update-feed.template.json` с null дают немедленный `NotConfigured` без создания update backend. Настроенная переносимая версия даёт `Portable` и также не создаёт сетевой backend. Конфиг допускает только канонический HTTPS URL публичного `github.com/owner/repo`; credentials/query/token/неизвестные поля отклоняются. В состояние/сообщения не попадают raw URL и exception text.

CheckForUpdatesAsync в закреплённом SDK не принимает CancellationToken: начатый запрос не бросается в фоне при отмене, операция остаётся сериализованной до завершения. Download поддерживает отмену SDK. Нет проверки private Releases, серверной авторизации или встроенного PAT.

## Проверка пакетов

`Pack-Installer.ps1` получает только фиксированные файлы NuGet по официальным HTTPS URL, сравнивает встроенные SHA-256 и запускает `dotnet nuget verify --all`. Затем CLI ставится из проверенного локального пакета, restore staging использует локальный feed. Источники/версии/хеши сохраняются в `build-provenance.json`; runtime/host Microsoft проверены по author+repository signature, Velopack/vpk — по repository signature NuGet.

`Inspect-Package.ps1` читает nupkg/nuspec, сравнивает package id/version, размер/SHA-256 Release assets и читает манифест Setup как PE DATAFILE. Дополнительно сверяются product DLL и все пять license/notice по SHA-256 внутри nupkg, включая `Third-Party-Notices.txt` проекта, а встроенный полный nupkg внутри unsigned Setup побайтово подтверждается его SHA-256. Setup не выполняется. Результат — `package-evidence.json`, `package.nuspec.xml`, `setup.manifest.xml`. Финальный synthetic smoke 0.3.7 прошёл: 476 entries, Setup 85 972 751 байт, full package 78 458 639 байт. Это пакет fixture-приложения с App.xaml resource, не итоговый установщик музыкального клиента.

`Legal/Velopack-LICENSE.txt` — точный first-party [LICENSE](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/LICENSE) commit из nuspec Velopack 1.2.161, с copyright Caelan Sayler/Velopack Ltd. Также поставляются неизменённые NETCore LICENSE/THIRD-PARTY-NOTICES и WindowsDesktop LICENSE из подписанных runtime nupkg 8.0.32. Csproj копирует их в output/publish, packscript — в staging. Эти лицензии относятся к зависимостям; лицензию собственного YMusic Native пользователь ещё не выбирал.

Установщик smoke не подписан Authenticode (`NotSigned`); NuGet repository signature исходных инструментов не является подписью нашего Setup. Signing certificate не предоставлен и не придумывается. Реальную установку/обновление/удаление и сохранение данных после них нужно проверять отдельно в разрешённом изолированном профиле/VM. Живой профиль и старый открытый клиент в этой работе не менялись.

Официальные источники: [NuGet Velopack 1.2.161](https://www.nuget.org/packages/Velopack/1.2.161), [NuGet vpk 1.2.161](https://www.nuget.org/packages/vpk/1.2.161), [startup и отключение auto-apply](https://docs.velopack.io/integrating/overview), [per-user Setup](https://docs.velopack.io/packaging/installer), [сохранение внешних данных](https://docs.velopack.io/integrating/preserved-files), [ярлыки](https://docs.velopack.io/integrating/shortcuts), [self-contained без framework bootstrap](https://docs.velopack.io/packaging/bootstrapping), [.NET 8 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json).
