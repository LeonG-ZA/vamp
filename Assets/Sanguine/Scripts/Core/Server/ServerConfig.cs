using System;

namespace Sanguine.Server
{
    public sealed class ServerConfig
    {
        public int Port = GameRules.DefaultPort;
        public int CitySeed = 1931;
        public int TickRate = 20;
        public int SnapshotRate = 10;
        public int MaxPlayers = 64;
        public int MortalsPerDistrict = 22;

        /// <summary>Seconds of night followed by seconds of day. A full cycle is one "night" in the city's politics.</summary>
        public float NightSeconds = 900f;
        public float DaySeconds = 240f;

        public float ThrallMissionSeconds = 90f;
        public float AutosaveSeconds = 60f;

        /// <summary>Directory for player profiles and world state. Empty disables persistence.</summary>
        public string SaveDirectory = "save";

        /// <summary>Enables /tp, /dawn, /setexposure etc. Never enable on a public server.</summary>
        public bool DebugCommands = false;

        /// <summary>Fixed RNG seed for the simulation (tests). 0 = time based.</summary>
        public int SimSeed = 0;

        public Action<string> Log = Console.WriteLine;

        public static ServerConfig FromArgs(string[] args)
        {
            var c = new ServerConfig();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--port": c.Port = int.Parse(next); i++; break;
                    case "--seed": c.CitySeed = int.Parse(next); i++; break;
                    case "--save": c.SaveDirectory = next; i++; break;
                    case "--night": c.NightSeconds = float.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                    case "--day": c.DaySeconds = float.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                    case "--mortals": c.MortalsPerDistrict = int.Parse(next); i++; break;
                    case "--max-players": c.MaxPlayers = int.Parse(next); i++; break;
                    case "--debug": c.DebugCommands = true; break;
                }
            }
            return c;
        }
    }
}
