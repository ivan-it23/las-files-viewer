namespace AMK.LasViewer.Agent;

/// <summary>Скрипт, который агент просит выполнить: показывается пользователю до запуска.</summary>
/// <param name="Title">Что делает скрипт — со слов модели.</param>
/// <param name="Code">Код Python целиком.</param>
/// <param name="FileNames">Имена файлов, данные которых получит скрипт; основной — первым.</param>
/// <param name="TimeoutSeconds">Сколько секунд скрипт может работать.</param>
public sealed record ScriptRequest(string Title, string Code, IReadOnlyList<string> FileNames, int TimeoutSeconds);

/// <summary>Спрашивает пользователя, можно ли выполнить скрипт агента.</summary>
/// <remarks>
/// Вызывается не из потока интерфейса. Отмена вопроса (кнопка «Стоп») —
/// <see cref="OperationCanceledException"/>, а не отказ.
/// </remarks>
public interface IScriptApprover
{
    Task<bool> ApproveAsync(ScriptRequest request, CancellationToken cancellationToken);
}
