using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DevProxyApp
{
    internal enum AntigravityModelOutcome { None, Streaming, RegionRejected, AccountRejected }

    internal sealed class AntigravityModelLog
    {
        private const int ReadLimit = 64 * 1024;
        private static readonly Regex Entry = new Regex(
            @"^(?:ERROR: logging before google\.Init: )?[IWEF](?<stamp>\d{4} \d{2}:\d{2}:\d{2}\.\d{6})\s+\d+\s+[\w.\-/]+\.go:\d+\]\s+(?<message>.*)$",
            RegexOptions.CultureInvariant);
        private static readonly Regex Response = new Regex(
            @"^URL: https://(?:daily-)?cloudcode-pa\.googleapis\.com/v1internal:streamGenerateContent(?:\?\S*)?\s+Trace:\s+\S+\s+ResponseID:\s+[^\s]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RegionReason = new Regex(
            @"(?i)""reason""\s*:\s*""UNSUPPORTED_LOCATION""",
            RegexOptions.CultureInvariant);

        private sealed class Position
        {
            internal DateTime Created;
            internal long Offset;
            internal bool SkipPartialLine;
        }

        private readonly Func<IEnumerable<string>> _files;
        private readonly DateTime _since;
        private readonly Dictionary<string, Position> _positions = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastEvent;
        internal DateTime LastEventUtc { get { return _lastEvent.ToUniversalTime(); } }

        internal AntigravityModelLog(Func<IEnumerable<string>> files, DateTime since)
        {
            _files = files;
            _since = since;
            foreach (string path in _files()) TryRead(path, true);
        }

        internal static AntigravityModelLog ForExecutable(string executable)
        {
            string name = Path.GetFileNameWithoutExtension(executable);
            if (name.Equals("agy", StringComparison.OrdinalIgnoreCase))
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".gemini", "antigravity-cli", "log");
                return new AntigravityModelLog(() => Latest(directory, "cli-*.log", false), DateTime.Now);
            }
            if (!AntigravityIntegration.IsIde(executable)) return null;

            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name, "logs");
            if (!name.Equals("Antigravity IDE", StringComparison.OrdinalIgnoreCase))
                return new AntigravityModelLog(() => DesktopFiles(logs), DateTime.Now);
            string ideBackend = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "antigravity", "log");
            return new AntigravityModelLog(() => DesktopFiles(logs).Concat(Latest(ideBackend, "cli-*.log", false)), DateTime.Now);
        }

        private static string[] Latest(string directory, string pattern, bool folders)
        {
            try
            {
                if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return new string[0];
                var paths = folders ? Directory.EnumerateDirectories(directory, pattern) : Directory.EnumerateFiles(directory, pattern);
                return paths.OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(4).ToArray();
            }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private static IEnumerable<string> DesktopFiles(string logs)
        {
            foreach (string path in Latest(logs, "language_server.log", false)) yield return path;
            foreach (string folder in Latest(logs, "20*", true))
                foreach (string path in Latest(folder, "ls-main.log", false)) yield return path;
        }

        internal AntigravityModelOutcome ReadNew()
        {
            var outcome = AntigravityModelOutcome.None;
            var paths = new HashSet<string>(_files(), StringComparer.OrdinalIgnoreCase);
            foreach (string removed in _positions.Keys.Where(path => !paths.Contains(path)).ToArray())
                _positions.Remove(removed);
            foreach (string path in paths)
            {
                var observed = TryRead(path, false);
                if (observed != AntigravityModelOutcome.None) outcome = observed;
            }
            return outcome;
        }

        private AntigravityModelOutcome TryRead(string path, bool baseline)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return AntigravityModelOutcome.None;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var created = File.GetCreationTimeUtc(path);
                    long length = file.Length;
                    Position position;
                    bool known = _positions.TryGetValue(path, out position);
                    bool truncated = known && position.Created == created && length < position.Offset;
                    if (!known || position.Created != created || truncated)
                    {
                        position = new Position { Created = created };
                        _positions[path] = position;
                        if (baseline || !truncated && created < _since.ToUniversalTime())
                        {
                            position.Offset = length;
                            if (length > 0)
                            {
                                file.Position = length - 1;
                                position.SkipPartialLine = file.ReadByte() != '\n';
                            }
                        }
                    }
                    return ReadFrom(file, position, length);
                }
            }
            catch (IOException) { return AntigravityModelOutcome.None; }
            catch (UnauthorizedAccessException) { return AntigravityModelOutcome.None; }
        }

        private AntigravityModelOutcome ReadFrom(FileStream file, Position position, long length)
        {
            if (length - position.Offset > ReadLimit)
            {
                position.Offset = length - ReadLimit;
                position.SkipPartialLine = true;
            }
            if (length == position.Offset) return AntigravityModelOutcome.None;
            file.Position = position.Offset;
            var bytes = new byte[(int)(length - position.Offset)];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }

            int start = 0;
            var outcome = AntigravityModelOutcome.None;
            for (int index = 0; index < count; index++)
            {
                if (bytes[index] != '\n') continue;
                if (position.SkipPartialLine) position.SkipPartialLine = false;
                else
                {
                    var line = Encoding.UTF8.GetString(bytes, start, index - start).TrimEnd('\r');
                    var observed = Parse(line);
                    if (observed != AntigravityModelOutcome.None) outcome = observed;
                }
                start = index + 1;
            }
            position.Offset += start;
            if (start == 0 && count == ReadLimit)
            {
                position.Offset += count;
                position.SkipPartialLine = true;
            }
            return outcome;
        }

        private AntigravityModelOutcome Parse(string line)
        {
            var match = Entry.Match(line);
            if (!match.Success) return AntigravityModelOutcome.None;
            var message = match.Groups["message"].Value;
            var outcome = Classify(message);
            if (outcome == AntigravityModelOutcome.None) return outcome;

            var stamp = match.Groups["stamp"].Value;
            DateTime at;
            int year = DateTime.Now.Year;
            if (!DateTime.TryParseExact(year.ToString(CultureInfo.InvariantCulture) + stamp,
                "yyyyMMdd HH:mm:ss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
                return AntigravityModelOutcome.None;
            if (at > DateTime.Now.AddDays(1)) at = at.AddYears(-1);
            if (at < _since || at <= _lastEvent || at > DateTime.Now.AddSeconds(15)) return AntigravityModelOutcome.None;
            _lastEvent = at;
            return outcome;
        }

        private static AntigravityModelOutcome Classify(string message)
        {
            if (message.IndexOf("account is not eligible for Antigravity", StringComparison.OrdinalIgnoreCase) >= 0 &&
                message.IndexOf("not currently available in your location", StringComparison.OrdinalIgnoreCase) >= 0)
                return AntigravityModelOutcome.AccountRejected;
            if (message.IndexOf("FAILED_PRECONDITION (code 400): User location is not supported for the API use", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Your location is not eligible for this service at this time. Detected location:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (message.IndexOf("403", StringComparison.Ordinal) >= 0 && RegionReason.IsMatch(message)))
                return AntigravityModelOutcome.RegionRejected;
            return Response.IsMatch(message) ? AntigravityModelOutcome.Streaming : AntigravityModelOutcome.None;
        }
    }

    internal sealed class AntigravityRouteHistory
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

        private sealed class Entry
        {
            internal AiRoute Route;
            internal AntigravityModelOutcome Outcome;
            internal DateTime At;
            internal IPAddress[] SmartDnsAddresses;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        internal void Record(AiRoute route, AntigravityModelOutcome outcome, DateTime at, IEnumerable<IPAddress> smartDnsAddresses)
        {
            if ((route != null && route.PublicProxy) ||
                (outcome != AntigravityModelOutcome.RegionRejected && outcome != AntigravityModelOutcome.Streaming)) return;
            var addresses = smartDnsAddresses.ToArray();
            if (route == null && addresses.Length == 0) return;
            if (_entries.Exists(entry => SameTransport(route, entry.Route) && entry.At > at)) return;
            _entries.RemoveAll(entry => SameTransport(route, entry.Route));
            _entries.Add(new Entry { Route = route, Outcome = outcome, At = at, SmartDnsAddresses = addresses });
            if (_entries.Count > 24) _entries.RemoveAt(0);
        }

        internal bool HasRecentRejection(DateTime now)
        {
            return _entries.Exists(entry => RejectedRecently(entry, now));
        }

        internal HashSet<IPAddress> RefusedSmartDnsAddresses(DateTime now)
        {
            var refused = new HashSet<IPAddress>();
            var seen = new HashSet<IPAddress>();
            foreach (var entry in _entries.OrderByDescending(entry => entry.At))
                foreach (var address in entry.SmartDnsAddresses)
                    if (seen.Add(address) && RejectedRecently(entry, now)) refused.Add(address);
            return refused;
        }

        private static bool RejectedRecently(Entry entry, DateTime now)
        {
            return entry.Outcome == AntigravityModelOutcome.RegionRejected && now - entry.At < RetryDelay;
        }

        // Called on the UI thread. The asynchronous selector gets its own list.
        internal List<AiRoute> Prioritize(List<AiRoute> routes, AiRoute refused, DateTime now, List<string> report)
        {
            var preferred = new List<AiRoute>();
            var remaining = new List<AiRoute>();
            foreach (var route in routes)
            {
                var recent = _entries.Find(entry => SameTransport(route, entry.Route));
                if (SameTransport(route, refused) || (recent != null && RejectedRecently(recent, now)))
                {
                    report.Add(route.Name + " — недавний региональный отказ Antigravity");
                }
                else if (recent != null && recent.Outcome == AntigravityModelOutcome.Streaming &&
                    now - recent.At < TimeSpan.FromMinutes(30)) preferred.Add(route);
                else remaining.Add(route);
            }
            preferred.AddRange(remaining);
            return preferred;
        }

        private static bool SameTransport(AiRoute a, AiRoute b)
        {
            if (a == null || b == null) return false;
            string proxyA = a.Proxy == "direct" ? null : a.Proxy;
            string proxyB = b.Proxy == "direct" ? null : b.Proxy;
            return proxyA == proxyB && (ManagedAccess.IsProfile(proxyA) || a.Dns == b.Dns);
        }
    }
}
