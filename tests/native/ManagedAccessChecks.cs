using System;
using DevProxyApp;

class ManagedAccessChecks {
    static int Main(string[] args) {
        try {
            string key = ManagedAccess.CreateKey();
            if (args.Length == 3 && args[0] == "proof") {
                Console.WriteLine(ManagedAccess.Sign(key, args[1], "CONNECT", args[2]));
                return 0;
            }
            string a = ManagedAccess.Sign(key, "gateway.test", "CONNECT", "example.test:443");
            string b = ManagedAccess.Sign(key, "gateway.test", "CONNECT", "example.test:443");
            if (!a.StartsWith("Dagami v1.") || a == b || a.Split('.')[1].Length != 87 || a.Split('.')[4].Length != 86)
                throw new Exception("Incorrect proof encoding or nonce reuse.");
            try { ManagedAccess.ParseEndpoint("https://user:password@example.test"); throw new Exception("Accepted credentials in endpoint."); }
            catch (ArgumentException) { }
            Console.WriteLine("PASS managed P-256 signatures, distinct nonces and endpoint validation");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
