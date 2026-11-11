using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using DevProxyApp;

class CodexDiscoveryChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static void Manifest(string root,string identity,string executable,string prefix="") {
  File.WriteAllText(Path.Combine(root,"AppxManifest.xml"),prefix+"<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\""+identity+"\" Version=\"26.10.1.0\"/><Applications>"+
   "<Application Id=\"CodexCoreCommandRunner\" Executable=\"app/resources/codex-command-runner.exe\"/>"+
   "<Application Id=\"App\" Executable=\""+SecurityElement.Escape(executable)+"\"/></Applications></Package>",Encoding.UTF8);
 }
 static int Main(){
  string temp=Path.Combine(Path.GetTempPath(),"Dagami-codex-discovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
  try {
   string root=Path.Combine(temp,"OpenAI.Codex_fixture");Directory.CreateDirectory(Path.Combine(root,@"app\resources"));
   string gui=Path.Combine(root,@"app\ChatGPT.exe"),cli=Path.Combine(root,@"app\resources\codex-command-runner.exe");
   File.Copy(typeof(MainForm).Assembly.Location,gui);File.Copy(typeof(CodexDiscoveryChecks).Assembly.Location,cli);
   Manifest(root,"OpenAI.Codex","app/ChatGPT.exe");
   Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==gui,"MSIX ChatGPT.exe launcher missed or command runner selected");
   var target=new AppTarget{Id="codex-gui",ExePath=gui};
   string packagePath=Path.Combine(temp,@"WindowsApps\OpenAI.Codex_26.10.1.0_x64__2p2nqsd0c76g0");
   var packaged=new AppTarget{Id="codex-gui",ExePath=Path.Combine(packagePath,@"app\ChatGPT.exe")};
   Assert(ApplicationDiscovery.CodexPackageRoot(packaged)==packagePath,"Versioned Codex MSIX path not recognized");
   Assert(ApplicationDiscovery.CodexPackageRoot(target)==null,"Unpackaged Codex treated as MSIX");
   packaged.Id="codex-cli";
   Assert(ApplicationDiscovery.CodexPackageRoot(packaged)==null,"Desktop alias could be applied to CLI");
   Assert(ApplicationSupport.Kind(target)=="codex-desktop"&&!NativeCliApps.IsCli(target)&&!NativeCliApps.UsesCommandShim(target),"Codex Desktop confused with CLI");
   Assert(ApplicationSupport.SettingsPath(target)==null,"MSIX Desktop would overwrite CLI settings");
   Assert(ApplicationDiscovery.Desktop("FixtureCodex",new[]{cli,gui})==gui,"Console candidate accepted as Desktop");
   Manifest(root,"OpenAI.ChatGPT","app/ChatGPT.exe");
   Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Ordinary ChatGPT mistaken for Codex");
   Manifest(root,"OpenAI.CodexEvil","app/ChatGPT.exe");
   Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Package identity checked by prefix only");
   string outside=Path.Combine(temp,"outside.exe");File.Copy(gui,outside);
   Manifest(root,"OpenAI.Codex","../outside.exe");Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Relative package traversal accepted");
   Manifest(root,"OpenAI.Codex",outside);Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Absolute executable outside package accepted");
   Manifest(root,"OpenAI.Codex","app/resources/codex-command-runner.exe");Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Console helper accepted as main GUI");
   Manifest(root,"OpenAI.Codex","app/missing.exe");Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Nonexistent application marked installed");
   Manifest(root,"OpenAI.Codex","app/ChatGPT.exe","<!DOCTYPE Package [<!ENTITY forbidden SYSTEM 'file:///missing-dagami-fixture'>]>");
   Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Manifest DTD accepted");
   File.WriteAllText(Path.Combine(root,"AppxManifest.xml"),"broken XML");Assert(ApplicationDiscovery.PackagedDesktop(root,"OpenAI.Codex")==null,"Malformed manifest crashed discovery");
   // Read-only integration against registration APIs. Nothing is launched,
   // stopped, patched or written inside the real package/user profile.
   var installed=ApplicationDiscovery.PackageRoots(ApplicationDiscovery.CodexPackageFamily);string actual=null;
   foreach(string package in installed){actual=ApplicationDiscovery.PackagedDesktop(package,"OpenAI.Codex");if(actual!=null)break;}
   if(actual!=null){
    Assert(ApplicationDiscovery.Desktop("Codex",new string[0])==actual,"Registered Codex package not discovered");
    Assert(File.Exists(actual)&&!NativeCliApps.IsCli(new AppTarget{ExePath=actual}),"Registered launcher is not a Desktop executable");
    var native=new AppTarget{Id="codex-gui",ExePath=actual};
    var start=new ProcessStartInfo(actual){Arguments="--proxy-server=http://127.0.0.1:53200",UseShellExecute=false};
    start.EnvironmentVariables["HTTPS_PROXY"]="http://127.0.0.1:53200";
    if(ApplicationDiscovery.UseCodexDesktopAlias(start,native)){
     Assert(Path.GetFileName(start.FileName)=="chatgpt-desktop.exe"&&Path.GetFileName(Path.GetDirectoryName(start.FileName))==ApplicationDiscovery.CodexPackageFamily,
      "Codex alias is not package-specific");
     Assert(!start.UseShellExecute&&start.Arguments.Contains("--proxy-server")&&start.EnvironmentVariables["HTTPS_PROXY"]=="http://127.0.0.1:53200",
      "Switching to the Codex alias changed proxy settings");
     Console.WriteLine("PASS current-user registered Codex alias selected with proxy settings retained (read only)");
    }else Console.WriteLine("SKIP registered Codex alias unavailable for this user");
    Console.WriteLine("PASS current-user installed Codex Desktop registration and manifest detected (read only)");
   }else Console.WriteLine("SKIP installed Codex package integration: not installed for this user");
   Console.WriteLine("PASS "+checks+" MSIX Codex identity, launcher selection, CLI separation and manifest validation checks");return 0;
  }catch(Exception error){Console.Error.WriteLine(error);return 1;}
  finally {
   string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!Path.GetFullPath(temp).StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(temp).StartsWith("Dagami-codex-discovery-"))throw new IOException("Unsafe fixture cleanup");
   Directory.Delete(temp,true);
  }
 }
}
