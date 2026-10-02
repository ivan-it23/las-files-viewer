namespace AMK.LasViewer.Engine;

/// <summary>
/// Находит интерпретатор Python и пакет движка.
/// </summary>
/// <remarks>
/// Установленное приложение несёт встроенный Python в <c>python\</c> и пакет
/// движка в <c>engine\</c> рядом с исполняемым файлом. Путь к пакету прописан во
/// встроенном <c>python312._pth</c>, поэтому PYTHONPATH там не нужен и не
/// работает.
///
/// При запуске из репозитория встроенного Python нет: берётся
/// <c>LASVIEWER_PYTHON</c>, затем <c>.venv</c> репозитория, затем
/// <c>python.exe</c> из PATH, а пакет — из каталога <c>engine\</c> репозитория.
/// </remarks>
public static class EngineLocator
{
    public const string PythonOverrideVariable = "LASVIEWER_PYTHON";

    private const string PackageMarker = @"lasviewer_engine\__main__.py";

    public static EngineLaunchSpec Locate(string applicationDirectory)
    {
        string? pythonOverride = Environment.GetEnvironmentVariable(PythonOverrideVariable);

        string bundledPython = Path.Combine(applicationDirectory, "python", "python.exe");
        string bundledEngine = Path.Combine(applicationDirectory, "engine");
        if (File.Exists(Path.Combine(bundledEngine, PackageMarker)))
        {
            if (!string.IsNullOrWhiteSpace(pythonOverride))
            {
                return new EngineLaunchSpec(EngineLaunchKind.Developer, RequireFile(pythonOverride), bundledEngine);
            }

            if (File.Exists(bundledPython))
            {
                return new EngineLaunchSpec(EngineLaunchKind.Bundled, bundledPython, bundledEngine);
            }

            throw new EngineUnavailableException(
                $"Пакет движка найден в {bundledEngine}, но встроенного Python нет: {bundledPython}. "
                + "Установка повреждена — переустановите приложение.");
        }

        string? repositoryEngine = FindRepositoryEngine(applicationDirectory);
        if (repositoryEngine is null)
        {
            throw new EngineUnavailableException(
                "Движок не найден: нет ни каталога engine рядом с приложением, "
                + $"ни репозитория выше {applicationDirectory}.");
        }

        string? python = !string.IsNullOrWhiteSpace(pythonOverride)
            ? RequireFile(pythonOverride)
            : FindRepositoryVenvPython(repositoryEngine) ?? FindPythonOnPath();
        if (python is null)
        {
            throw new EngineUnavailableException(
                "Python для запуска движка из репозитория не найден. Установите Python 3.12, "
                + $"создайте .venv в корне репозитория или задайте переменную {PythonOverrideVariable}.");
        }

        return new EngineLaunchSpec(EngineLaunchKind.Developer, python, repositoryEngine);
    }

    private static string RequireFile(string path)
    {
        return File.Exists(path)
            ? path
            : throw new EngineUnavailableException($"{PythonOverrideVariable} указывает на несуществующий файл: {path}");
    }

    private static string? FindRepositoryEngine(string startDirectory)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "engine");
            if (File.Exists(Path.Combine(candidate, PackageMarker)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindRepositoryVenvPython(string engineRoot)
    {
        string? repositoryRoot = Path.GetDirectoryName(engineRoot);
        if (repositoryRoot is null)
        {
            return null;
        }

        string candidate = Path.Combine(repositoryRoot, ".venv", "Scripts", "python.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindPythonOnPath()
    {
        string windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // python.exe в WindowsApps — заглушка, которая открывает Microsoft Store
            // вместо запуска интерпретатора.
            if (directory.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string candidate = Path.Combine(directory, "python.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
