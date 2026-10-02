using System.ComponentModel;

namespace AMK.LasViewer.App.Services;

/// <summary>
/// Сценарий обновления: проверка, вопрос пользователю, загрузка, запуск
/// установщика и закрытие приложения. Живёт отдельно от окна, чтобы окно
/// отвечало только за отображение.
/// </summary>
internal sealed class UpdateFlow(
    AutoUpdateService service,
    DialogService dialogs,
    Action<string> setStatus,
    Action closeForUpdate)
{
    private bool inProgress;

    /// <param name="silent">
    /// Фоновая проверка при запуске: сообщает только о найденном обновлении,
    /// а отказ сети пишет в журнал, не беспокоя пользователя.
    /// </param>
    public async Task RunAsync(bool silent, CancellationToken cancellationToken)
    {
        if (inProgress)
        {
            return;
        }

        inProgress = true;
        try
        {
            if (!silent)
            {
                setStatus("Проверка обновлений...");
            }

            UpdateCheckResult update = await service.CheckForUpdateAsync(cancellationToken);
            if (!update.IsSuccessful)
            {
                AppLog.Write($"Проверка обновлений: {update.ErrorMessage}");
                if (!silent)
                {
                    await dialogs.ShowMessageAsync("Проверка обновлений", update.ErrorMessage);
                    setStatus("Не удалось проверить обновления");
                }

                return;
            }

            if (!update.IsAvailable)
            {
                if (!silent)
                {
                    await dialogs.ShowMessageAsync(
                        "Проверка обновлений",
                        $"Установлена последняя версия ({AppInfo.Format(update.CurrentVersion)}).");
                    setStatus("Установлена последняя версия");
                }

                return;
            }

            string remoteVersion = AppInfo.Format(update.RemoteVersion);
            string? copyMismatch = AutoUpdateService.DescribeCopyMismatch();
            string prompt = $"Доступна версия {remoteVersion}.\nТекущая версия: {AppInfo.Format(update.CurrentVersion)}.";
            prompt += copyMismatch is null
                ? "\n\nСкачать и установить обновление?"
                : $"\n\n{copyMismatch}\n\nВсё равно скачать и установить обновление?";

            bool accepted = await dialogs.ConfirmAsync(
                "Обновление доступно",
                prompt,
                "Скачать и установить",
                "Позже",
                confirmIsDefault: copyMismatch is null);
            if (!accepted)
            {
                setStatus("Обновление отложено");
                return;
            }

            setStatus($"Загрузка обновления {remoteVersion}...");
            string installerPath = await service.DownloadInstallerAsync(update, cancellationToken);
            if (!AutoUpdateService.StartInstaller(installerPath, out string startError))
            {
                await dialogs.ShowMessageAsync("Ошибка обновления", startError);
                setStatus("Не удалось запустить установщик обновления");
                return;
            }

            AppLog.Write($"Запущен установщик обновления {remoteVersion}");
            closeForUpdate();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
                                               or InvalidDataException
                                               or HttpRequestException
                                               or UnauthorizedAccessException
                                               or Win32Exception)
        {
            AppLog.Write($"Ошибка обновления: {exception}");
            await dialogs.ShowMessageAsync("Ошибка обновления", exception.Message);
            setStatus("Ошибка обновления");
        }
        finally
        {
            inProgress = false;
        }
    }
}
