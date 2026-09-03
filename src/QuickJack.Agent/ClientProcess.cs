using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QuickJack.Agent;

/// <summary>Identifies which executable is on the other end of a named pipe.</summary>
internal static partial class ClientProcess
{
    private const int ProcessQueryLimitedInformation = 0x1000;

    public static string? ImagePathOf(NamedPipeServerStream server)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid))
                return null;

            using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process.IsInvalid) return null;

            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;

            return QueryFullProcessImageName(process, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : null;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, int flags, StringBuilder exeName, ref int size);
}
