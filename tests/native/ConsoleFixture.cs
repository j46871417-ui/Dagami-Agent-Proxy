using System;
using System.IO;
using System.Threading;
class ConsoleFixture {
 static int Main(string[] args){Thread.Sleep(200);string input=Console.ReadLine();File.WriteAllText(args[0],input??"<no input>");Console.WriteLine("FIXTURE-COMPLETE");return input=="interactive-input"?0:17;}
}
