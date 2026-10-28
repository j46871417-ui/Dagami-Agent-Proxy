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
 static void Number(byte[] data,int at,int value){Array.Copy(BitConverter.GetBytes(value),0,data,at,4);}
 static void Gate(byte[] data,int at){byte[] gate={0x48,0x85,0xc0,0x0f,0x84,0,0,0,0,0x80,0x78,8,0,0x0f,0x85,0,0,0,0,0x48,0x8b,0x50,0x50,0x4c,0x8d,0x1d,0,0,0,0};Array.Copy(gate,0,data,at,gate.Length);Number(data,at+5,100-9);Number(data,at+15,100-19);}
 static byte[] Cli(){var data=new byte[1024];data[0]=0x4d;data[1]=0x5a;Number(data,0x3c,128);Number(data,128,0x4550);data[132]=0x64;data[133]=0x86;data[134]=1;Number(data,152+16,512);Number(data,152+20,512);Number(data,152+36,0x20000000);byte[] markers=Encoding.ASCII.GetBytes("Go build ID: fixture\0Eligibility check failed:\0prompt-interactive\0name=ineligible,proto3\0https_proxyZ\0");Array.Copy(markers,0,data,256,markers.Length);Gate(data,576);return data;}
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
   byte[] cli=Cli(),cliPatched=AntigravityIntegration.PatchData(cli,out count);Assert(count==2&&cli.Length==cliPatched.Length,"CLI gate or proxy not prepared");
   byte[] goBuildInfo=(byte[])cli.Clone();Array.Copy(Encoding.ASCII.GetBytes("Go buildinf:"),0,goBuildInfo,256,12);AntigravityIntegration.PatchData(goBuildInfo,out count);Assert(count==2,"Windows Go build-info marker not recognised");
   int proxy=Encoding.ASCII.GetString(cli).IndexOf("https_proxy",StringComparison.Ordinal);for(int index=0;index<cli.Length;index++)if(cli[index]!=cliPatched[index])Assert((index>=585&&index<=588)||(index>=proxy&&index<proxy+11),"CLI changed an unrelated byte at "+index);
   Assert(cliPatched[proxy+11]==(byte)'Z'&&Encoding.ASCII.GetString(cliPatched).Contains("name=ineligible,proto3"),"CLI proxy boundary or protobuf metadata damaged");
   Reject(()=>AntigravityIntegration.PatchData(cliPatched,out count));
   byte[] wrong=(byte[])cli.Clone();Number(wrong,591,82);Reject(()=>AntigravityIntegration.PatchData(wrong,out count));
   wrong=(byte[])cli.Clone();Gate(wrong,720);Reject(()=>AntigravityIntegration.PatchData(wrong,out count));
   wrong=(byte[])cli.Clone();wrong[598]=0x60;Reject(()=>AntigravityIntegration.PatchData(wrong,out count));
   wrong=(byte[])cli.Clone();Number(wrong,188,0);Reject(()=>AntigravityIntegration.PatchData(wrong,out count));
   wrong=(byte[])cli.Clone();wrong[132]=0x64;wrong[133]=0xaa;Reject(()=>AntigravityIntegration.PatchData(wrong,out count));
   wrong=(byte[])cli.Clone();Number(wrong,172,1000);Reject(()=>AntigravityIntegration.PatchData(wrong,out count));
   string currentCli=Path.Combine(root,"cli","agy.exe");Directory.CreateDirectory(Path.GetDirectoryName(currentCli));File.WriteAllBytes(currentCli,cli);
   Assert(AntigravityIntegration.ClientPreparationStatus(currentCli).Contains("не применён"),"Unpatched CLI status is not clear");
   Assert(AntigravityIntegration.Inspect(currentCli).Contains("требуется подготовка"),"Inspect disagrees with the current CLI patcher");
   Assert(AntigravityIntegration.PrepareClient(currentCli).Complete,"Current CLI refused by preparation preflight");byte[] firstCli=File.ReadAllBytes(currentCli);
   Assert(AntigravityIntegration.ClientPreparationStatus(currentCli).Contains("применён, копия проверена"),"Prepared CLI status is not clear");
   string backup=currentCli+".devproxy-region.bak";File.WriteAllText(backup,"broken backup");Assert(AntigravityIntegration.ClientPreparationStatus(currentCli).Contains("не удалось проверить"),"Damaged backup was shown as verified");File.WriteAllBytes(backup,cli);
   Assert(AntigravityIntegration.PrepareClient(currentCli).Complete&&Same(firstCli,File.ReadAllBytes(currentCli)),"CLI repeat preparation not idempotent");AntigravityIntegration.RestoreClient(currentCli);Assert(Same(cli,File.ReadAllBytes(currentCli)),"CLI original not restored exactly");
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
   Assert(AntigravityIntegration.ClientPreparationStatus(agy).Contains("не поддерживается"),"Unknown CLI status reported as patched");
   bool compatible;AntigravityIntegration.InspectClient(agy,out compatible);Assert(!compatible,"Read-only inspection reported an unknown CLI as compatible");
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
