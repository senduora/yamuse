# Источники, авторы и лицензии

Yamuse использует открытые компоненты, документацию сервисов и результаты изучения других музыкальных клиентов. Ниже указана роль каждого источника и лицензия соответствующего материала.

Собственный код Yamuse опубликован под [MIT](LICENSE), copyright © 2026 senduora и участники проекта. Для сторонних материалов действуют приведённые ниже исходные лицензии и уведомления.

## Код и компоненты приложения

| Источник | Авторы | Использование | Лицензия и уведомления |
| --- | --- | --- | --- |
| [YandexMusic for .NET](https://github.com/jrfrigat/YandexMusic/tree/8c929094386137669766632f9cc2c97d3b05f670) | FrigaT | Адаптированные фрагменты работы с API: вход, подпись запросов, разбор данных, каталог, коллекция, волны и тексты. | [MIT](https://github.com/jrfrigat/YandexMusic/blob/8c929094386137669766632f9cc2c97d3b05f670/LICENSE); полное уведомление и copyright сохранены в [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). |
| [Velopack / vpk 1.2.161](https://github.com/velopack/velopack) | Caelan Sayler, Velopack Ltd. и участники проекта | Установщик, упаковка и библиотека обновления приложения. | [MIT](tools/packaging/licenses/Velopack-LICENSE.txt). |
| [.NET и Windows Desktop Runtime 8.0.32](https://github.com/dotnet) | .NET Foundation, Microsoft и участники проектов | Среда выполнения, WPF и системные библиотеки. | [MIT](tools/packaging/licenses/Microsoft.NETCore-LICENSE.txt), [Windows Desktop LICENSE](tools/packaging/licenses/Microsoft.WindowsDesktop-LICENSE.txt), [уведомления сторонних компонентов](tools/packaging/licenses/Microsoft.NETCore-THIRD-PARTY-NOTICES.txt). |
| [Microsoft.Windows.SDK.NET.Ref 10.0.19041.31](https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.19041.31) | Microsoft | Проекции Windows SDK: `Microsoft.Windows.SDK.NET.dll` и `WinRT.Runtime.dll`. | [Условия Windows SDK](https://aka.ms/WinSDKLicenseURL); сведения о составе — в [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). |

Лицензионные тексты компонентов входят в дистрибутив в папке `Legal`.

## Проекты, использованные как справочные материалы

Эти проекты изучались для сопоставления API, форматов ответов и пользовательских сценариев. В таблице приведены лицензии самих источников. Непосредственно адаптированный код перечислен отдельно выше.

| Проект и автор | Изученный срез | Роль | Лицензия источника |
| --- | --- | --- | --- |
| [trashee — onemorefix1337](https://github.com/onemorefix1337/trashee) | [`bb798e7`](https://github.com/onemorefix1337/trashee/tree/bb798e7a4c49f0985f33a80f31b0d27b9da03ec1) | Поведение клиента, тексты, воспроизведение и кэширование. | [MIT](https://github.com/onemorefix1337/trashee/blob/bb798e7a4c49f0985f33a80f31b0d27b9da03ec1/LICENSE). |
| [yandex-music-client — aeviww](https://github.com/aeviww/yandex-music-client) | [`a7a27b7`](https://github.com/aeviww/yandex-music-client/tree/a7a27b79e750047f225f5b6a499297cca84af2c7) | Воспроизведение, запросы API, поиск и организация состояния плеера. | [GPL-3.0](https://github.com/aeviww/yandex-music-client/blob/a7a27b79e750047f225f5b6a499297cca84af2c7/LICENSE). |
| [yandex-music-api — MarshalX](https://github.com/MarshalX/yandex-music-api) | [`9ca132e`](https://github.com/MarshalX/yandex-music-api/tree/9ca132e5c18a72aab779c1fc68c169380a9357d0) | Модели данных и описание методов неофициального API. | [LGPL-3.0](https://github.com/MarshalX/yandex-music-api/blob/9ca132e5c18a72aab779c1fc68c169380a9357d0/LICENSE). |
| [yandex-music-open-api — Alexander Cherkashin](https://github.com/acherkashin/yandex-music-open-api) | [`914ac07`](https://github.com/acherkashin/yandex-music-open-api/tree/914ac07f205d58d9fbd0c5d479f450cba5a1c0ad) | Схема и маршруты API, описание параметров запросов. | Apache-2.0, [указана автором в package.json](https://github.com/acherkashin/yandex-music-open-api/blob/914ac07f205d58d9fbd0c5d479f450cba5a1c0ad/package.json). |

Дополнительные справочные материалы по протоколу и авторизации: [llistochek/yandex-music-downloader](https://github.com/llistochek/yandex-music-downloader) ([MIT](https://github.com/llistochek/yandex-music-downloader/blob/main/LICENSE)) и [AlexxIT/YandexStation](https://github.com/AlexxIT/YandexStation) ([MIT](https://github.com/AlexxIT/YandexStation/blob/master/LICENSE.md)).

## Сервисы и документация

| Источник | Использование |
| --- | --- |
| [Яндекс Музыка](https://music.yandex.ru/) | Каталог, коллекция, рекомендации, воспроизведение, обложки и доступные тексты. |
| [Windows MediaPlayer](https://learn.microsoft.com/uwp/api/windows.media.playback.mediaplayer) и [Media Foundation Source Reader](https://learn.microsoft.com/windows/win32/medfound/processing-media-data-with-the-source-reader) | Системное воспроизведение потоков и декодирование аудио для локального измерения громкости. Используются через API Windows; дополнительные декодеры не поставляются. |
| [ITU-R BS.1770](https://www.itu.int/rec/R-REC-BS.1770/en) | Описание K-взвешивания и порогового отбора блоков при оценке громкости; собственная реализация алгоритма на C#. |
| [Яндекс OAuth](https://yandex.ru/dev/id/doc/en/codes/screen-code-oauth) | Документация входа через код на странице авторизации. |
| [LRCLIB](https://lrclib.net/docs) | Дополнительный источник текста и синхронных таймкодов. Клиент написан на C# по документации сервиса. |
| [Genius](https://genius.com/) | Поиск страницы песни и дополнительный источник текста. |

## Авторство и права

Указание проекта как источника означает указанную в таблице роль: зависимость, адаптацию кода либо справочный материал. Лицензии сторонних проектов относятся к их соответствующим материалам.

Yamuse развивается независимо от Яндекса. Названия сервисов и товарные знаки, музыка, обложки и тексты принадлежат соответствующим правообладателям. Условия использования Яндекс Музыки опубликованы [на сайте сервиса](https://yandex.ru/legal/music_termsofuse/).

Полные уведомления сторонних компонентов: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) и [папка лицензий](tools/packaging/licenses/).
