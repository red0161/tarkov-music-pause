using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TarkovMusicPause
{
    internal sealed class LogWatcher
    {
        private static readonly Regex RaidWorldLiveRe = new Regex(
            @"\|Info\|application\|(?:Game(?:Started|Starting|Spawned|Runned)|PlayerSpawnEvent):",
            RegexOptions.Compiled);
        private static readonly Regex SelectProfileRe = new Regex(
            @"\|Info\|application\|SelectProfile\s", RegexOptions.Compiled);
        private static readonly Regex BackendProfileSelectRe = new Regex(
            @"client/game/profile/select", RegexOptions.Compiled);
        private static readonly Regex OutputStopGameRe = new Regex(
            @"Reason:Stop game", RegexOptions.Compiled);
        private static readonly Regex OutputDisconnectedRe = new Regex(
            @"\|Info\|output\|network-connection\|Enter to the 'Disconnected' state",
            RegexOptions.Compiled);

        public const string DefaultLogRoot = @"C:\Games\Logs";

        private readonly string _logRoot;
        private readonly bool _resumeAfterRaid;
        private readonly bool _dryRun;
        private readonly Action<string> _emit;
        private readonly Action _sendMedia;

        public LogWatcher(string logRoot, bool resumeAfterRaid, bool dryRun, Action<string> emit, Action sendMedia)
        {
            _logRoot = logRoot;
            _resumeAfterRaid = resumeAfterRaid;
            _dryRun = dryRun;
            _emit = emit;
            _sendMedia = sendMedia;
        }

        public void Run(CancellationToken ct)
        {
            _emit("Log root: " + _logRoot);
            _emit("  Exists: " + Directory.Exists(_logRoot));
            try
            {
                var subdirs = Directory.GetDirectories(_logRoot)
                    .OrderByDescending(d => Directory.GetLastWriteTime(d))
                    .ToArray();
                _emit(string.Format("  Subdirs found: {0}, newest: {1}",
                    subdirs.Length, subdirs.Length > 0 ? Path.GetFileName(subdirs[0]) : "(none)"));
                if (subdirs.Length > 0)
                {
                    try
                    {
                        var files = Directory.GetFiles(subdirs[0])
                            .Select(Path.GetFileName).OrderBy(n => n).ToArray();
                        _emit("  Files in newest session: " + (files.Length > 0 ? string.Join(", ", files) : "(empty)"));
                    }
                    catch (Exception ex2) { _emit("  Cannot list files: " + ex2.Message); }
                }
            }
            catch (Exception ex) { _emit("  Cannot list subdirs: " + ex.Message); }

            string activeSession = null;
            var filePos = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var partialLine = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            bool inRaidAudioPaused = false;
            bool waitingLogged = false;

            while (!ct.IsCancellationRequested)
            {
                var found = LatestSessionWatchFiles(_logRoot);
                if (found == null)
                {
                    if (!waitingLogged) { waitingLogged = true; _emit("Waiting for a Tarkov session..."); }
                    ct.WaitHandle.WaitOne(500);
                    continue;
                }
                waitingLogged = false;

                string sessionDir = found.Item1;
                string[] logPaths = found.Item2;
                var desired = new HashSet<string>(logPaths, StringComparer.OrdinalIgnoreCase);

                if (!string.Equals(activeSession, sessionDir, StringComparison.OrdinalIgnoreCase))
                {
                    activeSession = sessionDir;
                    filePos.Clear(); partialLine.Clear();
                    inRaidAudioPaused = false;
                    _emit("Watching session: " + sessionDir);
                    foreach (var p in logPaths) TailFromEof(p, "tail", filePos, partialLine);
                }
                else
                {
                    foreach (var key in filePos.Keys.ToList())
                        if (!desired.Contains(key)) { filePos.Remove(key); partialLine.Remove(key); }
                    foreach (var p in logPaths)
                        if (!filePos.ContainsKey(p)) TailFromEof(p, "+tail", filePos, partialLine);
                }

                bool anyData = false;
                foreach (var p in filePos.Keys.OrderBy(k => k).ToList())
                {
                    if (ct.IsCancellationRequested) break;
                    long size;
                    try { size = new FileInfo(p).Length; } catch { continue; }
                    long off = filePos[p];
                    if (size < off) { off = 0; filePos[p] = 0; partialLine[p] = new byte[0]; _emit("  log truncated (reset): " + Path.GetFileName(p)); }
                    if (size <= off) continue;

                    byte[] chunk;
                    try
                    {
                        using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            fs.Seek(off, SeekOrigin.Begin);
                            int toRead = (int)(size - off);
                            chunk = new byte[toRead];
                            int totalRead = 0;
                            while (totalRead < toRead)
                            {
                                int n = fs.Read(chunk, totalRead, toRead - totalRead);
                                if (n == 0) break;
                                totalRead += n;
                            }
                            if (totalRead < toRead) { var tmp = new byte[totalRead]; Buffer.BlockCopy(chunk, 0, tmp, 0, totalRead); chunk = tmp; }
                        }
                    }
                    catch (Exception ex) { _emit("  Read error " + Path.GetFileName(p) + ": " + ex.Message); continue; }

                    filePos[p] = off + chunk.Length;
                    anyData = true;

                    var existing = partialLine.ContainsKey(p) ? partialLine[p] : new byte[0];
                    var data = existing.Length > 0 ? Concat(existing, chunk) : chunk;
                    var parts = SplitOnNewline(data);
                    partialLine[p] = parts[parts.Length - 1];

                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        string line;
                        try { line = Encoding.UTF8.GetString(parts[i]).TrimEnd('\r'); }
                        catch { continue; }
                        ProcessLine(line, ref inRaidAudioPaused);
                    }
                }

                if (!anyData) ct.WaitHandle.WaitOne(500);
            }
        }

        private void TailFromEof(string path, string label, Dictionary<string, long> filePos, Dictionary<string, byte[]> partialLine)
        {
            try { filePos[path] = new FileInfo(path).Length; } catch { filePos[path] = 0; }
            partialLine[path] = new byte[0];
            _emit("  " + label + " " + Path.GetFileName(path));
        }

        private void ProcessLine(string line, ref bool inRaidAudioPaused)
        {
            if (RaidWorldLiveRe.IsMatch(line))
            {
                if (inRaidAudioPaused) return;
                inRaidAudioPaused = true;
                _emit("Raid live -> pause (media key): " + (line.Length > 80 ? line.Substring(0, 80) + "..." : line));
                if (!_dryRun) _sendMedia();
                return;
            }
            if (!_resumeAfterRaid || !inRaidAudioPaused) return;
            string label = null;
            if (OutputStopGameRe.IsMatch(line)) label = "Raid session ended (Stop game)";
            else if (OutputDisconnectedRe.IsMatch(line)) label = "Raid session ended (Disconnected)";
            else if (SelectProfileRe.IsMatch(line) || BackendProfileSelectRe.IsMatch(line)) label = "Back to menu";
            if (label == null) return;
            inRaidAudioPaused = false;
            _emit(label + " -> resume (media key): " + (line.Length > 80 ? line.Substring(0, 80) + "..." : line));
            if (!_dryRun) _sendMedia();
        }

        private static Tuple<string, string[]> LatestSessionWatchFiles(string logRoot)
        {
            if (!Directory.Exists(logRoot)) return null;
            var direct = SessionLogPaths(logRoot);
            if (direct != null) return Tuple.Create(Path.GetFullPath(logRoot), direct);

            string best = null;
            long bestAge = -1;
            foreach (var dir in Directory.GetDirectories(logRoot))
            {
                var paths = SessionLogPaths(dir);
                if (paths == null) continue;
                var appFiles = paths.Where(p => p.IndexOf("application", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                if (appFiles.Length == 0) continue;
                long newest = appFiles.Select(f => { try { return new FileInfo(f).LastWriteTime.ToFileTime(); } catch { return 0L; } }).Max();
                if (newest > bestAge) { bestAge = newest; best = dir; }
            }
            if (best == null) return null;
            var again = SessionLogPaths(best);
            return again == null ? null : Tuple.Create(Path.GetFullPath(best), again);
        }

        private static string[] SessionLogPaths(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            var app = Directory.GetFiles(dir, "*application*.log");
            if (app.Length == 0) return null;
            return app
                .Concat(Directory.GetFiles(dir, "*output*.log"))
                .Concat(Directory.GetFiles(dir, "*backend*.log"))
                .Concat(Directory.GetFiles(dir, "*network-connection*.log"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p).ToArray();
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private static byte[][] SplitOnNewline(byte[] data)
        {
            var parts = new List<byte[]>();
            int start = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == (byte)'\n')
                {
                    var part = new byte[i - start];
                    Buffer.BlockCopy(data, start, part, 0, part.Length);
                    parts.Add(part);
                    start = i + 1;
                }
            }
            var remainder = new byte[data.Length - start];
            Buffer.BlockCopy(data, start, remainder, 0, remainder.Length);
            parts.Add(remainder);
            return parts.ToArray();
        }
    }
}
