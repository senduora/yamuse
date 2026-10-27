# Проверки на отдельном неактивном рабочем столе

Это инструмент для доверенных локальных тестов; он не входит в приложение или установщик.
Runner собирается под .NET 10. SDK 10.0.403 закреплён в корневом global.json;
native-изоляция Program.cs сохранена. Wrapper выполняет incremental build и запускает
DLL выбранным dotnet host, поэтому локального распакованного SDK достаточно.

Из корня проекта выполняйте `./tools/Verify-Project.ps1`: скрипт собирает приложение,
привязывает тесты к той же DLL и вызывает runner. Для полного набора добавьте `-Full`.
Каждому запуску нужна новая `-RunName`; журналы находятся в `work/<RunName>/`.
Для локального SDK укажите `-DotnetPath <путь к dotnet.exe>` у Verify-Project.ps1;
тот же host передаётся runner. Глобальная установка не требуется.

Runner создаёт уникальный `WinSta0\YamuseChecks_GUID` через `CreateDesktopW` с маской
`0x87`, без права SWITCHDESKTOP. Он не переключает рабочий стол, не захватывает экран
и не отправляет физический ввод. Дочерний процесс создаётся приостановленным,
включается в собственный kill-on-close Job Object и только затем запускается.
Наследуются лишь NUL stdin и дескриптор журнала. По завершении проверяются остановка
дочернего процесса, нулевое число процессов в job и закрытие desktop handles.

Заблокированный Windows-сеанс или ошибка доступа прекращают проверку. Не расширяйте
права и не обходите защиту. Само наличие изолированного desktop не разрешает тестам
обращаться к реальному аккаунту, сети или изменять пользовательские данные.
Штатный `Verify-Project.ps1` не передаёт `--account` или `--online`.

Прямой вызов из PowerShell, если checks уже собраны:

```powershell
./tools/testing/private-desktop-runner/Invoke-PrivateDesktopChecks.ps1 `
  -Executable $checksExecutable `
  -Arguments @($resultsDirectory, '--only=Branding040:') `
  -WorkingDirectory $checksWorkingDirectory `
  -LogPath $combinedLogPath `
  -ReceiptPath $runnerReceiptPath
```

Коды завершения: child exit code, `124` при timeout, `125` при ошибке runner/cleanup.
Полная квитанция содержит хеши launcher и child DLL, desktop до/после и состояние job.
Прежние результаты относятся к исходной версии runner; новую сборку проверяют
квитанцией конкретного запуска.

Первичные источники: [CreateDesktopW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopw),
[STARTUPINFOW.lpDesktop](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow),
[Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).
