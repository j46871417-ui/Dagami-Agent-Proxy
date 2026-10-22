"""Daily online backup + 90-day retention; run as the service account."""
import os
import sqlite3
import time
from contextlib import closing
from pathlib import Path


def maintain(database, now=None):
    now = int(time.time()) if now is None else now
    database = Path(database)
    backup_dir = database.parent/'backups'
    backup_dir.mkdir(mode=0o700, exist_ok=True)
    snapshot = backup_dir/(time.strftime('%Y%m%d', time.gmtime(now))+'.sqlite')
    if not snapshot.exists():
        partial = snapshot.with_suffix('.partial')
        with closing(sqlite3.connect(database, timeout=30)) as source, closing(sqlite3.connect(partial)) as target:
            source.backup(target)
        partial.replace(snapshot)
    with closing(sqlite3.connect(database, timeout=30)) as db:
        with db:
            db.execute('DELETE FROM reports WHERE created < ?', (now-90*86400,))
        db.execute('PRAGMA wal_checkpoint(TRUNCATE)')
    # Keep three days of private snapshots. No report information goes in filenames.
    for path in backup_dir.glob('*.sqlite'):
        if path.stat().st_mtime < now-3*86400:
            path.unlink()


if __name__=='__main__':
    maintain(os.environ.get('REPORTS_DB','/var/lib/dagami-reports/reports.sqlite'))
