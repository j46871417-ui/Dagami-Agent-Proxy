using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Diagnostics;
using System.Text;
using DevProxyApp;
class TlsDiagnosticsChecks {
 static int checks;
 static void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
 static byte[] Der(byte tag,byte[] bytes){using(var output=new MemoryStream()){output.WriteByte(tag);if(bytes.Length<128)output.WriteByte((byte)bytes.Length);else{var length=new System.Collections.Generic.List<byte>();for(int n=bytes.Length;n>0;n>>=8)length.Insert(0,(byte)n);output.WriteByte((byte)(128|length.Count));output.Write(length.ToArray(),0,length.Count);}output.Write(bytes,0,bytes.Length);return output.ToArray();}}
 static byte[] Integer(byte[] bytes){int offset=0;while(offset<bytes.Length-1&&bytes[offset]==0)offset++;byte[] value=new byte[bytes.Length-offset+((bytes[offset]&128)!=0?1:0)];Array.Copy(bytes,offset,value,value.Length-(bytes.Length-offset),bytes.Length-offset);return Der(2,value);}
 static string Pem(string label,byte[] bytes){return "-----BEGIN "+label+"-----\n"+Convert.ToBase64String(bytes,Base64FormattingOptions.InsertLineBreaks)+"\n-----END "+label+"-----\n";}
 static void Check(string layer,string root) {
  using(var rsa=new RSACng(2048)) {
   var request=new CertificateRequest("CN=dagami-fixture.invalid",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
   using(var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1))) {
    string certificatePath=Path.Combine(root,"certificate.pem"),keyPath=Path.Combine(root,"key.pem");
    File.WriteAllText(certificatePath,Pem("CERTIFICATE",generated.Export(X509ContentType.Cert)));
    RSAParameters key=rsa.ExportParameters(true);
    using(var data=new MemoryStream()){
     foreach(byte[] value in new[]{new byte[]{0},key.Modulus,key.Exponent,key.D,key.P,key.Q,key.DP,key.DQ,key.InverseQ}){byte[] encoded=Integer(value);data.Write(encoded,0,encoded.Length);}
     File.WriteAllText(keyPath,Pem("RSA PRIVATE KEY",Der(48,data.ToArray())));
    }
    // Python/OpenSSL serves an in-memory fixture key from this disposable TEMP
    // directory. Windows certificate/private-key stores are never changed.
    string script="import socket,ssl,sys\nc=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)\nc.load_cert_chain(sys.argv[1],sys.argv[2])\ns=socket.socket()\ns.bind(('127.0.0.1',0))\ns.listen(1)\ns.settimeout(5)\nprint(s.getsockname()[1],flush=True)\ntry:\n p,_=s.accept()\n p.settimeout(3)\n c.wrap_socket(p,server_side=True).close()\nexcept (ssl.SSLError,OSError):\n pass\nfinally:\n s.close()\n";
    string python=Environment.GetEnvironmentVariable("DAGAMI_TEST_PYTHON");if(string.IsNullOrEmpty(python))throw new Exception("Run this suite through tools/run_native_checks.py");
    var start=new ProcessStartInfo(python,"-u -c "+NativeShortcuts.Quote(script)+" "+NativeShortcuts.Quote(certificatePath)+" "+NativeShortcuts.Quote(keyPath)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    using(var server=Process.Start(start))try {
     var ready=System.Threading.Tasks.Task.Factory.StartNew(()=>server.StandardOutput.ReadLine());
     Assert(ready.Wait(5000),"TLS fixture did not start");int port;Assert(int.TryParse(ready.Result,out port),"TLS fixture returned no port");
     bool rejected=false;
     using(var client=new TcpClient("127.0.0.1",port))try {using(var tls=TlsDiagnostics.Connect(client.GetStream(),"www.googleapis.com",layer,3000)){} }
     catch(AuthenticationException error){
      rejected=true;
      Assert(error.Message.Contains("host=www.googleapis.com")&&error.Message.Contains("layer="+layer),"TLS host/layer missing");
      Assert(error.Message.Contains("certificate_checked=True"),"Certificate was not inspected");
      Assert(error.Message.Contains("RemoteCertificateChainErrors"),"Untrusted certificate reason missing");
      Assert(error.Message.Contains("chain=")&&!error.Message.Contains("chain=none"),"Chain error codes missing");
      Assert(!error.Message.Contains("dagami-fixture.invalid"),"Certificate subject leaked into diagnostic");
     }
     Assert(rejected,"Invalid certificate accepted");
    }finally{if(!server.WaitForExit(5000))server.Kill();Assert(server.WaitForExit(3000)&&server.ExitCode==0,"TLS fixture failed: "+server.StandardError.ReadToEnd());}
   }
  }
 }
 static int Main(){string root=Path.Combine(Path.GetTempPath(),"Dagami-tls-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{Check("target-service",root);Check("https-proxy",root);Console.WriteLine("PASS "+checks+" real TLS rejection, layer/chain diagnostics and certificate privacy checks (loopback only; trust store unchanged)");return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}finally{string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-tls-fixture-"))throw new IOException("Unsafe fixture cleanup");Directory.Delete(root,true);}}
}
