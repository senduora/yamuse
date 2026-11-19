# Локальная упаковка Windows x64

Приложение использует **Velopack 1.2.161**, стабильный NuGet/vpk от 29.09.2026, лицензия MIT. Прямая зависимость закреплена точным диапазоном `[1.2.161]`, `src/YMusic.Native/packages.lock.json` содержит contentHash. `dotnet-tools.json` фиксирует ту же версию CLI. Для self-contained сборки закреплены .NET Core и Windows Desktop **10.0.15**, для SDK в global.json — **10.0.404**, для Windows projection — **10.0.19041.57**.

Файл `update-feed.json` входит в сборку и указывает на публичный репозиторий. Yamuse запрашивает последний стабильный выпуск и сверяет пакет с его опубликованным SHA-256. При явном применении клиент готовит проверенный `Update.exe` из пакета и передаёт установку Velopack. [Официальный репозиторий](https://github.com/senduora/yamuse)

## Сборка

Номер выпуска выбирается и согласуется по [политике `X.Y.Z`](../../docs/VERSIONING.md). Для упаковки нужна стабильная версия. До подготовки пакета скрипт сверяет `-Version` с `Version` и `AssemblyVersion` исходного проекта или с `InformationalVersion` и `AssemblyVersion` готовой DLL.

Нужны Windows x64, PowerShell и .NET SDK 10.0.404. Выполняйте пример из корня репозитория для согласованного пакета. Укажите `RunName` уже пройденной полной проверки `tools/Verify-Project.ps1 -Full`. В её квитанции записаны папка publish, `InformationalVersion` и SHA-256 проверенных DLL и EXE.

```powershell
$runName = Read-Host 'RunName пройденной полной проверки'
$verifiedRun = Join-Path (Join-Path $PWD 'work') $runName
$receiptPath = Join-Path $verifiedRun 'verification.json'
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.status -cne 'PASS' -or !$receipt.full) { throw 'Требуется квитанция полной проверки.' }

$version = ([string]$receipt.productVersion).Split('+')[0]
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Требуется стабильная версия X.Y.Z.' }

$attempt = "package-$version-" + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
$outputFolder = Join-Path $PWD "artifacts\$attempt"
$packageWork = Join-Path $PWD "work\$attempt"
$pack = @{
    Version = $version
    OutputFolder = $outputFolder
    WorkFolder = $packageWork
    PublishedFolder = Join-Path $verifiedRun 'publish'
    VerificationReceipt = $receiptPath
}
# Для локального SDK добавьте $pack.DotnetPath с полным путём к dotnet.exe.
# При наличии локального кэша добавьте $pack.PackageCache с путём к папке с nupkg.
& .\tools\packaging\Pack-Installer.ps1 @pack
```

Без `PublishedFolder` скрипт копирует исходники и global.json в отдельный staging, не трогает активный `app`, публикует self-contained WPF `win-x64`, затем вызывает закреплённый `vpk pack`. В staging создаётся RID-specific lock; основная блокировка зависимостей не переписывается. `PinnedRuntime.targets` задаёт WindowsSdkPackageVersion отдельно: глобальная RuntimeFrameworkVersion несовместима с Windows profile references SDK 10. Transitive framework downloads не отключаются. Итоговые Core и WPF runtime 10.0.15 обязательно сверяются в runtimeconfig; ASP.NET может быть загружен SDK в build cache, но не поставляется WPF-приложением.

`-PublishedFolder <проверенная папка>` упаковывает копию без пересборки. Скрипт сверяет папку, версию и SHA-256 полной описи файлов с `-VerificationReceipt`, затем повторно сверяет опись скопированной папки перед добавлением публичного `update-feed.json` и документов `Legal`. Опись учитывает относительные пути, размеры и SHA-256 всех файлов, включая DLL, EXE и `.deps.json`. Для отдельных локальных проверок SHA-256 DLL и EXE можно передать через `-ExpectedProductSha256` и `-ExpectedAppHostSha256`. Скрипт проверяет привязку закреплённого x64 apphost к `YMusic.Native.dll`. Ожидаемая опись для обоих архивов и встроенного пакета Setup формируется до вызова Velopack из проверенного publish и точных замен feed и Legal. Её SHA-256 записывается в `build-provenance.json` как `stagePublishInventorySha256`.

Для получения одинакового product DLL при проверках и publish держите один project/obj/RID: сначала `build -c Release -r win-x64 --self-contained false`, затем restore/publish с тем же RID и `--self-contained true`. В обеих фазах импортируйте `-p:DirectoryBuildTargetsPath=<полный путь к PinnedRuntime.targets>`. Между фазами нельзя менять исходники; после publish сравните SHA-256 DLL с проверенным. На synthetic WPF с App.xaml и embedded PDB идентичность подтверждена, но это не заменяет сравнения реальной финальной сборки.

На выходе: `Yamuse-win-Setup.exe`, `Yamuse-<version>-full.nupkg`, `Yamuse-win-Portable.zip`, `releases.win.json`, контрольные метаданные и хеши. Скрипт **не** запускает Setup, не создаёт Release/репозиторий, не делает commit/push и не удаляет предыдущие сборки. `OutputFolder` и `WorkFolder` должны быть новыми или пустыми. Для `OutputFolder`, `WorkFolder` и `PublishedFolder` используйте отдельные папки без вложенности. Удаление промежуточных файлов не выполняется автоматически.

По умолчанию кэш NuGet хранится в `<WorkFolder>\packages`. Если задаёте другой `-PackageCache`, выделите ему отдельную папку без пересечения с исходным publish, рабочей папкой и выходом. Скрипты проверяют существующие компоненты этих путей и содержимое publish перед копированием или записью.

При сборке из исходников с буквальным `%HH` в пути `WorkFolder` (например, `%2E`) SDK использует отдельный каталог `.yamuse-sdk-…` под ближайшим подходящим предком или в `work` репозитория. Его путь записывается в `build-provenance.json` как `sdkWorkFolder`. Проверенный publish, RID lock и выбранный кэш `.nupkg` по-прежнему доступны по своим обычным путям.

Набор self-contained содержит runtime для компьютеров без установленного .NET. Это увеличивает установщик и место на диске; оно не означает, что все файлы runtime одновременно загружаются в RAM. Отдельного updater-сервиса в фоне нет. RAM/startup реального клиента после упаковки отдельно не измерены.

## Установка, удаление и данные

Стандартный Setup.exe Velopack — per-user, с `%LOCALAPPDATA%\YMusic.Native` и уровнем `asInvoker`, без `--msi` и без загрузки prerequisites. Создаются ярлыки Desktop/StartMenuRoot и штатная запись удаления. Эти действия подтверждены контрактом Velopack и метаданными пакета; установка/удаление в живом профиле в этой работе не выполнялись.

Идентификатор пакета **YMusic.Native** сохраняется для совместимости с уже установленными выпусками. Данные Yamuse находятся в `%LOCALAPPDATA%\Yamuse`, музыка — в `Документы\Yamuse\Music`. При первом старте приложение переносит локальный профиль и музыку из доступной локальной папки. Сетевые и недоступные «Документы» оно оставляет без изменений. Пользовательский `--installto` в папку данных использовать нельзя.

Полное удаление запускается кнопкой в настройках установленного приложения. После явного подтверждения приложение завершает загрузки и воспроизведение, выходит, а отдельный этап очищает фиксированные пользовательские папки и вызывает штатный `Update.exe --uninstall`. Музыка удаляется по собственному индексу с проверкой размера и SHA-256. Посторонние и изменённые файлы сохраняются с сообщением; при занятом файле индекс остаётся для повторной попытки. Переходы через внешние junction/symlink запрещены, удаление файлов выполняется через проверенные handles.

При обычном запуске и после обновления `UninstallSupport` заменяет только известную штатную команду в существующей HKCU-записи удаления Yamuse на `current\YMusic.Native.exe --uninstall`. Поэтому удаление через параметры Windows после запуска приложения использует такое же подтверждение и очистку. Portable не регистрирует удаление. Прямой вызов нативного `Update.exe --uninstall` по контракту Velopack удаляет только установленный пакет; интерактивную очистку данных нужно запускать через Yamuse.

`Program.Main` выбран через `StartupObject`. При обычном запуске он захватывает общий mutex и проверяет маркер удаления перед инициализацией Velopack и WPF. Velopack получает параметры запуска и обрабатывает fast hooks до создания окна. Обычный SDK-запуск не перечисляет и не очищает кэш пакетов. Скачанное обновление ожидает явного применения. Команды полного удаления обрабатываются отдельной веткой, а демо открывается без инициализации Velopack. При упаковке CLI проверяет вызов `VelopackApp.Run` в точке входа.

## API для настроек

`UpdateSupport.Current.State` возвращает неизменяемый `AppUpdateState`: `Status`, `FeedConfigured`, `IsInstalled`, `CurrentVersion` (из assembly), `AvailableVersion`, `ProgressPercent`, `Message` на русском. Статусы: `NotConfigured`, `Portable`, `Ready`, `Checking`, `NoUpdate`, `Available`, `Downloading`, `Downloaded`, `Error`.

- `CheckAsync(CancellationToken = default)` проверяет доступность; при уже скачанном локальном обновлении возвращает `Downloaded`.
- `DownloadAsync(IProgress<int>? = null, CancellationToken = default)` скачивает выбранное обновление. Повторный вызов после готовности не скачивает его заново. Операции сериализуются.
- `ApplyAndRestart()` разрешён только после `Downloaded`; вызывается отдельной кнопкой после сохранения состояния и остановки плеера. Только этот метод вызывает официальный SDK apply/restart.

Сам service не ставит таймер и не делает автоматических сетевых вызовов. Отсутствующий файл либо `update-feed.template.json` с null дают немедленный `NotConfigured` без создания update backend. Переносимая версия проверяет публичные метаданные через `CheckPortableReleaseAsync` и предлагает официальный установщик или архив; backend установки Velopack для неё не создаётся. Единственный источник обновлений для установленной и переносимой версий — официальный публичный репозиторий `https://github.com/senduora/yamuse`. В состояние/сообщения не попадают raw URL и exception text.

Проверка выпуска и скачивание пакета поддерживают отмену. Если пакет уже сохранён и проверен, последующая отмена сохраняет его готовность к установке. Клиент обращается к публичным выпускам GitHub без авторизации.

## Проверка пакетов

`Pack-Installer.ps1` получает только фиксированные файлы NuGet по официальным HTTPS URL. Он скачивает пакет во временный файл в кэше и присваивает окончательное имя после сверки SHA-256. Если другой запуск уже записал пакет, скрипт сверяет его хеш и сохраняет файл на месте. Затем `dotnet nuget verify --all` проверяет подписи, CLI ставится из проверенного локального пакета, а restore staging использует локальный feed. Источники, версии и хеши сохраняются в `build-provenance.json`.

`Pack-Installer.ps1` проверяет точный SHA-256 `runtimeconfig.json` и состав `.deps.json` для win-x64. Байты `.deps.json` сверяются с описью и разбираются как строгий JSON в UTF-8, в том числе с UTF-8 BOM. Проверка охватывает пути и метаданные, по которым .NET находит библиотеки. Также сверяется SHA-256 закреплённого runtime, включая Velopack, Windows projection и локализованные сборки WPF. `Inspect-Package.ps1` сверяет полную запись в `releases.win.json` по ID пакета, версии, имени, размеру и хешам. Он также сверяет три записи в `assets.win.json` и строку полного пакета в `RELEASES` по имени, размеру и SHA-1. Файлы nupkg и Portable ZIP сверяются побайтно с `-ReferencePublishedFolder`. Velopack может пропустить `createdump.exe` и добавляет известные служебные файлы. Три общих служебных файла дополнительно сверяются между архивами. Манифест и иконка Setup читаются как PE DATAFILE, а SHA-256 подтверждает соответствие встроенного nupkg. Скрипт пишет `package-evidence.json`, `package.nuspec.xml` и `setup.manifest.xml`.

При упаковке `Pack-Installer.ps1` передаёт инспектору папку stage и утверждённый SHA-256 её описи. Для повторной проверки сохраните `<WorkFolder>\publish` и `build-provenance.json`. С переменными из примера упаковки команда выглядит так.

```powershell
$provenance = Get-Content -LiteralPath (Join-Path $outputFolder 'build-provenance.json') -Raw | ConvertFrom-Json
& .\tools\packaging\Inspect-Package.ps1 `
    -OutputFolder $outputFolder `
    -ExpectedVersion $version `
    -ReferencePublishedFolder (Join-Path $packageWork 'publish') `
    -ExpectedStageInventorySha256 $provenance.stagePublishInventorySha256 `
    -ExpectedProductSha256 $receipt.productDllSha256 `
    -ExpectedAppHostSha256 $receipt.mainExecutableSha256
```

`Legal/Velopack-LICENSE.txt` — точный first-party [LICENSE](https://github.com/velopack/velopack/blob/92d6a1c91716729d449034df5c50307dcce39493/LICENSE) commit из nuspec Velopack 1.2.161, с copyright Caelan Sayler/Velopack Ltd. Также поставляются точные NETCore LICENSE/THIRD-PARTY-NOTICES и WindowsDesktop LICENSE из подписанных runtime nupkg 10.0.15. Csproj копирует их в output/publish, packscript — в staging. Эти лицензии относятся к зависимостям; собственный код Yamuse распространяется по лицензии MIT (см. LICENSE). Vendor notices сохраняются побайтно; Git не нормализует их line endings или upstream whitespace. Инспектор сверяет SHA-256.

Установщик smoke не подписан Authenticode (`NotSigned`); NuGet repository signature исходных инструментов не является подписью нашего Setup. Signing certificate не предоставлен и не придумывается. Реальную установку/обновление/удаление и сохранение данных после них нужно проверять отдельно в разрешённом изолированном профиле/VM. Живой профиль и старый открытый клиент в этой работе не менялись.

Официальные источники: [NuGet Velopack 1.2.161](https://www.nuget.org/packages/Velopack/1.2.161), [NuGet vpk 1.2.161](https://www.nuget.org/packages/vpk/1.2.161), [startup и отключение auto-apply](https://docs.velopack.io/integrating/overview), [per-user Setup](https://docs.velopack.io/packaging/installer), [сохранение внешних данных](https://docs.velopack.io/integrating/preserved-files), [ярлыки](https://docs.velopack.io/integrating/shortcuts), [self-contained без framework bootstrap](https://docs.velopack.io/packaging/bootstrapping), [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).

## Описания опубликованных выпусков

- Перед подготовкой описания получить актуальный список GitHub Releases. Для стабильного выпуска база — предыдущий уже опубликованный стабильный релиз по `published_at`; draft, локальные тестовые сборки и prerelease не сдвигают эту базу. Нельзя выбирать базу вычитанием единицы из номера версии. Для отдельного prerelease-канала указывать его базу явно; он не заменяет стабильную историю.
- Описание содержит весь итоговый набор изменений между тегом базы и тегом публикуемого выпуска, включая изменения всех пропущенных локальных версий. Для первого опубликованного релиза описать начальный набор возможностей. Проверить связь коммитов и изменения исходников между точными тегами; локальные `RELEASE-NOTES` сами по себе не доказывают публикацию или наличие функции.
- Включать только фактически вошедшие в публикуемый тег изменения. Объединять повторяющиеся промежуточные пункты по конечному поведению, указывать предыдущий опубликованный выпуск и ссылку на сравнение исходников. Не переносить возможности более новых локальных сборок.
- Писать простое пользовательское описание на русском. Разделы проблем и known issues добавлять только по отдельному поручению пользователя.
- Сохранять один и тот же окончательный текст в GitHub Release и `docs/releases/RELEASE-NOTES-<версия>.md`; обновлять индекс `docs/releases/README.md` только фактически опубликованными выпусками. Имена файлов и ссылки на загрузку сверять с настоящими assets этого релиза.
- Редактирование описания существующего выпуска не создаёт новую версию и не меняет теги, пакеты или assets. Перед записью сохранить исходное описание и свежую метадату, проверить отсутствие параллельных изменений; после записи прочитать описание обратно, сравнить текст и SHA-256, подтвердить неизменность остальных полей и assets. Commit/push и публикация выполняются только в отдельно разрешённом объёме.
