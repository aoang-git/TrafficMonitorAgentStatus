using AgentStatus;
using System.IO.Pipes;
using System.Runtime.InteropServices;

static class IpcSecurityTests
{
    [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(IntPtr handle, int kind, uint info,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr descriptor, uint revision, uint info, out IntPtr text, out uint length);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static void Run(Action<bool, string> check)
    {
        string name = "tmas-label-test-" + Guid.NewGuid().ToString("N");
        var pipes = new List<NamedPipeServerStream>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var pipe = UserPipe.Create(name, true);
                pipes.Add(pipe);
                uint error = GetSecurityInfo(pipe.SafePipeHandle.DangerousGetHandle(), 6, 0x10, out _, out _, out _, out _, out var descriptor);
                try
                {
                    if (error != 0) throw new Exception("Read label error " + error);
                    if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, 0x10, out var labelText, out _))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    try { check(Marshal.PtrToStringUni(labelText) == "S:(ML;;NW;;;ME)", "IPC instance retains medium integrity label"); }
                    finally { LocalFree(labelText); }
                }
                finally { LocalFree(descriptor); }
            }
            for (int i = 0; i < 24; i++)
            {
                int slot = i % pipes.Count; pipes[slot].Dispose();
                pipes[slot] = UserPipe.Create(name, true);
            }
            check(true, "medium IPC label supports pooled instance replacement without audit privilege");
        }
        finally { foreach (var pipe in pipes) pipe.Dispose(); }
    }
}
