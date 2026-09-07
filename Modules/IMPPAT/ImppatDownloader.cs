using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ELB_DockTool
{
    /// <summary>
    /// IMPPAT 2.0 bulk downloader.
    /// C# port of the supplied imppat_downloader.py source.
    /// Keeps the original IMPPAT filtering, property display and download behavior.
    /// </summary>
    public sealed class ImppatDownloader
    {
        private const string BaseUrl = "https://cb.imsc.res.in/imppat";
        private const string PlantSearchUrl = BaseUrl + "/phytochemical/{0}";
        private const string DruglikeUrl = BaseUrl + "/druglikeproperties/{0}";
        private const string PhyschemUrl = BaseUrl + "/physicochemicalproperties/{0}";
        private const string AdmetUrl = BaseUrl + "/admetproperties/{0}";
        private const double DefaultDelay = 0.5;

        private static readonly Dictionary<string, (string Keyword, string Pass)> FilterRows =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["lipinski"] = ("lipinski", "passed"),
                ["ghose"] = ("ghose", "passed"),
                ["veber"] = ("veber", "good"),
                ["egan"] = ("egan", "good"),
                ["gsk"] = ("gsk", "good"),
                ["pfizer"] = ("pfizer", "good")
            };

        private static readonly Dictionary<string, string> ShowLabels =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["physchem"] = "Physicochemical",
                ["druglike"] = "Drug-likeness",
                ["admet"] = "ADMET"
            };

        private static readonly Dictionary<string, string> ShowUrls =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["physchem"] = PhyschemUrl,
                ["druglike"] = DruglikeUrl,
                ["admet"] = AdmetUrl
            };

        private readonly HttpClient _http;

        public ImppatDownloader()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        }

        public async Task<Dictionary<string, string>?> ParsePropertyTableAsync(string url)
        {
            try
            {
                using var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string html = await response.Content.ReadAsStringAsync();
                return ParsePropertyTable(html);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> ParsePropertyTable(string html)
        {
            var data = new Dictionary<string, string>();
            foreach (Match row in Regex.Matches(html, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var cells = Regex.Matches(row.Groups[1].Value, @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (cells.Count < 3) continue;

                string key = StripHtml(cells[0].Groups[1].Value).Trim();
                string val = StripHtml(cells[2].Groups[1].Value).Trim();

                if (!string.IsNullOrWhiteSpace(key))
                    data[key] = val;
            }
            return data;
        }

        public async Task ShowPropertiesAsync(string imphyId, IEnumerable<string> showOpts)
        {
            foreach (string tab in showOpts)
            {
                if (!ShowLabels.TryGetValue(tab, out string? label) ||
                    !ShowUrls.TryGetValue(tab, out string? template))
                    continue;

                string url = string.Format(template, Uri.EscapeDataString(imphyId));
                var data = await ParsePropertyTableAsync(url);

                Console.WriteLine($"\n  -- {label} --");
                if (data == null || data.Count == 0)
                {
                    Console.WriteLine("    (no data)");
                    continue;
                }

                foreach (var kv in data)
                    Console.WriteLine($"    {kv.Key,-55} {kv.Value}");
            }
        }

        public async Task<List<Compound>> FetchPhytochemicalIdsAsync(string plantName)
        {
            string url = string.Format(PlantSearchUrl, Uri.EscapeDataString(plantName));
            Console.WriteLine($"\n[*] Querying IMPPAT for: {plantName}");

            var ids = new List<Compound>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int start = 0, length = 100, draw = 1;
            int? total = null;

            while (true)
            {
                try
                {
                    using var content = new FormUrlEncodedContent(BuildDataTablesPayload(draw, start, length));
                    using var response = await _http.PostAsync(url, content);
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync();

                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;

                    if (total == null && root.TryGetProperty("recordsTotal", out JsonElement totalEl))
                    {
                        total = totalEl.GetInt32();
                        Console.WriteLine($"[+] Total compounds reported: {total}");
                    }

                    if (!root.TryGetProperty("data", out JsonElement rows) ||
                        rows.ValueKind != JsonValueKind.Array ||
                        rows.GetArrayLength() == 0)
                        break;

                    foreach (JsonElement row in rows.EnumerateArray())
                    {
                        string cellId = "";
                        string cellName = "";

                        if (row.ValueKind == JsonValueKind.Array)
                        {
                            if (row.GetArrayLength() > 2) cellId = row[2].ToString();
                            if (row.GetArrayLength() > 3) cellName = row[3].ToString();
                        }
                        else if (row.ValueKind == JsonValueKind.Object)
                        {
                            if (row.TryGetProperty("2", out var idEl)) cellId = idEl.ToString();
                            if (row.TryGetProperty("3", out var nameEl)) cellName = nameEl.ToString();
                        }

                        Match? m = Regex.Match(cellId, @"IMPHY\d+", RegexOptions.IgnoreCase);
                        if (!m.Success) m = Regex.Match(cellName, @"IMPHY\d+", RegexOptions.IgnoreCase);
                        if (!m.Success) continue;

                        string imphyId = m.Value.ToUpperInvariant();
                        string name = StripHtml(cellName).Trim();
                        if (string.IsNullOrWhiteSpace(name)) name = imphyId;

                        if (seen.Add(imphyId))
                            ids.Add(new Compound(imphyId, name));
                    }

                    start += length;
                    draw++;

                    if (total.HasValue && ids.Count >= total.Value)
                        break;
                }
                catch (Exception)
                {
                    Console.WriteLine("[!] DataTables AJAX unavailable -- falling back to HTML scrape");
                    try
                    {
                        string html = await _http.GetStringAsync(url);
                        foreach (Match a in Regex.Matches(html, @"<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
                                 RegexOptions.Singleline | RegexOptions.IgnoreCase))
                        {
                            Match m = Regex.Match(a.Groups[1].Value, @"IMPHY\d+", RegexOptions.IgnoreCase);
                            if (!m.Success) continue;

                            string imphyId = m.Value.ToUpperInvariant();
                            if (seen.Add(imphyId))
                                ids.Add(new Compound(imphyId, StripHtml(a.Groups[2].Value).Trim()));
                        }
                    }
                    catch { }
                    break;
                }
            }

            Console.WriteLine($"[+] Collected {ids.Count} unique compounds");
            return ids;
        }

        private static Dictionary<string, string> BuildDataTablesPayload(int draw, int start, int length)
        {
            var p = new Dictionary<string, string>
            {
                ["draw"] = draw.ToString(),
                ["start"] = start.ToString(),
                ["length"] = length.ToString(),
                ["search[value]"] = "",
                ["search[regex]"] = "false",
                ["order[0][column]"] = "0",
                ["order[0][dir]"] = "asc"
            };

            for (int i = 0; i < 5; i++)
            {
                p[$"columns[{i}][data]"] = i.ToString();
                p[$"columns[{i}][searchable]"] = "true";
                p[$"columns[{i}][orderable]"] = "true";
                p[$"columns[{i}][search][value]"] = "";
                p[$"columns[{i}][search][regex]"] = "false";
            }
            return p;
        }

        public static string BuildDownloadUrl(string imphyId, string format)
        {
            return format.ToLowerInvariant() switch
            {
                "pdbqt" => $"{BaseUrl}/images/3D/PDBQT/{imphyId}_3D.pdbqt",
                "sdf" => $"{BaseUrl}/images/3D/SDF/{imphyId}_3D.sdf",
                _ => throw new ArgumentException($"Unsupported format: {format}")
            };
        }

        public async Task<string> DownloadFileAsync(string url, string dest)
        {
            if (File.Exists(dest))
            {
                Console.WriteLine($"    [skip] {Path.GetFileName(dest)}  (already downloaded)");
                return "skip";
            }

            try
            {
                using var response = await _http.GetAsync(url);
                if ((int)response.StatusCode == 404)
                {
                    Console.WriteLine($"    [miss] {Path.GetFileName(dest)}  (404 - not on server)");
                    return "miss";
                }

                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(dest, bytes);
                Console.WriteLine($"    [OK]   {Path.GetFileName(dest)}");
                return "ok";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [ERR]  {Path.GetFileName(dest)}  - {ex.Message}");
                return "err";
            }
        }

        public async Task<Dictionary<string, bool>?> CheckDruglikenessAsync(
            string imphyId, IEnumerable<string> filters)
        {
            string url = string.Format(DruglikeUrl, Uri.EscapeDataString(imphyId));

            try
            {
                using var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                string html = await response.Content.ReadAsStringAsync();

                var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

                foreach (Match row in Regex.Matches(html, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    var cells = Regex.Matches(row.Groups[1].Value, @"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (cells.Count < 3) continue;

                    string propName = StripHtml(cells[0].Groups[1].Value).Trim().ToLowerInvariant();
                    string propVal = StripHtml(cells[2].Groups[1].Value).Trim().ToLowerInvariant();

                    if (propName.StartsWith("number of"))
                        continue;

                    foreach (string key in filters)
                    {
                        if (results.ContainsKey(key) || !FilterRows.TryGetValue(key, out var rule))
                            continue;

                        if (propName.Contains(rule.Keyword, StringComparison.OrdinalIgnoreCase))
                            results[key] = propVal.Contains(rule.Pass, StringComparison.OrdinalIgnoreCase);
                    }
                }

                return results;
            }
            catch
            {
                return null;
            }
        }

        private static string StripHtml(string value)
        {
            string s = Regex.Replace(value, @"<script\b[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<style\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", " ");
            s = System.Net.WebUtility.HtmlDecode(s);
            return Regex.Replace(s, @"\s+", " ");
        }

        public async Task RunAsync(
            string? plant,
            string? id,
            IEnumerable<string>? show,
            IEnumerable<string>? filters,
            IEnumerable<string>? formats,
            bool noDownload,
            double delaySeconds = DefaultDelay)
        {
            var showOpts = (show ?? Array.Empty<string>()).Select(x => x.ToLowerInvariant()).ToList();
            var activeFilters = (filters ?? Array.Empty<string>()).Select(x => x.ToLowerInvariant()).ToList();
            var fmts = (formats ?? new[] { "pdbqt" }).Select(x => x.ToLowerInvariant()).ToList();

            if (!string.IsNullOrWhiteSpace(id))
            {
                string imphyId = id.Trim().ToUpperInvariant();
                Console.WriteLine($"\n[*] Single compound: {imphyId}");

                if (showOpts.Count > 0)
                    await ShowPropertiesAsync(imphyId, showOpts);

                if (!noDownload)
                {
                    string outDir = "IMPPAT_OUTPUT";
                    Directory.CreateDirectory(outDir);
                    Console.WriteLine();

                    foreach (string fmt in fmts)
                    {
                        string url = BuildDownloadUrl(imphyId, fmt);
                        string dest = Path.Combine(outDir, $"{imphyId}.{fmt}");
                        await DownloadFileAsync(url, dest);
                        await Delay(delaySeconds);
                    }
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(plant))
                throw new ArgumentException("Either plant or id must be provided.");

            List<Compound> compounds = await FetchPhytochemicalIdsAsync(plant);
            if (compounds.Count == 0)
            {
                Console.WriteLine("[!] No compounds found. Check the plant name spelling.");
                return;
            }

            string outFolder = $"IMPPAT_{plant.Replace(' ', '_')}";
            if (!noDownload)
                Directory.CreateDirectory(outFolder);

            int ok = 0, fail = 0, skipFilter = 0;

            Console.WriteLine($"\n[*] Output folder : {outFolder}/");
            if (activeFilters.Count > 0)
                Console.WriteLine($"[*] Active filters: {string.Join(", ", activeFilters)}");
            if (showOpts.Count > 0)
                Console.WriteLine($"[*] Show tabs     : {string.Join(", ", showOpts)}");
            Console.WriteLine($"[*] Formats       : {string.Join(", ", fmts)}");
            if (noDownload)
                Console.WriteLine("[*] --no-download : structure files will NOT be saved");

            Console.WriteLine($"\n{new string('─', 55)}");

            for (int i = 0; i < compounds.Count; i++)
            {
                Compound compound = compounds[i];
                Console.WriteLine($"\n[{i + 1}/{compounds.Count}] {compound.Id}  {compound.Name}");

                if (activeFilters.Count > 0)
                {
                    var props = await CheckDruglikenessAsync(compound.Id, activeFilters);
                    await Delay(delaySeconds);

                    if (props == null)
                    {
                        Console.WriteLine("    [skip] could not fetch drug-likeness page");
                        skipFilter++;
                        continue;
                    }

                    var failed = activeFilters.Where(f => !props.TryGetValue(f, out bool passed) || !passed).ToList();
                    if (failed.Count > 0)
                    {
                        Console.WriteLine($"    [skip] failed: {string.Join(", ", failed)}");
                        skipFilter++;
                        continue;
                    }

                    Console.WriteLine($"    [pass] {string.Join(", ", activeFilters)}");
                }

                if (showOpts.Count > 0)
                {
                    await ShowPropertiesAsync(compound.Id, showOpts);
                    await Delay(delaySeconds);
                }

                if (!noDownload)
                {
                    foreach (string fmt in fmts)
                    {
                        string url = BuildDownloadUrl(compound.Id, fmt);
                        string dest = Path.Combine(outFolder, $"{compound.Id}.{fmt}");
                        string result = await DownloadFileAsync(url, dest);
                        if (result == "ok") ok++;
                        else if (result is "miss" or "err") fail++;
                        await Delay(delaySeconds);
                    }
                }
            }

            Console.WriteLine($"\n{new string('=', 55)}");
            Console.WriteLine($"Plant      : {plant}");
            if (!noDownload)
            {
                Console.WriteLine($"Downloaded : {ok}");
                Console.WriteLine($"Missing    : {fail}");
            }
            Console.WriteLine($"Filtered   : {skipFilter}");
            Console.WriteLine(new string('=', 55));
        }

        private static Task Delay(double seconds)
        {
            if (seconds <= 0) return Task.CompletedTask;
            return Task.Delay(TimeSpan.FromSeconds(seconds));
        }

        public sealed record Compound(string Id, string Name);
    }
}
