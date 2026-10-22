using System;
using System.IO;
using DevProxyApp;
class RealBinaryReadChecks {
 static int Main(string[] args) {
  if (args.Length != 1) return 2;
  try {
   byte[] original = File.ReadAllBytes(args[0]); string before = AntigravityIntegration.Hash(original); int count;
   byte[] patched = AntigravityIntegration.PatchData(original, out count);
   if (count == 0 || patched.Length != original.Length || AntigravityIntegration.Hash(File.ReadAllBytes(args[0])) != before) throw new Exception("Read-only binary verification failed");
   Console.WriteLine("PASS real installed-backup read-only patch simulation: bytes=" + original.Length + ", changes=" + count + ", input unchanged"); return 0;
  } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
}
