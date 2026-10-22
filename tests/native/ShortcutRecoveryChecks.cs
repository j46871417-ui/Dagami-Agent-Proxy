using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DevProxyApp;

class ShortcutRecoveryChecks {
 static int checks; static object shell;
 static void Assert(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
 static string Hash(string path) { return AntigravityIntegration.Hash(File.ReadAllBytes(path)); }
 static void Link(string path, string exe, string args) {
  object link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
  try {
   foreach (var pair in new[] { new[] { "TargetPath", exe }, new[] { "Arguments", args }, new[] { "WorkingDirectory", Path.GetDirectoryName(exe) } })
    link.GetType().InvokeMember(pair[0], BindingFlags.SetProperty, null, link, new object[] { pair[1] });
   link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
  } finally { Marshal.FinalReleaseComObject(link); }
 }
 [STAThread] static int Main() {
  string root = Path.Combine(Path.GetTempPath(), "Dagami-shortcut-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
  try {
   shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
   string exe = Path.Combine(root, "Editor.exe"), other = Path.Combine(root, "Other.exe"), folder = Path.Combine(root, "Desktop");
   Directory.CreateDirectory(folder); File.Copy(typeof(MainForm).Assembly.Location, exe); File.Copy(exe, other);
   var app = new AppTarget { Id = "repair-fixture", DisplayName = "Editor", ExePath = exe };
   var store = new NativeShortcuts(Path.Combine(root, "state")); string manager = typeof(MainForm).Assembly.Location, issue;
   string path = Path.Combine(folder, "Editor.lnk"); Link(path, exe, "--project original");
   store.EnablePrepared(app, manager, new[] { folder }, () => {}, () => {}, true);
   File.Delete(path);
   Assert(store.VerifiedCount(app.Id, out issue) == 1 && issue != null, "Deleted link not diagnosed");
   var repaired = store.EnablePrepared(app, manager, new[] { folder }, () => {}, () => {}, true);
   Assert(repaired.Count == 1 && File.Exists(repaired[0]), "Deleted link blocked recreation");
   Assert(store.VerifiedCount(app.Id, out issue) == 1 && issue == null, "Recreated link not verified");
   Assert(Directory.GetFiles(Path.Combine(root, "state", "history")).Length > 0, "Old journal not retained");
   string moved = Path.Combine(folder, "Moved editor.lnk"); File.Move(repaired[0], moved);
   repaired = store.EnablePrepared(app, manager, new[] { folder }, () => {}, () => {}, true);
   Assert(repaired.Count == 1 && repaired[0] == moved, "Moved link duplicated instead of located");
   Assert(store.VerifiedCount(app.Id, out issue) == 1 && issue == null, "Moved record path not updated");
   string relocatedManager = Path.Combine(root, "MovedDagami.exe"); File.Copy(manager, relocatedManager);
   store.EnablePrepared(app, relocatedManager, new[] { folder }, () => {}, () => {}, true);
   object relocatedLink = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { moved });
   try { Assert(Convert.ToString(relocatedLink.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, relocatedLink, null)) == relocatedManager, "Moved manager not rebound"); }
   finally { Marshal.FinalReleaseComObject(relocatedLink); }
   store.Restore(app.Id);
   Link(moved, exe, "--installer-recreated"); string original = Hash(moved);
   store.EnablePrepared(app, manager, new[] { folder }, () => {}, () => {}, true);
   Link(moved, exe, "--new-installer-args"); string installer = Hash(moved);
   store.EnablePrepared(app, manager, new[] { folder }, () => {}, () => {}, true);
   Assert(store.VerifiedCount(app.Id, out issue) == 1 && issue == null, "Installer recreation blocked patch");
   store.Restore(app.Id); Assert(Hash(moved) == installer && installer != original, "Restore lost new installer arguments");
   File.Delete(moved);
   Link(path, other, "--foreign-1"); string foreign1 = Hash(path);
   string collision = Path.Combine(folder, "Editor (Dagami 1).lnk"); Link(collision, other, "--foreign-2"); string foreign2 = Hash(collision);
   File.WriteAllText(Path.Combine(folder, "broken.lnk"), "unreadable fixture");
   repaired = store.EnablePrepared(app, manager, new[] { folder }, () => {}, () => {}, true);
   Assert(repaired.Count == 1 && repaired[0].EndsWith("Editor (Dagami 2).lnk"), "Unique name not selected");
   Assert(Hash(path) == foreign1 && Hash(collision) == foreign2, "Foreign name collision overwritten");
   File.Delete(repaired[0]); Assert(store.Restore(app.Id).Count == 1 && store.Count(app.Id) == 0, "Missing shortcut blocked disable");
   app.DisplayName = "Bad:/name*?";
   repaired = store.EnablePrepared(app, manager, new[] { folder }, () => {}, () => {}, true);
   Assert(repaired.Count == 1 && File.Exists(repaired[0]), "Invalid display-name characters blocked creation");
   Assert(store.VerifiedCount(app.Id, out issue) == 1 && issue == null, "Sanitized shortcut failed verification");
   store.Restore(app.Id);
   string restoreFolder = Path.Combine(root, "RestoreMoved"); Directory.CreateDirectory(restoreFolder);
   string beforeMove = Path.Combine(restoreFolder, "Before move.lnk"), afterMove = Path.Combine(restoreFolder, "After move.lnk");
   Link(beforeMove, exe, "--restore-after-move"); string moveOriginal = Hash(beforeMove);
   store.EnablePrepared(app, manager, new[] { restoreFolder }, () => {}, () => {}, true); File.Move(beforeMove, afterMove);
   store.Restore(app.Id);
   Assert(Hash(afterMove) == moveOriginal && store.Count(app.Id) == 0, "Disable left a moved shortcut pointing at the manager");
   Console.WriteLine("PASS " + checks + " deleted/moved/recreated links, journal history, collisions, malformed links, disable and safe names"); return 0;
  } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
  finally {
   if (shell != null) Marshal.FinalReleaseComObject(shell);
   string resolved = Path.GetFullPath(root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
   if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Dagami-shortcut-recovery-")) throw new IOException("Unsafe fixture cleanup");
   Directory.Delete(resolved, true);
  }
 }
}
