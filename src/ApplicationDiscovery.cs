using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DevProxyApp {
 public static class ApplicationDiscovery {
  public static string AntigravityIde(IEnumerable<string> roots) {
   var candidates = new List<string>();
   foreach(string root in roots) foreach(string folder in new[]{"Antigravity IDE","antigravity-ide","antigravity"})
    candidates.Add(Path.Combine(root,folder,"Antigravity IDE.exe"));
   return Desktop("Antigravity IDE",candidates);
  }
  public static void IncludeRegistered(List<AppTarget> targets,IEnumerable<AppTarget> registrations) {
   foreach(var registration in registrations) {
    var existing=targets.Find(target=>target.Id==registration.Id);
    if(existing==null)targets.Add(registration);
    else if(!existing.IsInstalled) {existing.ExePath=registration.ExePath;if(string.IsNullOrEmpty(existing.ConfigPath))existing.ConfigPath=registration.ConfigPath;}
   }
  }
  public static string Cli(string command) {
   string existing=NativeCliApps.FindCommand(command,Environment.GetEnvironmentVariable("PATH"),NativeCliApps.Current.BinDirectory,true);
   if(existing!=null&&NativeCliApps.IsCli(new AppTarget{ExePath=existing}))return existing;
   string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
   foreach(string path in new[]{Path.Combine(home,@".local\bin",command+".exe"),Path.Combine(home,"."+command,"bin",command+".exe"),Path.Combine(local,command,"bin",command+".exe"),Path.Combine(roaming,"npm",command+".cmd")})
    if(File.Exists(path)&&NativeCliApps.IsCli(new AppTarget{ExePath=path}))return path;
   return null;
  }
  public static string Desktop(string name,IEnumerable<string> candidates) {
   foreach(string path in candidates)if(File.Exists(path))return path;
   foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})try {
    using(var key=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\"+name+".exe")) {
     string path=key==null?null:Convert.ToString(key.GetValue(null));if(!string.IsNullOrEmpty(path)&&File.Exists(path.Trim('"')))return path.Trim('"');
    }
   }catch(System.Security.SecurityException){}
   // A running packaged Desktop app reveals its real launcher without guessing
   // a versioned WindowsApps directory or changing package permissions.
   foreach(var process in Process.GetProcessesByName(name))using(process)try {
    string path=process.MainModule.FileName;var target=new AppTarget{ExePath=path};
    if(File.Exists(path)&&!NativeCliApps.IsCli(target))return path;
   }catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}
   return null;
  }
  static string Identity(string path) {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())),0,8).Replace("-","").ToLowerInvariant();}
  public static List<AppTarget> JetBrains(IEnumerable<string> roots) {
   var found=new List<AppTarget>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(string root in roots)Visit(root,0,found,visited);return found;
  }
  static void Visit(string root,int depth,List<AppTarget> found,HashSet<string> visited) {
   if(!Directory.Exists(root)||!visited.Add(Path.GetFullPath(root)))return;
   try {
    string product=Path.Combine(root,"product-info.json");
    if(File.Exists(product)) {
     var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(product));
     if(data==null)return;
     foreach(string launcher in new[]{"idea64","pycharm64","webstorm64","phpstorm64","clion64","goland64","rider64","datagrip64","rubymine64","rustrover64","dataspell64"}) {
      string exe=Path.Combine(root,"bin",launcher+".exe");if(!File.Exists(exe))continue;
      object name;var target=new AppTarget{Id="jb_"+Identity(exe),DisplayName="JetBrains "+(data.TryGetValue("name",out name)?Convert.ToString(name):Path.GetFileName(root)),ExePath=exe};
      ApplicationSupport.ResolveSettings(target);found.Add(target);break;
     }
     return;
    }
    if(depth>=4)return;
    foreach(string directory in Directory.GetDirectories(root)) {
     string name=Path.GetFileName(directory);
     if(name=="jbr"||name=="plugins"||name=="node_modules"||(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)continue;
     Visit(directory,depth+1,found,visited);
    }
   }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
  }
 }
}
