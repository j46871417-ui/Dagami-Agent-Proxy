"""
core/windows_job.py
Win32 Job Object manager and thread lifecycle orchestrator.
Zero-Dependencies implementation using ctypes.
Ensures child IDE process trees are cleanly terminated on parent exit.
"""

import sys
import ctypes
from ctypes import wintypes
from typing import Optional

# WinAPI Constants
JOBOBJECT_EXTENDED_LIMIT_INFORMATION = 9
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
TH32CS_SNAPTHREAD = 0x00000004
THREAD_SUSPEND_RESUME = 0x0002
INVALID_HANDLE_VALUE = wintypes.HANDLE(-1).value


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("ReadOperationCount", ctypes.c_uint64),
        ("WriteOperationCount", ctypes.c_uint64),
        ("OtherOperationCount", ctypes.c_uint64),
        ("ReadTransferCount", ctypes.c_uint64),
        ("WriteTransferCount", ctypes.c_uint64),
        ("OtherTransferCount", ctypes.c_uint64),
    ]


class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", wintypes.LARGE_INTEGER),
        ("PerJobUserTimeLimit", wintypes.LARGE_INTEGER),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.POINTER(ctypes.c_ulong)),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class JOBOBJECT_EXTENDED_LIMIT_INFORMATION_STRUCT(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
        ("IoInfo", IO_COUNTERS),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryLimit", ctypes.c_size_t),
        ("PeakJobMemoryLimit", ctypes.c_size_t),
    ]


class THREADENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ThreadID", wintypes.DWORD),
        ("th32OwnerProcessID", wintypes.DWORD),
        ("tpBasePri", wintypes.LONG),
        ("tpDeltaPri", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
    ]


class WindowsJobManager:
    """
    Manages a Win32 Job Object bound to the devproxy lifecycle.
    Forces all descendant processes to terminate when the Job Object handle closes.
    """

    def __init__(self, job_name: Optional[str] = None):
        if sys.platform != "win32":
            raise OSError("WindowsJobManager is supported only on Windows systems.")

        self.k32 = ctypes.windll.kernel32
        self._setup_winapi_signatures()
        self.job_handle: Optional[wintypes.HANDLE] = None
        self._init_job(job_name)

    def _setup_winapi_signatures(self) -> None:
        self.k32.CreateJobObjectW.argtypes = [wintypes.LPVOID, wintypes.LPCWSTR]
        self.k32.CreateJobObjectW.restype = wintypes.HANDLE

        self.k32.SetInformationJobObject.argtypes = [
            wintypes.HANDLE,
            ctypes.c_int,
            wintypes.LPVOID,
            wintypes.DWORD,
        ]
        self.k32.SetInformationJobObject.restype = wintypes.BOOL

        self.k32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        self.k32.AssignProcessToJobObject.restype = wintypes.BOOL

        self.k32.CloseHandle.argtypes = [wintypes.HANDLE]
        self.k32.CloseHandle.restype = wintypes.BOOL

        self.k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
        self.k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE

        self.k32.Thread32First.argtypes = [wintypes.HANDLE, ctypes.POINTER(THREADENTRY32)]
        self.k32.Thread32First.restype = wintypes.BOOL

        self.k32.Thread32Next.argtypes = [wintypes.HANDLE, ctypes.POINTER(THREADENTRY32)]
        self.k32.Thread32Next.restype = wintypes.BOOL

        self.k32.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.k32.OpenThread.restype = wintypes.HANDLE

        self.k32.ResumeThread.argtypes = [wintypes.HANDLE]
        self.k32.ResumeThread.restype = wintypes.DWORD

    def _init_job(self, job_name: Optional[str]) -> None:
        self.job_handle = self.k32.CreateJobObjectW(None, job_name)
        if not self.job_handle:
            raise ctypes.WinError(ctypes.GetLastError())

        info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION_STRUCT()
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE

        success = self.k32.SetInformationJobObject(
            self.job_handle,
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
            ctypes.byref(info),
            ctypes.sizeof(info),
        )
        if not success:
            err = ctypes.GetLastError()
            self.close()
            raise ctypes.WinError(err)

    def assign_process(self, process_handle: int) -> bool:
        """Assigns target process handle to this job object."""
        if not self.job_handle:
            return False
        res = self.k32.AssignProcessToJobObject(self.job_handle, wintypes.HANDLE(process_handle))
        if not res:
            raise ctypes.WinError(ctypes.GetLastError())
        return True

    def resume_all_threads(self, pid: int) -> int:
        """
        Enumerates all threads of target PID and calls ResumeThread.
        Fixes the early-return bug from Toolhelp32 snapshots to prevent
        Electron/Chromium deadlocks during suspended process starts.
        """
        h_snapshot = self.k32.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
        if h_snapshot == INVALID_HANDLE_VALUE:
            raise ctypes.WinError(ctypes.GetLastError())

        resumed_count = 0
        entry = THREADENTRY32()
        entry.dwSize = ctypes.sizeof(THREADENTRY32)

        try:
            success = self.k32.Thread32First(h_snapshot, ctypes.byref(entry))
            while success:
                if entry.th32OwnerProcessID == pid:
                    h_thread = self.k32.OpenThread(THREAD_SUSPEND_RESUME, False, entry.th32ThreadID)
                    if h_thread:
                        prev_suspend_count = self.k32.ResumeThread(h_thread)
                        self.k32.CloseHandle(h_thread)
                        if prev_suspend_count != 0xFFFFFFFF:
                            resumed_count += 1
                success = self.k32.Thread32Next(h_snapshot, ctypes.byref(entry))
        finally:
            self.k32.CloseHandle(h_snapshot)

        return resumed_count

    def close(self) -> None:
        """Closes job handle, triggering kill of assigned processes if still running."""
        if self.job_handle:
            self.k32.CloseHandle(self.job_handle)
            self.job_handle = None

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()
