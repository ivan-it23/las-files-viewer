using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AMK.LasViewer.App.Services;

/// <summary>
/// Единая точка вызова системных диалогов выбора файлов (как в «Визуализации данных»).
/// </summary>
/// <remarks>
/// Привязывает диалог к окну — без этого неупакованное приложение его не
/// откроет — и не даёт отказу пройти незамеченным. Отказ реален: в процессе с
/// правами администратора <c>Windows.Storage.Pickers</c> завершаются
/// <c>E_FAIL</c>, а из <c>async void</c> обработчика меню исключение ушло бы
/// мимо окна, и пункт меню выглядел бы просто не работающим.
/// </remarks>
internal static class FilePickers
{
    public static async Task<IReadOnlyList<StorageFile>> PickMultipleFilesAsync(
        FileOpenPicker picker,
        Window owner,
        DialogService dialogs)
    {
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        try
        {
            return await picker.PickMultipleFilesAsync();
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write($"Диалог выбора файлов не открылся: {exception}");
            string message = AppInfo.IsElevatedProcess
                ? "Приложение запущено с правами администратора, а в таком режиме Windows не открывает "
                  + "системный диалог выбора файлов.\n\nЗакройте приложение и запустите его обычным "
                  + "способом — с ярлыка на рабочем столе или из меню «Пуск»."
                : $"Windows не открыл системный диалог выбора файлов.\n\n{exception.Message}";
            await dialogs.ShowMessageAsync("Диалог выбора недоступен", message);
            return [];
        }
    }
}
