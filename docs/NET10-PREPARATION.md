# Переход Yamuse на .NET 10

Начиная с Yamuse 0.5.0 основной проект, тесты и средства native QA используют
.NET 10. Установщик и Portable включают runtime; отдельно ставить .NET для запуска
готового клиента не требуется. Для сборки SDK **10.0.403** закреплён в global.json.

## Воспроизводимый сценарий

Штатная проверка — `tools/Verify-Project.ps1`. Ей нужна новая RunName для каждого
запуска. Исходники не копируются в старый workspace и не зависят от прежней папки.
Параметр DotnetPath позволяет использовать локально распакованный SDK без изменения
системной установки или PATH.

TFM продукта и fixtures — `net10.0-windows10.0.19041.0`, Windows projection —
`10.0.19041.57`. Выполняются locked restore, self-contained publish и build для
win-x64. Фактические includedFrameworks должны быть `10.0.14`; publish и проверки
используют одну product DLL. Velopack/vpk остаются **1.2.161**.

```powershell
# Если SDK 10.0.403 установлен и доступен через dotnet.
./tools/Verify-Project.ps1 -RunName net10-check-001 -Full

# Только restore/publish/build, без оконных проверок.
./tools/Verify-Project.ps1 -RunName net10-build-001 -BuildOnly
```

RunName всегда новый. `-BuildOnly` выдаёт `BUILD_PASS_NATIVE_PENDING`, а не PASS
проверок. Полный запуск использует private-desktop-runner под выбранным .NET 10 host;
блокировку Windows он не обходит, рабочий стол не переключает и физический ввод
не отправляет. Журналы, checks.json и квитанции находятся в work выбранного запуска.

## Выявленные различия

- SDK 10 использует профиль `Microsoft.Windows.SDK.NET.Ref.Windows`. Глобальная
  RuntimeFrameworkVersion ошибочно превращала номер .NET runtime в номер Windows
  projection. Применён отдельный WindowsSdkPackageVersion; runtime выбирает
  закреплённый SDK, результат обязательно сверяется после publish.
- C# 14 выбирает span-перегрузку SequenceEqual. В старой проверке DownloadCenter
  чтение файла через await вынесено перед сравнением массивов, чтобы span не
  пересекал границу await. Условия проверки сохранены для обеих платформ.
- Для новой очереди через OLE передаётся только строковый одноразовый идентификатор.
  Объекты очереди остаются в окне, внешние сериализованные объекты не читаются.
  Опасная совместимость с BinaryFormatter не включается.

## Штатная упаковка

1. Использовать точный publish после native QA и передать его SHA-256 в
   Pack-Installer через ExpectedProductSha256. SDK/runtime и полная версия проверяются.
2. Pack-Installer получает закреплённые Microsoft и Velopack NuGet packages,
   проверяет SHA-256 и подписи; notices берутся побайтно из подписанных runtime packages.
3. Inspect-Package сверяет DLL, apphost, CoreCLR, WPF, runtimeconfig, иконки и notices
   в full.nupkg/Portable, а также встроенный пакет Setup без его запуска.
4. Публиковать только проверенный конкретный пакет с анонимным полным readback.
   Установка, применение обновления и работа с настоящим аккаунтом — отдельные действия.

Подробнее: [руководство упаковки](../tools/packaging/README.md).

## Первичные источники

- [Поддержка .NET](https://dotnet.microsoft.com/en-us/platform/support/policy).
- [Переход между версиями WPF](https://learn.microsoft.com/dotnet/desktop/wpf/migration/).
- [WPF .NET 10 и изменения clipboard](https://learn.microsoft.com/dotnet/desktop/wpf/whats-new/net100).
- [Совместимость .NET 10](https://learn.microsoft.com/dotnet/core/compatibility/10).
- [Официальные SDK/runtime metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
