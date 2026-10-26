# Локальная упаковка Windows x64

Приложение использует **Velopack 1.2.161**, стабильный NuGet/vpk от 29.09.2026, лицензия MIT. Прямая зависимость закреплена точным диапазоном `[1.2.161]`, `src/YMusic.Native/packages.lock.json` содержит contentHash. `dotnet-tools.json` фиксирует ту же версию CLI. Для self-contained сборки закреплены .NET Core и Windows Desktop **8.0.32** от 13.10.2026; локально проверено с SDK **8.0.301**.

Публичный источник настроен на [senduora/yamuse](https://github.com/senduora/yamuse). `update-feed.json` копируется в выход сборки и publish через csproj; PAT, секретов, фонового процесса и собственного механизма замены exe нет. Проверка публичного выпуска и загрузка его пакета сверяются с опубликованными Release assets и SHA-256.

## Сборка

Новые версии следуют [политике `X.Y.Z`](../../docs/VERSIONING.md): `X` — крупное обновление, `Y` — новые небольшие возможности, `Z` — исправления. Текущий стабильный выпуск — `0.4.0`. Полная `InformationalVersion` обязательна при проверке пакета: числовая `AssemblyVersion` равна `0.4.0.0` и не отличает preview от стабильной версии. Скрипт принимает только стабильную `X.Y.Z`; до создания рабочих папок и получения инструментов он отклоняет preview-исходники либо несовпадение полной версии готовой DLL. Упаковка preview пока не поддерживается.

Нужны Windows x64, PowerShell и .NET SDK 8. Для окончательного пакета используйте уже проверенный self-contained publish и хеш DLL, прошедшего общие проверки. Указывайте новые пустые папки:

```powershell
& .\tools\packaging\Pack-Installer.ps1 `
  -Version 0.4.0 `
  -OutputFolder (Join-Path $PWD 'artifacts\setup-0.4.0') `
  -WorkFolder (Join-Path $PWD 'work\package-0.4.0') `
  -PackageCache (Join-Path $PWD 'work\verified-packages') `
  -PublishedFolder $checkedSelfContainedPublish `
  -ExpectedProductSha256 $checkedProductHash
```

Без `PublishedFolder` скрипт копирует исходники в отдельный staging, не трогает активный `app`, публикует self-contained WPF `win-x64`, затем вызывает закреплённый `vpk pack`. В staging создаётся RID-specific lock; основная блокировка зависимостей не переписывается. `PinnedRuntime.targets` отделяет версию Windows SDK projection 10.0.19041.31 от .NET runtime 8.0.32 — глобальная настройка RuntimeFrameworkVersion в SDK 8.0.301 иначе ошибочно применяется к обоим. Transitive framework downloads не отключаются: это нужно WPF profile для получения parent runtime pack. ASP.NET 8.0.32 запрашивается SDK в build cache, но не входит в publish этого WPF-приложения; это отдельно проверено холодным restore.

`-PublishedFolder <проверенная папка>` упаковывает копию без пересборки. Проверяются assembly version, наличие WPF/CoreCLR и точная версия bundled runtime. `-ExpectedProductSha256` связывает пакет с проверенным DLL; тот же хеш затем сравнивается с DLL внутри full.nupkg. В обеих ветках кладутся согласованный публичный `update-feed.json` и `Legal` notices, без подмены DLL.

Для получения одинакового product DLL при проверках и publish держите один project/obj/RID: сначала `build -c Release -r win-x64 --self-contained false`, затем restore/publish с тем же RID и `--self-contained true`. В обеих фазах импортируйте `-p:DirectoryBuildTargetsPath=<полный путь к PinnedRuntime.targets>`. Между фазами нельзя менять исходники; после publish сравните SHA-256 DLL с проверенным. На synthetic WPF с App.xaml и embedded PDB идентичность подтверждена, но это не заменяет сравнения реальной финальной сборки.

На выходе: `Yamuse-win-Setup.exe`, `Yamuse-<version>-full.nupkg`, `Yamuse-win-Portable.zip`, `releases.win.json`, контрольные метаданные и хеши. Скрипт **не** запускает Setup, не создаёт Release/репозиторий, не делает commit/push и не удаляет предыдущие сборки. `OutputFolder` и `WorkFolder` должны быть новыми/пустыми. Удаление промежуточных файлов не выполняется автоматически.

Набор self-contained содержит runtime для компьютеров без установленного .NET. Это увеличивает установщик и место на диске; оно не означает, что все файлы runtime одновременно загружаются в RAM. Отдельного updater-сервиса в фоне нет. RAM/startup реального клиента после упаковки отдельно не измерены.

## Установка, удаление и данные

Стандартный Setup.exe Velopack — per-user, с `%LOCALAPPDATA%\YMusic.Native` и уровнем `asInvoker`, без `--msi` и без загрузки prerequisites. Создаются ярлыки Desktop/StartMenuRoot и штатная запись удаления. Эти действия подтверждены контрактом Velopack и метаданными пакета; установка/удаление в живом профиле в этой работе не выполнялись.

Идентификатор пакета **YMusic.Native** сохраняется для совместимости с уже установленными выпусками. Данные Yamuse находятся в `%LOCALAPPDATA%\Yamuse`, музыка — в `Документы\Yamuse\Music`. При первом старте новая версия безопасно переносит прежние папки без повторного скачивания музыки; пользовательский `--installto` в папку данных использовать нельзя.

Полное удаление запускается кнопкой в настройках установленного приложения. После явного подтверждения приложение завершает загрузки и воспроизведение, выходит, а отдельный этап очищает фиксированные пользовательские папки и вызывает штатный `Update.exe --uninstall`. Музыка удаляется по собственному индексу с проверкой размера и SHA-256. Посторонние и изменённые файлы сохраняются с сообщением; при занятом файле индекс остаётся для повторной попытки. Переходы через внешние junction/symlink запрещены, удаление файлов выполняется через проверенные handles.

При обычном запуске и после обновления `UninstallSupport` заменяет только известную штатную команду в существующей HKCU-записи удаления Yamuse на `current\YMusic.Native.exe --uninstall`. Поэтому удаление через параметры Windows после запуска приложения использует такое же подтверждение и очистку. Portable не регистрирует удаление. Прямой вызов нативного `Update.exe --uninstall` по контракту Velopack удаляет только установленный пакет; интерактивную очистку данных нужно запускать через Yamuse.

Bootstrap расположен в `Program.Main`, выбран через StartupObject, и выполняет `VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)` с регистрацией after-update callback и `Run()` до создания `App`, `InitializeComponent` и `Run`. Fast hooks завершаются до открытия WPF/данных. Команды полного удаления обрабатываются до старта приложения; защитный marker не позволяет заново создавать данные во время передачи управления штатному деинсталлятору. Auto-apply при запуске явно выключен: скачанное обновление ожидает отдельного действия пользователя. При упаковке CLI проверяет наличие `VelopackApp.Run` в entry point.

## API для настроек

`UpdateSupport.Current.State` возвращает неизменяемый `AppUpdateState`: `Status`, `FeedConfigured`, `IsInstalled`, `CurrentVersion` (из assembly), `AvailableVersion`, `ProgressPercent`, `Message` на русском. Статусы: `NotConfigured`, `Portable`, `Ready`, `Checking`, `NoUpdate`, `Available`, `Downloading`, `Downloaded`, `Error`.

- `CheckAsync(CancellationToken = default)` проверяет доступность; при уже скачанном локальном обновлении возвращает `Downloaded`.
- `DownloadAsync(IProgress<int>? = null, CancellationToken = default)` скачивает выбранное обновление. Повторный вызов после готовности не скачивает его заново. Операции сериализуются.
- `ApplyAndRestart()` разрешён только после `Downloaded`; вызывается отдельной кнопкой после сохранения состояния и остановки плеера. Только этот метод вызывает официальный SDK apply/restart.

Сам service не ставит таймер и не делает автоматических сетевых вызовов. Отсутствующий файл либо `update-feed.template.json` с null дают немедленный `NotConfigured` без создания update backend. Переносимая версия проверяет публичные метаданные через `CheckPortableReleaseAsync` и предлагает официальный установщик или архив; backend установки Velopack для неё не создаётся. Конфиг допускает только канонический HTTPS URL публичного `github.com/owner/repo`; credentials/query/token/неизвестные поля отклоняются. В состояние/сообщения не попадают raw URL и exception text.

CheckForUpdatesAsync в закреплённом SDK не принимает CancellationToken: начатый запрос не бросается в фоне при отмене, операция остаётся сериализованной до завершения. Download поддерживает отмену SDK. Нет проверки private Releases, серверной авторизации или встроенного PAT.

## Проверка пакетов

`Pack-Installer.ps1` получает только фиксированные файлы NuGet по официальным HTTPS URL, сравнивает встроенные SHA-256 и запускает `dotnet nuget verify --all`. Затем CLI ставится из проверенного локального пакета, restore staging использует локальный feed. Источники/версии/хеши сохраняются в `build-provenance.json`; runtime/host Microsoft проверены по author+repository signature, Velopack/vpk — по repository signature NuGet.

`Inspect-Package.ps1` читает nupkg/nuspec, сравнивает package id/version, размер/SHA-256 Release assets и читает манифест Setup как PE DATAFILE. Дополнительно сверяются product DLL и все шесть license/notice по SHA-256 внутри nupkg, включая `Third-Party-Notices.txt` и собственную MIT-лицензию `Yamuse-LICENSE.txt`. Встроенный полный nupkg внутри unsigned Setup подтверждается его SHA-256. Новый обязательный аргумент `-ReferencePublishedFolder` указывает на тот же проверенный publish: apphost сравнивается побайтово, его ProductName/FileDescription и native icon resources, а также иконка Setup должны соответствовать Yamuse. `Pack-Installer.ps1` передаёт reference и ожидаемые SHA автоматически. Setup не выполняется. Результат — `package-evidence.json`, `package.nuspec.xml`, `setup.manifest.xml`.

`Legal/Velopack-LICENSE.txt` — точный first-party [LICENSE](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/LICENSE) commit из nuspec Velopack 1.2.161, с copyright Caelan Sayler/Velopack Ltd. Также поставляются неизменённые NETCore LICENSE/THIRD-PARTY-NOTICES и WindowsDesktop LICENSE из подписанных runtime nupkg 8.0.32. Csproj копирует их в output/publish, packscript — в staging. Эти лицензии относятся к зависимостям; собственный код Yamuse распространяется по лицензии MIT (см. LICENSE).

Установщик smoke не подписан Authenticode (`NotSigned`); NuGet repository signature исходных инструментов не является подписью нашего Setup. Signing certificate не предоставлен и не придумывается. Реальную установку/обновление/удаление и сохранение данных после них нужно проверять отдельно в разрешённом изолированном профиле/VM. Живой профиль и старый открытый клиент в этой работе не менялись.

Официальные источники: [NuGet Velopack 1.2.161](https://www.nuget.org/packages/Velopack/1.2.161), [NuGet vpk 1.2.161](https://www.nuget.org/packages/vpk/1.2.161), [startup и отключение auto-apply](https://docs.velopack.io/integrating/overview), [per-user Setup](https://docs.velopack.io/packaging/installer), [сохранение внешних данных](https://docs.velopack.io/integrating/preserved-files), [ярлыки](https://docs.velopack.io/integrating/shortcuts), [self-contained без framework bootstrap](https://docs.velopack.io/packaging/bootstrapping), [.NET 8 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json).

## Описания опубликованных выпусков

- Перед подготовкой описания получить актуальный список GitHub Releases. Для стабильного выпуска база — предыдущий уже опубликованный стабильный релиз по `published_at`; draft, локальные тестовые сборки и prerelease не сдвигают эту базу. Нельзя выбирать базу вычитанием единицы из номера версии. Для отдельного prerelease-канала указывать его базу явно; он не заменяет стабильную историю.
- Описание содержит весь итоговый набор изменений между тегом базы и тегом публикуемого выпуска, включая изменения всех пропущенных локальных версий. Для первого опубликованного релиза описать начальный набор возможностей. Проверить связь коммитов и изменения исходников между точными тегами; локальные `RELEASE-NOTES` сами по себе не доказывают публикацию или наличие функции.
- Включать только фактически вошедшие в публикуемый тег изменения. Объединять повторяющиеся промежуточные пункты по конечному поведению, указывать предыдущий опубликованный выпуск и ссылку на сравнение исходников. Не переносить возможности более новых локальных сборок.
- Писать простое пользовательское описание на русском. Разделы проблем и known issues добавлять только по отдельному поручению пользователя.
- Сохранять один и тот же окончательный текст в GitHub Release и `docs/releases/RELEASE-NOTES-<версия>.md`; обновлять индекс `docs/releases/README.md` только фактически опубликованными выпусками. Имена файлов и ссылки на загрузку сверять с настоящими assets этого релиза.
- Редактирование описания существующего выпуска не создаёт новую версию и не меняет теги, пакеты или assets. Перед записью сохранить исходное описание и свежую метадату, проверить отсутствие параллельных изменений; после записи прочитать описание обратно, сравнить текст и SHA-256, подтвердить неизменность остальных полей и assets. Commit/push и публикация выполняются только в отдельно разрешённом объёме.
