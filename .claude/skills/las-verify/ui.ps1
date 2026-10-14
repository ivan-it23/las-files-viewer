# Проверка «Просмотрщика LAS» в окне: запуск, нажатия через UI Automation,
# снимок только окна программы, вопрос агенту. Описание — SKILL.md рядом.
param(
    [Parameter(Mandatory = $true)][ValidateSet('start', 'click', 'shot', 'ask', 'approve', 'read', 'stop')][string]$Action,
    [Parameter(Mandatory = $true)][string]$Work,
    [string]$Build,
    [string[]]$Files = @(),
    [string]$Name,
    [string]$Out = 'shot',
    [string]$Question,
    [int]$Seconds = 180
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class LasVerifyWin {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$pidFile = Join-Path $Work 'pid.txt'
$auto = [System.Windows.Automation.AutomationElement]

function Get-AppWindow {
    $processId = [int](Get-Content $pidFile)
    $condition = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $processId)
    $window = $auto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    if ($null -eq $window) { throw "Окно процесса $processId не найдено." }
    return $window
}

function Find-ByName($window, [string]$elementName) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, $elementName)
    $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "Элемент «$elementName» не найден." }
    return $element
}

switch ($Action) {
    'start' {
        $env:LASVIEWER_PYTHON = (Get-Command python).Source
        $env:LASVIEWER_AGENT_SETTINGS = Join-Path $Work 'agent-settings-test.json'
        Remove-Item $env:LASVIEWER_AGENT_SETTINGS -ErrorAction SilentlyContinue
        $arguments = @($Files | ForEach-Object { '"' + $_ + '"' })
        $process = if ($arguments.Count) {
            Start-Process (Join-Path $Build 'LasViewer.exe') -ArgumentList $arguments -PassThru
        } else {
            Start-Process (Join-Path $Build 'LasViewer.exe') -PassThru
        }
        $process.Id | Set-Content $pidFile
        Start-Sleep -Seconds 12
    }
    'click' {
        $element = Find-ByName (Get-AppWindow) $Name
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 800
    }
    'shot' {
        # Только окно программы: снимок экрана захватил бы чужие окна пользователя.
        $handle = [IntPtr](Get-AppWindow).Current.NativeWindowHandle
        $rect = New-Object LasVerifyWin+RECT
        [LasVerifyWin]::GetWindowRect($handle, [ref]$rect) | Out-Null
        $bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        [LasVerifyWin]::PrintWindow($handle, $dc, 2) | Out-Null
        $graphics.ReleaseHdc($dc)
        $bitmap.Save((Join-Path $Work "$Out.png"))
        $graphics.Dispose(); $bitmap.Dispose()
    }
    'ask' {
        $window = Get-AppWindow
        $box = Find-ByName $window 'Вопрос агенту'
        $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Question)
        $send = Find-ByName $window 'Отправить или остановить'
        $send.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    'approve' {
        # Ждёт окна «Запустить скрипт?», ставит «без вопроса до конца диалога» и
        # нажимает «Запустить». Дальше скрипты этого диалога идут без вопроса.
        $deadline = (Get-Date).AddSeconds($Seconds)
        while ((Get-Date) -lt $deadline) {
            $window = Get-AppWindow
            $condition = New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, 'Запустить')
            $run = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $run) {
                $trustCondition = New-Object System.Windows.Automation.PropertyCondition(
                    $auto::NameProperty, 'Запускать скрипты без вопроса до конца этого диалога с агентом')
                $trust = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $trustCondition)
                if ($null -ne $trust) {
                    $toggle = $trust.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
                }
                $run.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                'Скрипт разрешён'
                return
            }
            Start-Sleep -Seconds 2
        }
        'Окно подтверждения не появилось'
    }
    'read' {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        $texts = (Get-AppWindow).FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        $texts | ForEach-Object { $_.Current.Name } | Out-File (Join-Path $Work 'answer.txt') -Encoding utf8
    }
    'stop' {
        Stop-Process -Id ([int](Get-Content $pidFile)) -Force
    }
}
