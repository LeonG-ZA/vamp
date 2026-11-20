// Preview exporter: runs the real game server, scripts a short scene with bot clients and writes
// the live game state plus the complete CityDressing (what the Unity client builds) to JSON for the
// browser preview renderer (Tools/preview/render.html).
//   build:  see Tools/preview/README.md
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Sanguine;
using Sanguine.Server;
using Sanguine.Tests;

public static class ExportScene
{
    private static string F(float f) { return f.ToString("0.###", CultureInfo.InvariantCulture); }
    private static string S(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    public static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "scene.json";
        string save = Path.Combine(Path.GetTempPath(), "sanguine-preview-" + Guid.NewGuid().ToString("N"));
        var cfg = new ServerConfig { Port = 0, SaveDirectory = save, DebugCommands = true, SimSeed = 11, MortalsPerDistrict = 60, Log = m => { } };
        var server = new GameServer(cfg);
        server.Start();
        CityDressing dressing = CityDressing.Create(server.City);

        var sel = new Bot("Selene"); sel.Connect(server.Port, "pw123", Bloodline.Sylphine);
        var cor = new Bot("Corvin"); cor.Connect(server.Port, "pw123", Bloodline.Vorlen);
        sel.WaitFor(() => sel.Welcomed && sel.World != null, 5);
        cor.WaitFor(() => cor.Welcomed, 5);
        sel.Say("/circle create Velvet Knives");
        Thread.Sleep(1100);

        // Prefer a calm mortal in Midtown close to club neon.
        var neon = new List<BoxPiece>();
        foreach (BoxPiece b in dressing.Boxes) if (b.Material == SurfaceNames.Glow && b.Emission >= 3f) neon.Add(b);
        EntityState target = default(EntityState);
        float bestScore = float.MaxValue;
        for (int sweep = 0; sweep < 9; sweep++)
        {
            float tx = 25 + 50 * (sweep % 3), tz = 25 + 50 * (sweep / 3);
            sel.Say("/tp " + F(tx) + " " + F(tz));
            sel.WaitFor(() => false, 0.5);
            foreach (EntityState e in sel.Entities)
            {
                if (e.Kind != EntityKind.Mortal || e.Flags != EntityFlags.None || GameData.DistrictAt(e.X, e.Z) != 1) continue;
                float score = 1000f;
                foreach (BoxPiece n in neon) score = Math.Min(score, Entity.Dist(e.X, e.Z, n.CX, n.CZ));
                if (score < bestScore) { bestScore = score; target = e; }
            }
        }
        Console.WriteLine("target mortal " + target.Id + " at " + F(target.X) + "," + F(target.Z) + " (" + F(bestScore) + " m from neon)");

        sel.Say("/setexposure 52");
        Thread.Sleep(1100);
        float rot = 0f;
        for (int tries = 0; tries < 12 && !sel.Flag(SelfFlags.Feeding); tries++)
        {
            sel.Pump();
            foreach (EntityState e in sel.Entities) if (e.Id == target.Id) target = e;
            // Approach from the street side so the camera sees the feeding against the facades.
            float dx = 0.9f, dz = 0.45f;
            sel.Say("/tp " + F(target.X - dx) + " " + F(target.Z - dz));
            sel.WaitFor(() => false, 0.12);
            rot = (float)(Math.Atan2(dx, dz) * 180 / Math.PI);
            sel.Conn.Send(SimpleMsg.Move(sel.X, sel.Z, rot));
            sel.Conn.Send(SimpleMsg.FeedStart(target.Id));
            sel.WaitFor(() => sel.Flag(SelfFlags.Feeding), 0.6);
        }
        Console.WriteLine("feeding: " + sel.Flag(SelfFlags.Feeding));
        cor.Say("/tp " + F(target.X + 4.5f) + " " + F(target.Z - 6.5f));
        cor.WaitFor(() => false, 0.2);
        cor.Conn.Send(SimpleMsg.Move(cor.X, cor.Z, -35f));
        Thread.Sleep(1100);
        cor.Say("Careful, Selene. The Hush is thin tonight.");
        sel.WaitFor(() => false, 2.5);

        var sb = new StringBuilder("{\n");
        sb.Append("\"seed\":" + server.City.Seed + ",\n");
        sb.Append("\"me\":{\"id\":" + sel.Id + ",\"name\":\"Selene\",\"bloodline\":1,\"x\":" + F(sel.X) + ",\"z\":" + F(sel.Z) + ",\"rot\":" + F(rot) + ",\"target\":" + target.Id + "},\n");
        SelfStateMsg s = sel.Self;
        sb.Append("\"self\":{\"blood\":" + F(s.Blood) + ",\"maxBlood\":" + F(s.MaxBlood) + ",\"health\":" + F(s.Health) + ",\"maxHealth\":" + F(s.MaxHealth) +
                  ",\"conscience\":" + F(s.Conscience) + ",\"exposure\":" + F(s.Exposure) + ",\"standing\":" + s.Standing + ",\"flags\":" + (int)s.Flags +
                  ",\"power\":" + F(s.PowerCooldown) + ",\"attack\":" + F(s.AttackCooldown) + ",\"district\":" + s.District + "},\n");
        WorldStateMsg w = sel.World;
        sb.Append("\"world\":{\"isDay\":" + (w.IsDay ? "true" : "false") + ",\"toChange\":" + F(w.SecondsToChange) + ",\"night\":" + w.Night + ",\"phase\":" + F(w.CyclePhase) +
                  ",\"regent\":" + S(w.Regent) + ",\"edict\":" + S(w.Edict) + ",\"circle\":" + S(w.MyCircle) + ",\"controllers\":[" +
                  string.Join(",", Array.ConvertAll(w.DistrictControllers, S)) + "]},\n");
        sb.Append("\"entities\":[");
        bool first = true;
        foreach (EntityState e in sel.Entities)
        {
            if (e.Id == sel.Id) continue;
            EntityInfoMsg info;
            sel.Infos.TryGetValue(e.Id, out info);
            sb.Append(first ? "\n" : ",\n"); first = false;
            sb.Append("{\"id\":" + e.Id + ",\"kind\":" + (int)e.Kind + ",\"x\":" + F(e.X) + ",\"z\":" + F(e.Z) + ",\"rot\":" + F(e.Rot) + ",\"hp\":" + e.HealthPct +
                      ",\"flags\":" + (int)e.Flags + ",\"name\":" + S(info.Name) + ",\"bloodline\":" + (int)info.Bloodline + ",\"circle\":" + S(info.Circle) + "}");
        }
        sb.Append("],\n\"notices\":[" + string.Join(",", sel.Notices.ConvertAll(S).ToArray()) + "],\n");
        sb.Append("\"chat\":[" + string.Join(",", sel.Chat.ConvertAll(S).ToArray()) + "],\n");
        AppendDressing(sb, dressing);
        sb.Append("}\n");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine("wrote " + outPath + " (" + sel.Entities.Count + " entities, blood " + F(s.Blood) + ", exposure " + F(s.Exposure) + ")");
        server.Stop();
        try { Directory.Delete(save, true); } catch { }
        return 0;
    }

    private static void AppendDressing(StringBuilder sb, CityDressing d)
    {
        sb.Append("\"dressing\":{\n\"buildings\":[");
        for (int i = 0; i < d.Buildings.Count; i++)
        {
            DressedBuilding b = d.Buildings[i];
            sb.Append(i > 0 ? ",\n" : "\n").Append("{\"cx\":" + F(b.Def.CX) + ",\"cz\":" + F(b.Def.CZ) + ",\"sx\":" + F(b.Def.SX) + ",\"sz\":" + F(b.Def.SZ) +
                ",\"h\":" + F(b.Def.Height) + ",\"base\":" + F(b.BaseY) + ",\"band\":" + F(b.BandHeight) + ",\"gm\":" + S(b.GroundMaterial) +
                ",\"um\":" + S(b.UpperMaterial) + ",\"rm\":" + S(b.RoofMaterial) + ",\"tm\":" + S(b.TrimMaterial) + ",\"parapet\":" + F(b.ParapetHeight) +
                ",\"fascia\":" + (b.Fascia ? "true" : "false") + ",\"uo\":" + F(b.UOffset) + ",\"vo\":" + F(b.VOffset) + "}");
        }
        sb.Append("],\n\"slabs\":[");
        for (int i = 0; i < d.Slabs.Count; i++)
        {
            SlabPiece s = d.Slabs[i];
            sb.Append(i > 0 ? "," : "").Append("{\"x0\":" + F(s.MinX) + ",\"z0\":" + F(s.MinZ) + ",\"x1\":" + F(s.MaxX) + ",\"z1\":" + F(s.MaxZ) +
                ",\"h\":" + F(s.Height) + ",\"top\":" + S(s.TopMaterial) + ",\"side\":" + S(s.SideMaterial) + "}");
        }
        sb.Append("],\n\"surfaces\":[");
        for (int i = 0; i < d.Surfaces.Count; i++)
        {
            SurfacePiece s = d.Surfaces[i];
            sb.Append(i > 0 ? "," : "").Append("{\"x0\":" + F(s.MinX) + ",\"z0\":" + F(s.MinZ) + ",\"x1\":" + F(s.MaxX) + ",\"z1\":" + F(s.MaxZ) +
                ",\"y\":" + F(s.Y) + ",\"m\":" + S(s.Material) + "}");
        }
        sb.Append("],\n\"boxes\":[");
        for (int i = 0; i < d.Boxes.Count; i++)
        {
            BoxPiece b = d.Boxes[i];
            sb.Append(i > 0 ? ",\n" : "\n").Append("{\"cx\":" + F(b.CX) + ",\"cy\":" + F(b.CY) + ",\"cz\":" + F(b.CZ) + ",\"sx\":" + F(b.SX) + ",\"sy\":" + F(b.SY) +
                ",\"sz\":" + F(b.SZ) + ",\"yaw\":" + F(b.Yaw) + ",\"m\":" + S(b.Material) + ",\"r\":" + F(b.R) + ",\"g\":" + F(b.G) + ",\"b\":" + F(b.B) + ",\"e\":" + F(b.Emission) + "}");
        }
        sb.Append("],\n\"props\":[");
        for (int i = 0; i < d.Props.Count; i++)
        {
            PropPlacement p = d.Props[i];
            sb.Append(i > 0 ? ",\n" : "\n").Append("{\"id\":" + S(p.Id) + ",\"x\":" + F(p.X) + ",\"y\":" + F(p.Y) + ",\"z\":" + F(p.Z) + ",\"yaw\":" + F(p.Yaw) +
                ",\"sx\":" + F(p.ScaleX) + ",\"sy\":" + F(p.ScaleY) + ",\"sz\":" + F(p.ScaleZ) + "}");
        }
        sb.Append("],\n\"lights\":[");
        for (int i = 0; i < d.Lights.Count; i++)
        {
            LightPlacement l = d.Lights[i];
            sb.Append(i > 0 ? ",\n" : "\n").Append("{\"k\":" + (int)l.Kind + ",\"x\":" + F(l.X) + ",\"y\":" + F(l.Y) + ",\"z\":" + F(l.Z) + ",\"r\":" + F(l.R) +
                ",\"g\":" + F(l.G) + ",\"b\":" + F(l.B) + ",\"range\":" + F(l.Range) + ",\"i\":" + F(l.Intensity) + "}");
        }
        sb.Append("],\n\"stripes\":[");
        for (int i = 0; i < d.Stripes.Count; i++)
        {
            Stripe st = d.Stripes[i];
            sb.Append(i > 0 ? "," : "").Append("[" + F(st.CX) + "," + F(st.CZ) + "," + F(st.SX) + "," + F(st.SZ) + "]");
        }
        sb.Append("]\n}\n");
    }
}
