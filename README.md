<p align="center">
  <img src="docs/assets/yamuse-header.svg" alt="Yamuse — твоя музыка в нативном приложении для Windows" width="100%">
</p>

<p align="center">
  <strong>Яндекс Музыка в нативном клиенте для Windows.</strong><br>
  Коллекция, волны, плейлисты и офлайн-музыка в одном приложении на C# и WPF.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4?style=flat-square" alt="Windows 10 и 11 x64">
  <img src="https://img.shields.io/badge/.NET-8-512BD4?style=flat-square" alt=".NET 8">
  <img src="https://img.shields.io/badge/UI-WPF-FF6B59?style=flat-square" alt="Нативный интерфейс WPF">
  <a href="https://github.com/senduora/ymusic-native/releases"><img src="https://img.shields.io/github/v/release/senduora/ymusic-native?style=flat-square&color=FF6B59" alt="Последняя версия"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-7EC699?style=flat-square" alt="Лицензия MIT"></a>
</p>

<p align="center">
  <a href="https://github.com/senduora/ymusic-native/releases/latest/download/YMusic.Native-win-Setup.exe"><strong>Скачать для Windows</strong></a>
  &nbsp;·&nbsp;
  <a href="https://github.com/senduora/ymusic-native/releases/latest/download/YMusic.Native-win-Portable.zip">Portable</a>
  &nbsp;·&nbsp;
  <a href="https://github.com/senduora/ymusic-native/releases">История версий</a>
  &nbsp;·&nbsp;
  <a href="CREDITS.md">Источники и лицензии</a>
</p>

---

## Возможности

| | Что умеет клиент |
| --- | --- |
| **Ваша коллекция** | Вход через Яндекс, «Мне нравится», плейлисты, альбомы, исполнители и история прослушивания. |
| **Музыка под настроение** | Моя волна и отдельная волна по коллекции. |
| **Поиск** | Треки, исполнители, альбомы, сборники, редакционные подборки и плейлисты — с отдельными категориями выдачи. |
| **Исполнители и альбомы** | Карточки музыкантов, популярные треки, месячная аудитория из данных сервиса, дискография и похожие исполнители. Артисты открываются из списков и плеера, альбомы — из колонки в списке треков. |
| **Воспроизведение** | Очередь, перемешивание, повтор, предзагрузка следующего трека и плавные переходы. Выбор качества: «Превосходное», «Оптимальное», «Экономичное». |
| **Офлайн** | Скачивание отдельных треков и целых плейлистов, сохранение статуса загрузки, до 10 одновременных загрузок и автоскачивание избранного по настройке. |
| **Обложки и тексты** | Обложки в коллекции и плеере, встроенный текст песни и подсветка строк при наличии таймкодов. |
| **Удобство Windows** | Мини-плеер, значок в трее, системные медиакнопки, плавная прокрутка и переходы между экранами. |

Интерфейс работает на WPF, воспроизведение — через системный аудиоплеер Windows. В основе клиента — C# и .NET 8.

## Установка

1. Скачайте [YMusic.Native-win-Setup.exe](https://github.com/senduora/ymusic-native/releases/latest/download/YMusic.Native-win-Setup.exe).
2. Запустите установщик и откройте Yamuse.
3. Нажмите «Войти» и завершите вход на странице Яндекса.

Установщик включает .NET Runtime и устанавливает приложение для текущего пользователя. Для доступа к музыке используется ваш аккаунт Яндекса.

Для запуска из папки скачайте [Portable-архив](https://github.com/senduora/ymusic-native/releases/latest/download/YMusic.Native-win-Portable.zip), распакуйте его и откройте `YMusic.Native.exe`.

## Обновления

Установленная версия проверяет новые выпуски в GitHub Releases при запуске. В **Настройках** можно проверить обновления, скачать новую версию и нажать **«Обновить и перезапустить»** в удобный момент.

Скачивание обновления идёт во время работы плеера. Для загрузки приложения и обновлений используется анонимный доступ к публичным релизам GitHub.

## Скачанная музыка и данные

Выберите трек или плейлист и нажмите **«Скачать»**. Для избранного доступна настройка **«Автоматически скачивать “Мне нравится”»**. Число одновременных загрузок выбирается в настройках — от 1 до 10.

| Данные | Папка |
| --- | --- |
| Музыка и офлайн-индексы | `Документы\YMusic Native\Music` |
| Авторизация и настройки | `%LOCALAPPDATA%\YMusic.Native.Prototype` |
| Установленное приложение | `%LOCALAPPDATA%\YMusic.Native` |

Вход сохраняется локально с защитой Windows DPAPI для текущего пользователя. Офлайн-файлы и настройки хранятся отдельно от файлов приложения.

## Разработка

Нужны **Windows** и **.NET 8 SDK**.

```powershell
git clone https://github.com/senduora/ymusic-native.git
cd ymusic-native
dotnet run --project src/YMusic.Native/YMusic.Native.csproj -c Release
```

Проверки используют временные папки, тестовые HTTP-ответы и аудиофайлы:

```powershell
dotnet run --project tests/YMusic.Native.Checks/YMusic.Native.Checks.csproj -c Release -- work/check-results
```

Упаковка установщика и файлов обновления:

```powershell
./tools/packaging/Pack-Installer.ps1 -Version 0.3.7 -OutputFolder ./artifacts/setup -WorkFolder ./work/package
```

Подробности сборки и упаковки — в [руководстве](tools/packaging/README.md).

## Источники и благодарности

В проекте используются адаптированные фрагменты [YandexMusic for .NET](https://github.com/jrfrigat/YandexMusic) и механизм установки и обновлений [Velopack](https://github.com/velopack/velopack).

Для изучения API и устройства музыкальных клиентов использовались [trashee](https://github.com/onemorefix1337/trashee), [yandex-music-client](https://github.com/aeviww/yandex-music-client), [yandex-music-api](https://github.com/MarshalX/yandex-music-api) и [yandex-music-open-api](https://github.com/acherkashin/yandex-music-open-api). Дополнительные источники текстов — [LRCLIB](https://lrclib.net/) и [Genius](https://genius.com/).

Авторы, версии, лицензии и роль каждого источника перечислены в [CREDITS.md](CREDITS.md). Лицензионные уведомления сохранены в [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) и [tools/packaging/licenses](tools/packaging/licenses/).

## Права и лицензия

Собственный исходный код Yamuse распространяется по [лицензии MIT](LICENSE). © 2026 senduora и участники проекта.

Yamuse — независимый неофициальный проект. Яндекс Музыка и связанные товарные знаки принадлежат их правообладателям. Права на музыку, обложки и тексты сохраняются за соответствующими правообладателями; использование сервиса регулируется [условиями Яндекс Музыки](https://yandex.ru/legal/music_termsofuse/).

Сторонние компоненты распространяются на условиях их собственных лицензий с сохранением авторства и уведомлений.
