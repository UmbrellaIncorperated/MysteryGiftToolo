using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using PKHeX.Core;

namespace MysteryGiftTool
{
    internal class Game
    {
        public string Name;
        public string ID;
        public int Generation;
    }

    internal static class Program
    {
        private static DateTime now = DateTime.Now;
        private static bool keep_log;
        private static StreamWriter log;
        private const string filelist_server = "https://npfl.c.app.nintendowifi.net/p01/filelist/{0}/FGONLYT";
        private const string file_server = "https://npdl.cdn.nintendowifi.net/p01/nsa/{0}/FGONLYT/{1}";
        private static readonly CTR.AesEngine engine = new CTR.AesEngine();

        private static readonly Game[] games =
        {
            new Game {Name = "Bank", ID = "vgBivYesOH9RS5I8", Generation=7 },
            new Game {Name = "UltraMoon",ID= "b3Gq6LF6EqE1bvKy", Generation=7},
            new Game {Name = "UltraSun", ID= "fnCAH3KrGIl9dgSd", Generation=7 },
            new Game {Name = "Sun", ID = "8QjtffIMWFhiFpTz", Generation = 7},
            new Game {Name = "Moon", ID = "7mXz0DXR4b4CdD8r", Generation = 7},
            new Game {Name = "X", ID = "h0VRqB2YEgq39zvO", Generation = 6},
            new Game {Name = "Y", ID = "Slv7vHlUOfqrKMpz", Generation = 6},
            new Game {Name = "Omega Ruby", ID = "cRFY0WFHNjPh44If", Generation = 6},
            new Game {Name = "Alpha Sapphire", ID = "guBwm9TlQvYvncKn", Generation = 6}
        };

        public static void CreateDirectoryIfNull(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        public static void Log(string msg)
        {
            Console.WriteLine(msg);
            log?.WriteLine(msg);
        }

        private static bool LoadBoot9()
        {
            if (engine.IsBootRomLoaded) return true;
            try
            {
                if (File.Exists("boot9.bin"))
                    engine.LoadKeysFromBootromFile(File.ReadAllBytes("boot9.bin"));
                else if (File.Exists("boot9_prot.bin"))
                    engine.LoadKeysFromBootromFile(File.ReadAllBytes("boot9_prot.bin"));
            }
            catch
            {
                return false;
            }
            return engine.IsBootRomLoaded;
        }

        private static void Main(string[] args)
        {
            CreateDirectoryIfNull("logs");
            CreateDirectoryIfNull("data");
            CreateDirectoryIfNull("wondercards");
            CreateDirectoryIfNull("regulations");
            CreateDirectoryIfNull("cups");
            foreach (var game in games)
                CreateDirectoryIfNull(Path.Combine("data", game.Name));

            var log_file = $"logs/{now:MMMM dd, yyyy - HH-mm-ss}.log";
            log = new StreamWriter(log_file, false, Encoding.Unicode) { AutoFlush = true };

            Log("MysteryGiftTool v1.0 - SciresM");
            Log($"{now:MMMM dd, yyyy - HH-mm-ss}");

            // The original set only SecurityProtocolType.Tls (TLS 1.0), which modern Windows
            // and .NET will often refuse outright.
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            ServicePointManager.DefaultConnectionLimit = 8;
            Log("Installed certificate bypass.");

            try
            {
                UpdateArchives();
                Log("Loading 3DS arm9 bootrom...");

                if (LoadBoot9())
                {
                    keep_log = true;
                    Log("Decrypting and extracting gifts...");
                    GameInfo.Strings = GameInfo.GetStrings("en");
                    ExtractArchives();
                }
                else
                {
                    keep_log = true;
                    Log("No boot9.bin / boot9_prot.bin found - archives downloaded but not decrypted.");
                }
            }
            catch (Exception ex)
            {
                keep_log = true;
                Log($"An exception occurred: {ex}");
            }

            log.Close();
            log = null;
            if (!keep_log)
                File.Delete(log_file);
        }

        private static List<BossMetadata> ParseList(string fl)
        {
            var list = new List<BossMetadata>();
            if (string.IsNullOrEmpty(fl))
                return list;

            foreach (var line in fl.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("\t"))
                    continue;
                try
                {
                    list.Add(BossMetadata.FromString(line));
                }
                catch (ArgumentException ex)
                {
                    // One malformed line used to abort the whole run.
                    Log($"  skipping unparseable list entry: {ex.Message}");
                }
            }
            return list;
        }

        private static void UpdateArchives()
        {
            foreach (var game in games)
            {
                Log($"Updating for {game.Name}...");
                var game_dir = Path.Combine("data", game.Name);
                var fl_path = Path.Combine(game_dir, "list.txt");
                var server_filelist = string.Format(filelist_server, game.ID);

                var listRes = NetworkUtils.RequestWithRetry(server_filelist);

                string fl;
                if (listRes.Success)
                {
                    fl = listRes.Text;
                }
                else
                {
                    keep_log = true;
                    Log($"Could not fetch the file list for {game.Name} ({listRes.Error}).");
                    if (!File.Exists(fl_path))
                        continue;
                    // Fall back to the cached list so previously failed archives still get retried.
                    fl = File.ReadAllText(fl_path);
                    Log($"Falling back to the cached list for {game.Name}.");
                }

                var old_fl = File.Exists(fl_path) ? File.ReadAllText(fl_path) : "";
                if (old_fl != fl)
                    keep_log = true;

                var new_boss = ParseList(fl);
                if (new_boss.Count == 0)
                {
                    Log($"File list for {game.Name} has no usable entries - the server may have returned an error page. Not overwriting list.txt.");
                    continue;
                }
                var old_boss = ParseList(old_fl);

                var archive_dir = Path.Combine(game_dir, "boss");
                CreateDirectoryIfNull(archive_dir);

                int have = 0, got = 0;
                var failures = new List<string>();

                // No download cap. The original broke out of this loop after 11 new files.
                foreach (var boss in new_boss)
                {
                    var archive_path = Path.Combine(archive_dir, boss.ArchiveName);
                    if (File.Exists(archive_path))
                    {
                        have++;
                        continue;
                    }

                    var url = string.Format(file_server, game.ID, Uri.EscapeDataString(boss.Name));
                    var dl = NetworkUtils.RequestWithRetry(url);
                    if (!dl.Success)
                    {
                        Log($"FAILED {boss.FileName}: {dl.Error}");
                        failures.Add($"{boss.Name}\t{dl.Error}\t{url}");
                        continue;
                    }
                    if (dl.Data == null || dl.Data.Length == 0)
                    {
                        Log($"FAILED {boss.FileName}: empty response body");
                        failures.Add($"{boss.Name}\tempty response\t{url}");
                        continue;
                    }

                    // Write to a temp name first so an interrupted run can't leave a truncated
                    // archive that later runs will skip because File.Exists returns true.
                    var tmp = archive_path + ".part";
                    File.WriteAllBytes(tmp, dl.Data);
                    if (File.Exists(archive_path)) File.Delete(archive_path);
                    File.Move(tmp, archive_path);

                    got++;
                    Log($"Downloaded {boss.FileName} ({dl.Data.Length} bytes).");
                    if (old_boss.Any(bm => boss.IsUpdatedVersionOf(bm)))
                        Log($"{boss.FileName} is an updated version of an old archive!");

                    Thread.Sleep(NetworkUtils.RequestDelayMs);
                }

                Log($"{game.Name}: {new_boss.Count} listed, {have} already present, {got} downloaded, {failures.Count} failed.");

                var fail_path = Path.Combine(game_dir, "failed.txt");
                if (failures.Count > 0)
                {
                    keep_log = true;
                    File.WriteAllLines(fail_path, failures);
                    Log($"Wrote {failures.Count} failures to {fail_path}. list.txt left unchanged so these are retried next run.");
                }
                else
                {
                    if (File.Exists(fail_path)) File.Delete(fail_path);
                    // Only commit the new list once every archive on it is on disk. The original
                    // wrote it up front, so anything that 403'd was never attempted again.
                    if (listRes.Success)
                        File.WriteAllText(fl_path, fl);
                }
            }
        }

        private static void ExtractArchives()
        {
            foreach (var game in games)
            {
                Log($"Extracting archives for {game.Name}...");
                var game_dir = Path.Combine("data", game.Name);
                var archive_dir = Path.Combine(game_dir, "boss");
                var dec_dir = Path.Combine(game_dir, "boss_dec");
                CreateDirectoryIfNull(archive_dir);
                CreateDirectoryIfNull(dec_dir);

                foreach (var file in new DirectoryInfo(archive_dir).GetFiles())
                {
                    if (!file.Name.Contains("-_-"))
                        continue;
                    if (file.Extension == ".part")
                        continue;

                    BossMetadata boss;
                    try
                    {
                        boss = BossMetadata.FromArchiveName(file.Name);
                    }
                    catch (ArgumentException ex)
                    {
                        Log($"Skipping {file.Name}: {ex.Message}");
                        continue;
                    }

                    var dec_path = Path.Combine(dec_dir, boss.FileName);
                    if (File.Exists(dec_path))
                        continue;

                    Log($"Decrypting {boss.FileName}...");
                    byte[] dec_data;
                    try
                    {
                        dec_data = engine.DecryptBOSS(File.ReadAllBytes(file.FullName));
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to decrypt {boss.FileName}: {ex.Message}");
                        continue;
                    }

                    if (dec_data == null || dec_data.Length <= 0x296)
                    {
                        Log($"Failed to decrypt {boss.FileName} (bad or truncated archive - consider deleting it and redownloading).");
                        continue;
                    }

                    Log($"Decrypted {boss.FileName}.");
                    File.WriteAllBytes(dec_path, dec_data);

                    var contentData = new byte[dec_data.Length - 0x296];
                    Buffer.BlockCopy(dec_data, 0x296, contentData, 0, contentData.Length);

                    if (contentData.Length > 0 && contentData.Length % 0x310 == 0) // Wondercard(s)
                    {
                        var wcgdir = Path.Combine("wondercards", game.Name);
                        var wcdir = Path.Combine(wcgdir, $"wc{game.Generation}");
                        var wcfulldir = Path.Combine(wcgdir, $"wc{game.Generation}full");
                        CreateDirectoryIfNull(wcgdir);
                        CreateDirectoryIfNull(wcdir);
                        CreateDirectoryIfNull(wcfulldir);

                        var count = 0;
                        // Indexed walk instead of repeated Skip().ToArray(), which reallocated
                        // the whole remaining buffer once per card.
                        for (var off = 0; off + 0x310 <= contentData.Length; off += 0x310)
                        {
                            count++;
                            var currentWc = new byte[0x310];
                            Buffer.BlockCopy(contentData, off, currentWc, 0, 0x310);

                            var version = GetGameVersion(currentWc, game.Generation);
                            var stem = $"{boss.FileName}_{version}_{count}";

                            File.WriteAllBytes(
                                Path.Combine(wcfulldir, $"{stem}.wc{game.Generation}full"), currentWc);

                            MysteryGift wc = null;
                            try
                            {
                                if (game.Generation == 6)
                                    wc = new WC6(currentWc);
                                else if (game.Generation == 7)
                                    wc = new WC7(currentWc);
                            }
                            catch (Exception ex)
                            {
                                Log($"{stem}: could not parse as a wondercard ({ex.Message}); full card saved.");
                                continue;
                            }

                            if (wc == null)
                            {
                                Log($"{stem}: generation {game.Generation} has no wondercard class; full card saved.");
                                continue;
                            }

                            File.WriteAllBytes(
                                Path.Combine(wcdir, $"{stem}.wc{game.Generation}"), wc.Data);

                            Log($"{boss.FileName} ({count}) is a wondercard ({wc.Type}): ");
                            Log(GetWonderCardDescription(wc));
                        }

                        Log($"Found WCs: {count}.");
                    }
                    else if (boss.Name.ToUpper().Contains("CUP") && contentData.Length == 0x4C0) // CUP Regulation
                    {
                        Log($"{boss.FileName} is a CUP!");
                        var cup_dir = Path.Combine("cups", game.Name);
                        CreateDirectoryIfNull(cup_dir);
                        var reg_arc = new RegulationArchive(contentData, boss.FileName);
                        Log($"Extracting/Saving {boss.FileName}...");
                        reg_arc.Save(cup_dir);
                    }
                    else if (boss.Name.Contains("regulation") && game.Generation == 7) // Gen VII Regulations
                    {
                        Log($"{boss.FileName} is a regulation!");
                        var reg_dir = Path.Combine("regulations", game.Name);
                        CreateDirectoryIfNull(reg_dir);
                        var reg_arc = new RegulationArchive(contentData, boss.FileName);
                        Log($"Extracting/Saving {boss.FileName}...");
                        reg_arc.Save(reg_dir);
                    }
                    else
                    {
                        Log($"{boss.FileName} {contentData.Length} unknown file format");
                    }
                }
            }
        }

        private static string GetGameVersion(byte[] data, int Generation)
        {
            if (data == null || data.Length < 2)
                return "UNKNOWN";

            var gameInt = BitConverter.ToInt16(data, 0);

            if (Generation == 6) // XY ORAS
            {
                switch (gameInt)
                {
                    case 1: return "X";
                    case 2: return "Y";
                    case 3: return "XY";
                    case 4: return "AS";
                    case 8: return "OR";
                    case 12: return "ORAS";
                    case 15: return "XYORAS";
                    default: return "UNKNOWN";
                }
            }

            // SM USUM
            switch (gameInt)
            {
                case 1: return "S";
                case 2: return "M";
                case 3: return "SM";
                case 4: return "US";
                case 8: return "UM";
                case 12: return "USUM";
                case 15: return "SMUSUM";
                default: return "UNKNOWN";
            }
        }

        private static string GetWonderCardDescription(MysteryGift gift)
        {
            if (gift.Empty)
                return "Empty Slot. No data!";

            string s = gift.CardHeader + Environment.NewLine;
            if (gift.IsItem)
            {
                s += "Item: " + GameInfo.Strings.itemlist[gift.ItemID] + Environment.NewLine + "Quantity: " + gift.Quantity + Environment.NewLine;
            }
            else if (gift.IsPokémon)
            {
                var pk = gift.ConvertToPKM(new SAV7());

                try
                {
                    s += $"{GameInfo.Strings.specieslist[pk.Species]} @ {GameInfo.Strings.itemlist[pk.HeldItem]}  --- ";
                    s += (pk.IsEgg ? GameInfo.Strings.eggname : $"{pk.OT_Name} - {pk.TID:00000}/{pk.SID:00000}") + Environment.NewLine;
                    s += $"{GameInfo.Strings.movelist[pk.Move1]} / {GameInfo.Strings.movelist[pk.Move2]} / {GameInfo.Strings.movelist[pk.Move3]} / {GameInfo.Strings.movelist[pk.Move4]}" + Environment.NewLine;
                    if (gift is WC7)
                    {
                        var addItem = ((WC7)gift).AdditionalItem;
                        if (addItem != 0)
                            s += $"+ {GameInfo.Strings.itemlist[addItem]}" + Environment.NewLine;
                    }
                }
                catch { s += "Unable to create gift description." + Environment.NewLine; }
            }
            else { s += "Unknown Wonder Card Type!" + Environment.NewLine; }

            if (gift is WC7)
            {
                var wc7 = (WC7)gift;
                s += $"Repeatable: {wc7.GiftRepeatable}" + Environment.NewLine;
                s += $"Collected: {wc7.GiftUsed}" + Environment.NewLine;
                s += $"Once Per Day: {wc7.GiftOncePerDay}" + Environment.NewLine;
            }
            return s;
        }
    }
}
