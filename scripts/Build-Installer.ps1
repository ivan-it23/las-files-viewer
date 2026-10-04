<#
.SYNOPSIS
    Собирает установщик «Просмотрщика LAS» и файл описания обновления.

.DESCRIPTION
    1. Сверяет версию приложения и движка.
    2. Прогоняет тесты движка.
    3. Публикует приложение в artifacts\publish\win-x64.
    4. Кладёт рядом пакет движка и встроенный Python (scripts\Prepare-Python.ps1).
    5. Проверяет, что опубликованное приложение поднимает встроенный движок.
    6. Собирает установщик Inno Setup и las-viewer-version.txt.

    Установщик и las-viewer-version.txt выкладываются в каталог обновлений на
    хостинге: ivanbohg.beget.tech/public_html/FilesForProgramVisualisation
    (http://ivanbohg.beget.tech/FilesForProgramVisualisation/), рядом с
    файлами обновлений других приложений.
#>
param(
    [switch]$SkipPublish,
    [string]$HostPython = 'python'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'LasViewer\LasViewer.csproj'
$smokeProjectPath = Join-Path $repositoryRoot 'tests\AMK.LasViewer.Engine.Smoke\AMK.LasViewer.Engine.Smoke.csproj'
$engineSource = Join-Path $repositoryRoot 'engine'
$embeddedPythonSource = Join-Path $repositoryRoot 'artifacts\python'
$buildDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\build\win-x64'))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\publish\win-x64'))
$setupScript = Join-Path $repositoryRoot 'installer\setup.iss'
$feedFileName = 'las-viewer-version.txt'

$compilerPath = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compilerPath) {
    throw 'Inno Setup 6 не найден. Установите Inno Setup и повторите.'
}

# --- 1. Версия ---------------------------------------------------------------
[xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$version = [string]($project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'В LasViewer.csproj нет свойства Version.'
}

$engineInit = Get-Content -LiteralPath (Join-Path $engineSource 'lasviewer_engine\__init__.py') -Raw -Encoding UTF8
if ($engineInit -notmatch '__version__\s*=\s*"([^"]+)"') {
    throw 'В engine\lasviewer_engine\__init__.py нет __version__.'
}
if ($Matches[1] -ne $version) {
    throw "Версия движка $($Matches[1]) не совпадает с версией приложения $version. " +
          'Обновите __version__ в engine\lasviewer_engine\__init__.py.'
}

if (-not (Test-Path -LiteralPath (Join-Path $embeddedPythonSource 'python.exe'))) {
    throw "Встроенный Python не подготовлен: $embeddedPythonSource. Запустите scripts\Prepare-Python.ps1."
}

# --- 2. Тесты движка ---------------------------------------------------------
& $HostPython -m unittest discover -s (Join-Path $engineSource 'tests')
if ($LASTEXITCODE -ne 0) {
    throw "Тесты движка не пройдены (код $LASTEXITCODE)."
}

# --- 3. Публикация -----------------------------------------------------------
if (-not $SkipPublish) {
    $rootWithSeparator = $repositoryRoot.TrimEnd('\') + '\'
    foreach ($directory in @($buildDirectory, $publishDirectory)) {
        if (-not $directory.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Каталог сборки вне репозитория: $directory"
        }

        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    & dotnet publish $projectPath `
        -c Release `
        -p:Platform=x64 `
        "-p:OutDir=$buildDirectory\" `
        -r win-x64 `
        --self-contained true `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish завершился с кодом $LASTEXITCODE."
    }

    # Компилятор WinUI кладёт PRI и скомпилированную разметку (XBF) в OutDir,
    # а `dotnet publish -o` их не копирует. Без них установленное приложение
    # падает в InitializeComponent с XamlParseException.
    $compiledXamlFiles = @(Get-ChildItem -LiteralPath $buildDirectory -Recurse -File -Filter '*.xbf')
    if ($compiledXamlFiles.Count -eq 0) {
        throw "Скомпилированная разметка WinUI не найдена: $buildDirectory"
    }

    foreach ($sourceFile in $compiledXamlFiles) {
        $relativePath = $sourceFile.FullName.Substring($buildDirectory.Length).TrimStart('\')
        $destinationPath = Join-Path $publishDirectory $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath -Force
    }

    Copy-Item `
        -LiteralPath (Join-Path $buildDirectory 'LasViewer.pri') `
        -Destination (Join-Path $publishDirectory 'LasViewer.pri') `
        -Force
}

# --- 4. Движок и встроенный Python -------------------------------------------
$publishedEngine = Join-Path $publishDirectory 'engine'
$publishedPython = Join-Path $publishDirectory 'python'
foreach ($directory in @($publishedEngine, $publishedPython)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

# В установку идёт только пакет движка — без тестов и кэша байт-кода.
New-Item -ItemType Directory -Path $publishedEngine -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $engineSource 'lasviewer_engine') -Destination $publishedEngine -Recurse
Get-ChildItem -LiteralPath $publishedEngine -Recurse -Directory -Filter '__pycache__' |
    Remove-Item -Recurse -Force
Copy-Item -LiteralPath $embeddedPythonSource -Destination $publishedPython -Recurse

$bundledPython = Join-Path $publishedPython 'python.exe'

# Байт-код компилируется заранее: в Program Files движок его записать не
# сможет, а без него каждый запуск тратит время на компиляцию.
& $bundledPython -I -m compileall -q $publishedEngine
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось скомпилировать пакет движка (код $LASTEXITCODE)."
}

# --- 5. Проверка опубликованного приложения ---------------------------------
$selfCheckJson = & $bundledPython -I -X utf8 -m lasviewer_engine --self-check
if ($LASTEXITCODE -ne 0) {
    throw "Встроенный движок не проходит самопроверку (код $LASTEXITCODE)."
}

$selfCheck = ($selfCheckJson -join "`n") | ConvertFrom-Json
if (-not $selfCheck.python.embedded) {
    throw "Самопроверка запущена не встроенным Python: $($selfCheck.python.executable)"
}
if ($selfCheck.engine_version -ne $version) {
    throw "Опубликован движок версии $($selfCheck.engine_version), ожидалась $version."
}

# Проверка тем же путём, каким движок запускает приложение: поиск в каталоге
# установки, флаг -I, пути из python312._pth.
& dotnet run --project $smokeProjectPath -c Release -- $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Проверка связи с встроенным движком не пройдена (код $LASTEXITCODE)."
}

$requiredPublishedFiles = @(
    'LasViewer.exe'
    'LasViewer.dll'
    'LasViewer.runtimeconfig.json'
    'LasViewer.pri'
    'App.xbf'
    'MainWindow.xbf'
    'AMK.LasViewer.Engine.dll'
    'Microsoft.ui.xaml.dll'
    'Microsoft.WindowsAppRuntime.Bootstrap.dll'
    'Assets\LasViewer.ico'
    'engine\lasviewer_engine\__main__.py'
    'python\python.exe'
)

foreach ($relativePath in $requiredPublishedFiles) {
    $publishedPath = Join-Path $publishDirectory $relativePath
    if (-not (Test-Path -LiteralPath $publishedPath -PathType Leaf)) {
        throw "В публикации нет обязательного файла: $publishedPath"
    }
}

# --- 6. Установщик и описание обновления ------------------------------------
& $compilerPath "/DMyAppVersion=$version" $setupScript
if ($LASTEXITCODE -ne 0) {
    throw "ISCC завершился с кодом $LASTEXITCODE."
}

$installerPath = Join-Path $repositoryRoot "installer\Output\LasViewer-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Установщик не создан: $installerPath"
}

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
$feedPath = Join-Path (Split-Path -Parent $installerPath) $feedFileName
$feedContents = @(
    $version
    [System.IO.Path]::GetFileName($installerPath)
    $hash
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText($feedPath, $feedContents + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

Write-Output ''
Write-Output "Установщик: $installerPath"
Write-Output "Описание:   $feedPath"
Write-Output "Версия:     $version"
Write-Output "SHA-256:    $hash"
Write-Output 'Выложите оба файла в ivanbohg.beget.tech/public_html/FilesForProgramVisualisation'
