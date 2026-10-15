import sys
import unittest
import subprocess
from devproxy_pkg.core.windows_job import WindowsJobManager


class TestWindowsJob(unittest.TestCase):
    def test_job_manager_lifecycle(self):
        if sys.platform != "win32":
            with self.assertRaises(OSError):
                WindowsJobManager()
            return

        with WindowsJobManager() as job:
            self.assertIsNotNone(job.job_handle)

            # Spawn dummy Python process sleeping
            proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(5)"], stdout=subprocess.DEVNULL)
            try:
                assigned = job.assign_process(proc._handle)
                self.assertTrue(assigned)

                resumed = job.resume_all_threads(proc.pid)
                self.assertIsInstance(resumed, int)
            finally:
                proc.kill()
                proc.wait()


if __name__ == "__main__":
    unittest.main()
