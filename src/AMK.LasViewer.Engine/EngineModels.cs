namespace AMK.LasViewer.Engine;

/// <summary>Ответ движка на рукопожатие <c>system.hello</c>.</summary>
public sealed record EngineInfo(
    int ProtocolVersion,
    string EngineVersion,
    int Pid,
    PythonInfo Python,
    IReadOnlyDictionary<string, string?> Packages);

public sealed record PythonInfo(
    string Version,
    string Implementation,
    string Executable,
    bool Embedded,
    int Bits);

public enum EngineState
{
    NotStarted,
    Starting,
    Ready,
    Failed,
    Stopped
}

public enum EngineLaunchKind
{
    /// <summary>Встроенный Python из каталога установки.</summary>
    Bundled,

    /// <summary>Python разработчика и исходники движка из репозитория.</summary>
    Developer
}

/// <summary>Чем и откуда запускается движок.</summary>
public sealed record EngineLaunchSpec(
    EngineLaunchKind Kind,
    string PythonExecutable,
    string EngineRoot)
{
    public string Describe()
    {
        return Kind == EngineLaunchKind.Bundled
            ? "встроенный Python"
            : $"Python разработчика ({PythonExecutable})";
    }
}

public sealed class EngineStateChangedEventArgs(EngineState state, string? error) : EventArgs
{
    public EngineState State { get; } = state;

    /// <summary>Причина для состояния <see cref="EngineState.Failed"/>.</summary>
    public string? Error { get; } = error;
}
