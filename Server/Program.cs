using System;
using System.Threading;
using Sanguine.Server;

namespace Sanguine.DedicatedServer
{
    /// <summary>Headless dedicated server entry point (no Unity required).</summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--help") >= 0)
            {
                Console.WriteLine("Sanguine City dedicated server");
                Console.WriteLine("  --port <n>         TCP port (default " + GameRules.DefaultPort + ")");
                Console.WriteLine("  --seed <n>         city layout seed (clients receive it on login)");
                Console.WriteLine("  --save <dir>       save directory (default ./save)");
                Console.WriteLine("  --night <sec>      night length in seconds (default 900)");
                Console.WriteLine("  --day <sec>        day length in seconds (default 240)");
                Console.WriteLine("  --mortals <n>      mortals per district (default 22)");
                Console.WriteLine("  --max-players <n>  player cap (default 64)");
                Console.WriteLine("  --debug            enable debug commands (/tp, /dawn, ...)");
                return 0;
            }

            var cfg = ServerConfig.FromArgs(args);
            var server = new GameServer(cfg);
            server.Start();

            var quit = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => server.Stop();
            var console = new Thread(() =>
            {
                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    line = line.Trim().ToLowerInvariant();
                    if (line == "quit" || line == "stop" || line == "exit") break;
                    if (line == "status") Console.WriteLine("Players online: " + server.OnlinePlayers);
                }
                // Only an explicit command stops the server; a closed stdin (e.g. running as a service) does not.
                if (line != null) quit.Set();
            }) { IsBackground = true, Name = "console" };
            console.Start();
            Console.WriteLine("Type 'status' for player count, 'quit' (or Ctrl+C) to stop.");
            quit.WaitOne();
            server.Stop();
            return 0;
        }
    }
}
