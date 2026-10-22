using System;
using System.IO;
using System.Text;
using System.Xml;

namespace DevProxyApp {
 public static class JetBrainsProxySettings {
  static readonly string[] Keys={"USE_HTTP_PROXY","USE_PROXY_PAC","USE_PAC_URL","PROXY_TYPE_IS_SOCKS","PROXY_HOST","PROXY_PORT","PROXY_AUTHENTICATION","PROXY_EXCEPTIONS"};
  static XmlDocument Read(string path) {
   var doc=new XmlDocument{XmlResolver=null,PreserveWhitespace=true};
   if(!File.Exists(path)){doc.LoadXml("<application />");return doc;}
   using(var reader=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))doc.Load(reader);
   if(doc.DocumentElement==null||doc.DocumentElement.Name!="application")throw new InvalidDataException("Неизвестная структура настроек JetBrains.");return doc;
  }
  static XmlElement Component(XmlDocument doc) {
   var element=doc.SelectSingleNode("/application/component[@name='HttpConfigurable']") as XmlElement;
   if(element==null){element=doc.CreateElement("component");element.SetAttribute("name","HttpConfigurable");doc.DocumentElement.AppendChild(element);}return element;
  }
  static void Atomic(string path,XmlDocument doc) {
   Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=Path.Combine(Path.GetDirectoryName(path),".dagami-"+Guid.NewGuid().ToString("N")+".tmp");
   try{using(var writer=XmlWriter.Create(temp,new XmlWriterSettings{Encoding=new UTF8Encoding(false),Indent=true}))doc.Save(writer);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}
  }
  public static bool Apply(AppTarget target,string proxy,out string message) {
   try {
    ApplicationSupport.ResolveSettings(target);if(string.IsNullOrEmpty(target.ConfigPath))throw new InvalidOperationException("В product-info.json не найден каталог конфигурации JetBrains. Укажите установленный IDE с этим файлом.");
    var uri=new Uri(proxy);if(uri.Scheme!="http"||uri.Host!="127.0.0.1"||!string.IsNullOrEmpty(uri.UserInfo))throw new ArgumentException("Для JetBrains используется локальный HTTP-мост без пароля.");
    var doc=Read(target.ConfigPath);string backup=target.ConfigPath+".devproxy.bak";
    if(!File.Exists(backup))Atomic(backup,doc);
    var component=Component(doc);string[] values={"true","false","false","false","127.0.0.1",uri.Port.ToString(),"false","localhost,127.0.0.1,::1"};
    for(int i=0;i<Keys.Length;i++){var option=component.SelectSingleNode("option[@name='"+Keys[i]+"']") as XmlElement;if(option==null){option=doc.CreateElement("option");option.SetAttribute("name",Keys[i]);component.AppendChild(option);}option.SetAttribute("value",values[i]);}
    Atomic(target.ConfigPath,doc);message="Настройки JetBrains записаны; перезапустите IDE управляемым ярлыком.";return true;
   }catch(Exception ex){message=ex.Message;return false;}
  }
  public static bool Restore(AppTarget target,out string message) {
   try {
    ApplicationSupport.ResolveSettings(target);if(string.IsNullOrEmpty(target.ConfigPath))throw new InvalidOperationException("Каталог JetBrains не определён.");
    string backup=target.ConfigPath+".devproxy.bak";if(!File.Exists(backup))throw new InvalidOperationException("Нет резервной копии настроек JetBrains.");
    var doc=Read(File.Exists(target.ConfigPath)?target.ConfigPath:backup);var original=Read(backup);var current=Component(doc);var previous=original.SelectSingleNode("/application/component[@name='HttpConfigurable']");
    foreach(string key in Keys){var node=current.SelectSingleNode("option[@name='"+key+"']");if(node!=null)current.RemoveChild(node);var old=previous==null?null:previous.SelectSingleNode("option[@name='"+key+"']");if(old!=null)current.AppendChild(doc.ImportNode(old,true));}
    Atomic(target.ConfigPath,doc);File.Delete(backup);message="Исходные прокси-настройки JetBrains восстановлены; ручные изменения этих полей заменены.";return true;
   }catch(Exception ex){message=ex.Message;return false;}
  }
 }
}
