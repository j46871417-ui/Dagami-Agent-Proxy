using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DevProxyApp {
    public static class SignedLicenses {
        // Populate from the issuer's PUBLIC key during the coordinated bot rollout.
        // Never create a production private key in this client or its repository.
        public const string IssuerKeyId = "pending-migration";
        public const string IssuerPublicKeyXml = "";
        public static readonly bool AcceptLegacyKeys = true; // user-requested migration staging
        public static bool Validate(string key) {
            return Verify(key,IssuerKeyId,IssuerPublicKeyXml,DateTime.UtcNow);
        }
        static byte[] Decode(string text,int maximum) {
            if(text.Length==0||text.Length>maximum||!Regex.IsMatch(text,@"\A[A-Za-z0-9_-]+\z"))throw new FormatException();
            string padded=text.Replace('-','+').Replace('_','/');padded=padded.PadRight((padded.Length+3)/4*4,'=');
            byte[] data=Convert.FromBase64String(padded);
            if(Convert.ToBase64String(data).TrimEnd('=').Replace('+','-').Replace('/','_')!=text)throw new FormatException();
            return data;
        }
        static long Integer(object value) {if(!(value is int)&&!(value is long))throw new FormatException();return Convert.ToInt64(value);}
        public static bool Verify(string key,string issuer,string publicKeyXml,DateTime utcNow) {
            try {
                if(string.IsNullOrWhiteSpace(key)||key.Length>4096||string.IsNullOrEmpty(publicKeyXml))return false;
                string[] parts=key.Split('.');
                if(parts.Length!=4||parts[0]!="DAGAMI2"||parts[1]!=issuer||!Regex.IsMatch(issuer,@"\A[a-z0-9-]{1,32}\z"))return false;
                byte[] payload=Decode(parts[2],2048),signature=Decode(parts[3],2048);
                // Sign the version, key ID and encoded payload, preventing cross-protocol reuse.
                byte[] message=Encoding.ASCII.GetBytes(parts[0]+"."+parts[1]+"."+parts[2]);
                using(var rsa=new RSACryptoServiceProvider(new CspParameters(24))) {
                    rsa.PersistKeyInCsp=false;rsa.FromXmlString(publicKeyXml);
                    if(rsa.KeySize<3072||rsa.KeySize>8192||!rsa.VerifyData(message,CryptoConfig.MapNameToOID("SHA256"),signature))return false;
                }
                string json=new UTF8Encoding(false,true).GetString(payload);
                var data=new JavaScriptSerializer{MaxJsonLength=2048}.Deserialize<Dictionary<string,object>>(json);
                if(data==null||data.Count!=5||Integer(data["v"])!=2||!object.Equals(data["product"],"dagami-agent-proxy"))return false;
                Guid id;if(!(data["id"] is string)||!Guid.TryParseExact((string)data["id"],"N",out id))return false;
                long issued=Integer(data["iat"]),expires=Integer(data["exp"]);
                long now=(long)(utcNow.ToUniversalTime()-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
                return issued>0&&issued<=now+300&&(expires==0||(expires>issued&&expires>now));
            } catch(FormatException){return false;}catch(ArgumentException){return false;}catch(CryptographicException){return false;}
            catch(KeyNotFoundException){return false;}catch(InvalidOperationException){return false;}catch(OverflowException){return false;}
        }
    }
}
