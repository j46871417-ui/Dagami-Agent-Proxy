using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using DevProxyApp;
class FixtureApp {
 static int Main(string[] args) {
  if(args.Length<2)return 2;
  var text=new StringBuilder();
  text.AppendLine("nonce="+args[1]);text.AppendLine("cwd="+Environment.CurrentDirectory);
  foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy","AG_LS_PROXY","AGY_PROXY_URL","NO_PROXY"}) text.AppendLine(key+"="+Environment.GetEnvironmentVariable(key));
  File.WriteAllText(args[0],text.ToString());
  if(Array.IndexOf(args,"--leaf")>=0)return 0;
  var child=new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,NativeShortcuts.Quote(args[0]+".child")+" "+NativeShortcuts.Quote(args[1])+" --leaf");
  child.UseShellExecute=false;child.CreateNoWindow=true;using(var process=Process.Start(child))process.WaitForExit();
  Thread.Sleep(15000);return 0;
 }
}
