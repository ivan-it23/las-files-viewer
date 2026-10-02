using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AMK.LasViewer.Engine;

/// <summary>
/// Объект задания Windows, который завершает движок вместе с приложением.
/// </summary>
/// <remarks>
/// Движок сам выходит, когда приложение закрывает канал. Но если он занят
/// долгим расчётом или скриптом, до чтения канала он не дойдёт, и после
/// аварийного завершения приложения на машине остался бы висеть Python.
/// Задание с флагом «убить при закрытии» снимает этот случай: дескриптор
/// задания закрывает сама система при выходе процесса приложения, и все
/// процессы в задании, включая порождённые движком, завершаются.
/// </remarks>
internal static class EngineProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<SafeFileHandle> Job = new(Create);

    public static void Assign(Process process)
    {
        if (!AssignProcessToJobObject(Job.Value, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось привязать движок к заданию приложения.");
        }
    }

    private static SafeFileHandle Create()
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось создать задание для движка.");
        }

        JobObjectExtendedLimitInfo info = new()
        {
            BasicLimitInformation = new JobObjectBasicLimitInfo
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        int size = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                int error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "Не удалось настроить задание для движка.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInfo
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInfo BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
