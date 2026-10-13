using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace AMK.LasViewer.App.Services;

internal sealed record UpdateCheckResult(
    bool IsAvailable,
    Version CurrentVersion,
    Version RemoteVersion,
    string InstallerFileName,
    Uri? DownloadUri,
    string? Sha256,
    string ErrorMessage)
{
    public bool IsSuccessful => string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>
/// Проверка, загрузка и запуск обновления. Устроена как в «Визуализации данных».
/// </summary>
/// <remarks>
/// Обновления лежат рядом с посредником агента на amk-horizon.ru и берутся по
/// HTTPS: подлинность файлов обеспечивает сертификат сайта. Контрольная сумма
/// SHA-256 из описания защищает от недокачанного или повреждённого файла.
/// </remarks>
internal sealed class AutoUpdateService : IDisposable
{
    /// <summary>
    /// Каталог обновлений на сайте компании (<c>amk-horizon.ru/public_html/las-viewer</c>,
    /// рядом с <c>LlmController.php</c>). Туда выкладываются установщик и
    /// <see cref="DescriptorFileName"/>, которые создаёт <c>scripts\Build-Installer.ps1</c>.
    /// До 0.4.3 каталогом был <c>http://ivanbohg.beget.tech/FilesForProgramVisualisation/</c>.
    /// </summary>
    public static readonly Uri UpdateRoot = new("https://amk-horizon.ru/las-viewer/", UriKind.Absolute);

    public const string DescriptorFileName = "las-viewer-version.txt";

    private const long MaximumInstallerSize = 1024L * 1024L * 1024L;

    /// <summary>Код Windows для «операция отменена пользователем» — отказ в UAC.</summary>
    private const int ElevationCancelledErrorCode = 1223;

    /// <summary>
    /// Ключ, который создаёт Inno Setup. Значение — AppId из
    /// <c>installer/setup.iss</c> с суффиксом <c>_is1</c>; установка для всех
    /// пользователей, поэтому ключ в HKLM.
    /// </summary>
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{F1CEB470-E4CA-4AB8-B88E-0580C248534B}_is1";

    private readonly HttpClient httpClient;

    public AutoUpdateService()
    {
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LasViewerUpdater", "1.0"));
        httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        httpClient.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        Version currentVersion = AppInfo.Version;

        try
        {
            string body = await httpClient.GetStringAsync(new Uri(UpdateRoot, DescriptorFileName), cancellationToken);
            string[] lines = body
                .TrimStart('﻿')
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 3 || !TryParseVersion(lines[0], out Version remoteVersion))
            {
                return Failed(currentVersion, $"Сервер обновлений вернул некорректный {DescriptorFileName}.");
            }

            string installerFileName = lines[1];
            if (!IsSafeInstallerFileName(installerFileName))
            {
                return Failed(currentVersion, "Сервер обновлений вернул недопустимое имя установщика.");
            }

            // В отличие от «Визуализации данных», сумма обязательна: она
            // единственное, что связывает установщик с опубликованным описанием.
            string? sha256 = NormalizeSha256(lines[2]);
            if (sha256 is null)
            {
                return Failed(currentVersion, "Сервер обновлений вернул некорректную контрольную сумму SHA-256.");
            }

            return new UpdateCheckResult(
                remoteVersion > currentVersion,
                currentVersion,
                remoteVersion,
                installerFileName,
                new Uri(UpdateRoot, Uri.EscapeDataString(installerFileName)),
                sha256,
                string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(currentVersion, "Сервер обновлений не ответил за отведённое время.");
        }
        catch (HttpRequestException exception)
        {
            return Failed(currentVersion, $"Не удалось подключиться к серверу обновлений: {exception.Message}");
        }
        catch (IOException exception)
        {
            return Failed(currentVersion, $"Не удалось прочитать данные обновления: {exception.Message}");
        }
    }

    public async Task<string> DownloadInstallerAsync(UpdateCheckResult update, CancellationToken cancellationToken)
    {
        if (!update.IsAvailable
            || update.DownloadUri is null
            || update.Sha256 is null
            || !IsSafeInstallerFileName(update.InstallerFileName))
        {
            throw new InvalidOperationException("Данные обновления не содержат допустимый установщик.");
        }

        string updateDirectory = Path.Combine(Path.GetTempPath(), "AMKLasViewer", "Updates");
        Directory.CreateDirectory(updateDirectory);
        DeleteStaleDownloads(updateDirectory);

        string finalPath = Path.Combine(updateDirectory, update.InstallerFileName);
        string temporaryPath = finalPath + ".download";
        TryDeleteFile(temporaryPath);

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(
                update.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is <= 1024 or > MaximumInstallerSize)
            {
                throw new InvalidDataException("Размер установщика обновления недопустим.");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (FileStream target = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyWithLimitAsync(source, target, MaximumInstallerSize, cancellationToken);
            }

            ValidatePortableExecutable(temporaryPath);
            await ValidateSha256Async(temporaryPath, update.Sha256, cancellationToken);

            File.Move(temporaryPath, finalPath, true);
            TryDeleteFile(finalPath + ":Zone.Identifier");
            return finalPath;
        }
        catch
        {
            // Убрать недокачанный файл нужно, но не ценой исходной причины:
            // исключение удаления не должно заменить собой настоящее.
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Запускает скачанный установщик. <c>/SUPPRESSMSGBOXES</c> не передаётся:
    /// приложение закрывается сразу после запуска, и с подавленными сообщениями
    /// неудачное обновление выглядело бы просто закрывшимся приложением.
    /// </summary>
    public static bool StartInstaller(string installerPath, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!File.Exists(installerPath))
        {
            errorMessage = "Скачанный установщик не найден.";
            return false;
        }

        try
        {
            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /UPDATE=1",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
            });
            if (process is not null)
            {
                return true;
            }

            errorMessage = "Не удалось запустить установщик обновления.";
            return false;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ElevationCancelledErrorCode)
        {
            errorMessage = "Установка обновления требует прав администратора, а запрос был отклонён. "
                + "Приложение продолжает работать в прежней версии.";
            return false;
        }
        catch (Win32Exception exception)
        {
            errorMessage = $"Не удалось запустить установщик обновления: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Объясняет, почему обновление не затронет запущенную копию, либо
    /// <c>null</c>, если затронет. Установщик обновляет установленную копию, а
    /// не ту, из которой запущен процесс.
    /// </summary>
    public static string? DescribeCopyMismatch()
    {
        string runningDirectory = AppContext.BaseDirectory;
        string? installedDirectory = GetInstalledLocation();
        if (installedDirectory is null)
        {
            return "Эта копия запущена из каталога\n"
                + $"{TrimDirectory(runningDirectory)}\n"
                + "и на этом компьютере установщиком не устанавливалась. Обновление установит "
                + "приложение отдельной копией, а эта папка останется со старой версией.";
        }

        if (DirectoriesMatch(runningDirectory, installedDirectory))
        {
            return null;
        }

        return "Эта копия запущена из каталога\n"
            + $"{TrimDirectory(runningDirectory)}\n"
            + "а установлено приложение в\n"
            + $"{TrimDirectory(installedDirectory)}\n"
            + "Обновление получит установленная копия, а запущенная сейчас останется прежней версии.";
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private static string? GetInstalledLocation()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location)
                ? location
                : null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return null;
        }
    }

    private static bool DirectoriesMatch(string first, string second)
    {
        try
        {
            return string.Equals(
                TrimDirectory(Path.GetFullPath(first)),
                TrimDirectory(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static string TrimDirectory(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static UpdateCheckResult Failed(Version currentVersion, string message)
    {
        return new UpdateCheckResult(false, currentVersion, new Version(0, 0, 0), string.Empty, null, null, message);
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        string normalized = value?.Trim().TrimStart('v', 'V') ?? string.Empty;
        if (Version.TryParse(normalized, out Version? parsed))
        {
            version = new Version(
                Math.Max(0, parsed.Major),
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build),
                Math.Max(0, parsed.Revision));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static bool IsSafeInstallerFileName(string fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static string? NormalizeSha256(string value)
    {
        string normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized.ToUpperInvariant() : null;
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream target, long maximumBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            int bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException("Установщик обновления превышает допустимый размер.");
            }

            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private static void ValidatePortableExecutable(string path)
    {
        if (new FileInfo(path).Length <= 1024)
        {
            throw new InvalidDataException("Скачанный установщик пуст или повреждён.");
        }

        using FileStream stream = File.OpenRead(path);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
        {
            throw new InvalidDataException("Скачанный файл не является Windows-установщиком.");
        }
    }

    private static async Task ValidateSha256Async(string path, string expectedHash, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        if (!string.Equals(Convert.ToHexString(actualHash), expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Контрольная сумма обновления не совпадает с опубликованной.");
        }
    }

    private static void DeleteStaleDownloads(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
