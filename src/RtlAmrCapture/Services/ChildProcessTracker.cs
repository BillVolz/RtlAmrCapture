using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RtlAmrCapture.Services
{
    /// <summary>
    /// Wraps a Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Any process
    /// added via <see cref="AddProcess"/> is terminated by Windows the instant this process ends,
    /// no matter how it ends: normal shutdown, an unhandled exception, Environment.Exit, or being
    /// killed externally.
    ///
    /// This exists because rtlamr.exe was previously spawned with no lifetime tie to the parent
    /// service. When the hang watchdog cancelled a capture attempt, or the service exited via
    /// Environment.Exit, rtlamr.exe kept running as an orphan, still holding its connection to
    /// rtl_tcp open. Because rtl_tcp fans the same sample stream out to every connected client,
    /// each orphan left over from a failed run competed with the next run's rtlamr for a usable
    /// connection, which could itself cause the new run to receive no data and trigger another
    /// restart, leaving another orphan behind. This is the OS-level guarantee that stops that
    /// from being possible: RunAndCaptureStdout also kills the process explicitly on cancellation
    /// as the fast path, but a Job Object is what protects against every other way to exit that
    /// explicit cleanup code might not run.
    /// </summary>
    public sealed class ChildProcessTracker : IDisposable
    {
        private IntPtr _jobHandle;

        public ChildProcessTracker()
        {
            _jobHandle = CreateJobObject(IntPtr.Zero, null);

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            };

            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info
            };

            var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var extendedInfoPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

                if (!SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation,
                        extendedInfoPtr, (uint)length))
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(extendedInfoPtr);
            }
        }

        /// <summary>
        /// Adds a process to the job so Windows kills it if this process ends for any reason.
        /// Safe to call even if the process has already exited.
        /// </summary>
        public void AddProcess(IntPtr processHandle)
        {
            // A process can only belong to one job object on Windows versions before the
            // nested-jobs support added in 1607 (mid-2016), so this can fail if rtlamr.exe was
            // itself launched inside another job (for example under a debugger, or under some
            // CI/orchestration wrappers). That is not fatal here: the explicit process.Kill()
            // call in RunAndCaptureStdout's cancellation path still applies, this is a second
            // layer, not the only one.
            AssignProcessToJobObject(_jobHandle, processHandle);
        }

        public void Dispose()
        {
            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }

        #region P/Invoke

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        #endregion
    }
}
