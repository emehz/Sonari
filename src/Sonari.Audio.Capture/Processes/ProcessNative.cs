using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Sonari.Audio.Capture.Processes;

/// <summary>
/// P/Invoke mínimo para leer procesos sin pedir más permisos de los necesarios:
/// <list type="bullet">
/// <item><c>CreateToolhelp32Snapshot</c> da PID, PID del padre y nombre del ejecutable de todos los procesos de una vez,
/// incluidos los que no se pueden abrir (elevados o protegidos).</item>
/// <item><c>QueryFullProcessImageNameW</c> con <c>PROCESS_QUERY_LIMITED_INFORMATION</c> da la ruta completa; funciona
/// con casi todos los procesos de usuario sin ser administrador.</item>
/// </list>
/// Todos los handles se cierran en el mismo método que los abre.
/// </summary>
internal static class ProcessNative
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x00000102;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Todos los procesos del sistema en este instante.</summary>
    public static Dictionary<int, ProcessEntry> TakeSnapshot()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new Dictionary<int, ProcessEntry>();
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                int pid = (int)entry.th32ProcessID;
                result[pid] = new ProcessEntry(pid, (int)entry.th32ParentProcessID, entry.szExeFile);
            }
            while (Process32NextW(snapshot, ref entry));

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>Ruta completa del ejecutable, o <c>null</c> si el proceso no existe o no se puede consultar.</summary>
    public static string? TryGetExecutablePath(int processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// <c>true</c> si el proceso sigue vivo. Si no se puede abrir para comprobarlo (acceso denegado), se consulta la
    /// instantánea de procesos.
    /// </summary>
    public static bool IsRunning(int processId)
    {
        IntPtr process = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return TakeSnapshot().ContainsKey(processId);
        }

        try
        {
            return WaitForSingleObject(process, 0) == WaitTimeout;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}

/// <param name="ExeName">Solo el nombre del archivo (p. ej. «chrome.exe»), tal y como lo da Toolhelp.</param>
internal readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string ExeName);
