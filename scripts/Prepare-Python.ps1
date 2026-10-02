<#
.SYNOPSIS
    Готовит встроенный Python для установщика в artifacts\python.

.DESCRIPTION
    Скачивает встраиваемый дистрибутив Python с python.org, сверяет его
    SHA-256 с закреплённым значением, распаковывает, настраивает python312._pth
    и ставит пакеты из engine\requirements.txt.

    Архив кэшируется в artifacts\cache: повторный запуск ничего не скачивает.
#>
param(
    # Python, у которого есть pip: им ставятся пакеты во встроенный дистрибутив.
    [string]$HostPython = 'python'
)

$ErrorActionPreference = 'Stop'

$PythonVersion = '3.12.10'
# SHA-256 архива python-3.12.10-embed-amd64.zip. Архив сверен с MD5 со страницы
# выпуска на python.org (fe8ef205f2e9c3ba44d0cf9954e1abd3). При смене версии
# Python значение обновляется вместе с ней; несовпадение останавливает сборку.
$PythonEmbedSha256 = '4ACBED6DD1C744B0376E3B1CF57CE906F9DC9E95E68824584C8099A63025A3C3'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cacheDirectory = Join-Path $repositoryRoot 'artifacts\cache'
$targetDirectory = Join-Path $repositoryRoot 'artifacts\python'
$requirementsPath = Join-Path $repositoryRoot 'engine\requirements.txt'

$archiveName = "python-$PythonVersion-embed-amd64.zip"
$archivePath = Join-Path $cacheDirectory $archiveName
$downloadUri = "https://www.python.org/ftp/python/$PythonVersion/$archiveName"

New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $archivePath)) {
    Write-Output "Загрузка $downloadUri"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $partialPath = "$archivePath.download"
    Invoke-WebRequest -Uri $downloadUri -OutFile $partialPath -UseBasicParsing
    Move-Item -LiteralPath $partialPath -Destination $archivePath -Force
}

$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($actualHash -ne $PythonEmbedSha256) {
    throw "SHA-256 архива $archiveName не совпадает с закреплённым.`n" +
          "  ожидалось: $PythonEmbedSha256`n  получено:  $actualHash`n" +
          "Если версия Python менялась намеренно, закрепите новое значение в $PSCommandPath."
}

$rootWithSeparator = $repositoryRoot.TrimEnd('\') + '\'
if (-not $targetDirectory.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Каталог встроенного Python вне репозитория: $targetDirectory"
}

if (Test-Path -LiteralPath $targetDirectory) {
    Remove-Item -LiteralPath $targetDirectory -Recurse -Force
}

Expand-Archive -LiteralPath $archivePath -DestinationPath $targetDirectory

# Встроенный Python игнорирует PYTHONPATH: пути поиска модулей задаёт только
# файл ._pth. В установленном приложении python\ и engine\ лежат рядом,
# поэтому пакет движка подключается как ..\engine.
$pthFile = Get-ChildItem -LiteralPath $targetDirectory -Filter 'python*._pth' | Select-Object -First 1
if (-not $pthFile) {
    throw "В дистрибутиве нет файла ._pth: $targetDirectory"
}

$zipName = [System.IO.Path]::GetFileNameWithoutExtension($pthFile.Name) + '.zip'
$pthLines = @(
    $zipName
    '.'
    'Lib\site-packages'
    '..\engine'
    'import site'
)
[System.IO.File]::WriteAllLines($pthFile.FullName, $pthLines, [System.Text.UTF8Encoding]::new($false))

$sitePackages = Join-Path $targetDirectory 'Lib\site-packages'
New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null

$requirements = @(Get-Content -LiteralPath $requirementsPath -Encoding UTF8 |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') })

if ($requirements.Count -gt 0) {
    $pythonMinor = ($PythonVersion -split '\.')[0..1] -join '.'
    # Колёса выбираются под встроенный интерпретатор, а не под тот, которым
    # запущен pip: только готовые сборки win_amd64 нужной версии Python.
    & $HostPython -m pip install `
        --disable-pip-version-check `
        --no-input `
        --target $sitePackages `
        --platform win_amd64 `
        --python-version $pythonMinor `
        --implementation cp `
        --only-binary=:all: `
        -r $requirementsPath
    if ($LASTEXITCODE -ne 0) {
        throw "pip install завершился с кодом $LASTEXITCODE."
    }
}

$embeddedPython = Join-Path $targetDirectory 'python.exe'
& $embeddedPython -I -X utf8 -c "import sys; print('Встроенный Python', sys.version)"
if ($LASTEXITCODE -ne 0) {
    throw "Встроенный Python не запускается: $embeddedPython"
}

Write-Output "Готово: $targetDirectory"
