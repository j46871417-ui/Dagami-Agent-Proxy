using System;
using System.IO;
using System.Reflection;
using System.Text;
using DevProxyApp;

class AntigravityPatchChecks {
 static int checks;
 static void Assert(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
 static bool Same(byte[] a, byte[] b) { return Convert.ToBase64String(a) == Convert.ToBase64String(b); }
 static byte[] Legacy(string version) {
  var patterns = (string[])typeof(AntigravityIntegration).GetField("Patterns", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
  return Encoding.ASCII.GetBytes(version + "\0" + string.Join("\0", patterns) + "\0https_proxy\0https_proxy\0unrelated-tail");
 }
 static void Reject(Action action) { bool caught = false; try { action(); } catch (UnsupportedAntigravityBinaryException) { caught = true; } Assert(caught, "Unsupported bytes accepted"); }
 static int Main() {
  string root = Path.Combine(Path.GetTempPath(), "Dagami-patch-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
  try {
   int count; byte[] original = Legacy("v1"), patched = AntigravityIntegration.PatchData(original, out count);
   Assert(count == 10 && patched.Length == original.Length, "Legacy fields or proxy variable not rewritten");
   string text = Encoding.ASCII.GetString(patched);
   Assert(!text.Contains("ineligible") && !text.Contains("https_proxy") && text.Contains("AG_LS_PROXY"), "Proxy lookup not routed to private variable");
   Assert(text.EndsWith("unrelated-tail"), "Unrelated bytes changed");
   byte[] modern = Encoding.ASCII.GetBytes("Go build ID: fixture\0\n\nineligible\x18\x09\0name=ineligible,proto3\0json:\"ineligible_tiers,omitempty\"\0https_proxy");
   patched = AntigravityIntegration.PatchData(modern, out count);
   Assert(count == 4 && patched.Length == modern.Length, "Modern descriptor rejected or resized");
   Reject(() => AntigravityIntegration.PatchData(Encoding.ASCII.GetBytes("Go build ID: unrelated ineligible https_proxy"), out count));
   Reject(() => AntigravityIntegration.PatchData(Encoding.ASCII.GetBytes("Go build ID: fixture\0\n\nineligible\x18\x09\0name=ineligible,proto3\0inexigible"), out count));
   string agy = Path.Combine(root, "agy.exe"); File.WriteAllBytes(agy, modern);
   Assert(AntigravityIntegration.BackendPath(agy) == agy && NativeCliApps.IsCli(new AppTarget { ExePath = agy }), "agy not treated as a direct CLI target");
   Assert(AntigravityIntegration.PrepareClient(agy).Prepared, "agy preparation failed"); byte[] first = File.ReadAllBytes(agy);
   Assert(AntigravityIntegration.PrepareClient(agy).Prepared && Same(first, File.ReadAllBytes(agy)), "Repeated preparation not idempotent");
   AntigravityIntegration.RestoreClient(agy); Assert(Same(modern, File.ReadAllBytes(agy)), "agy restore not exact");
   AntigravityIntegration.PrepareClient(agy);
   byte[] update = Legacy("updated-v2"); File.WriteAllBytes(agy, update);
   Assert(AntigravityIntegration.PrepareClient(agy).Prepared, "Application update blocked by previous-generation backup");
   Assert(Directory.GetFiles(agy + ".devproxy-history").Length == 2, "Old generation backup and manifest not retained");
   AntigravityIntegration.RestoreClient(agy); Assert(Same(update, File.ReadAllBytes(agy)), "Restore downgraded updated application");
   string app = Path.Combine(root, "Antigravity.exe"); File.WriteAllText(app, "launcher fixture");
   string a = Path.Combine(root, @"resources\bin\language_server.exe"), b = Path.Combine(root, @"resources\app\extensions\antigravity\bin\language_server_windows_x64.exe");
   Directory.CreateDirectory(Path.GetDirectoryName(a)); Directory.CreateDirectory(Path.GetDirectoryName(b));
   File.WriteAllBytes(a, original); File.WriteAllBytes(b, modern);
   Assert(AntigravityIntegration.BackendPaths(app).Count == 2, "Manager/IDE backends not discovered");
   Assert(AntigravityIntegration.PrepareClient(app).Prepared, "Multiple backend preparation failed");
   Assert(!Same(original, File.ReadAllBytes(a)) && !Same(modern, File.ReadAllBytes(b)), "One backend left unprepared");
   AntigravityIntegration.RestoreClient(app); Assert(Same(original, File.ReadAllBytes(a)) && Same(modern, File.ReadAllBytes(b)), "Multiple restore not exact");
   string unsupported = Path.Combine(Path.GetDirectoryName(b), "language_server_unknown.exe"); File.WriteAllText(unsupported, "unknown architecture fixture");
   var partial = AntigravityIntegration.PrepareClient(app);
   Assert(partial.Prepared && !partial.Complete && partial.Message.Contains("Частичная"), "Skipped backend reported as complete preparation");
   Assert(File.ReadAllText(unsupported) == "unknown architecture fixture", "Unsupported secondary backend changed");
   File.Delete(unsupported); AntigravityIntegration.RestoreClient(app);
   // Lock the second sorted target: an earlier successful write must roll back.
   string[] ordered = AntigravityIntegration.BackendPaths(app).ToArray(); bool failed = false;
   using (var held = new FileStream(ordered[1], FileMode.Open, FileAccess.Read, FileShare.Read)) {
    try { AntigravityIntegration.PrepareClient(app); } catch (IOException) { failed = true; }
   }
   Assert(failed && Same(original, File.ReadAllBytes(a)) && Same(modern, File.ReadAllBytes(b)), "Partial multi-file failure did not restore previous bytes");
   File.WriteAllText(agy, "unknown new binary"); byte[] unknown = File.ReadAllBytes(agy);
   Assert(!AntigravityIntegration.PrepareClient(agy).Prepared && Same(unknown, File.ReadAllBytes(agy)), "Unknown application modified");
   Console.WriteLine("PASS " + checks + " Manager/IDE/agy patching, private proxy variable, modern descriptors, update generations, rollback and unknown-file protection"); return 0;
  } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
  finally {
   string resolved = Path.GetFullPath(root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
   if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Dagami-patch-recovery-")) throw new IOException("Unsafe fixture cleanup");
   Directory.Delete(resolved, true);
  }
 }
}
