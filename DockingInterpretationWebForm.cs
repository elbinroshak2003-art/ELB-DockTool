using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ELB_DockTool;

internal sealed class DockingInterpretationWebForm : Form
{
    readonly string receptorInput, resultInput;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    string? sessionDir;

    public DockingInterpretationWebForm(string receptorFile, string resultFile)
    {
        receptorInput = receptorFile;
        resultInput = resultFile;
        Text = $"ELB DockTool {Program.AppVersion} — Docking Interpretation";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1500, 900);
        MinimumSize = new Size(1180, 760);
        BackColor = Color.FromArgb(7, 16, 28);
        Controls.Add(web);
        Shown += async (_, _) => await InitializeViewerAsync();
        FormClosed += (_, _) => CleanupSession();
    }

    async Task InitializeViewerAsync()
    {
        try
        {
            string sourceViewer = Path.Combine(AppServices.BaseDir, "InterpretationViewer");
            if (!Directory.Exists(sourceViewer)) throw new DirectoryNotFoundException("InterpretationViewer folder was not found beside the application.");

            sessionDir = Path.Combine(AppServices.BaseDir, "workspace", "interpretation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            Directory.CreateDirectory(Path.Combine(sessionDir, "poses"));

            // The interpretation viewer receives display-friendly PDB coordinates.
            // The original Vina PDBQT remains available as the authoritative source for
            // pose scores/RMSD and as a fallback when conversion is unavailable.
            string receptorPdb = await EnsurePdbAsync(receptorInput, Path.Combine(sessionDir, "receptor.pdb"));
            if (!File.Exists(receptorPdb)) throw new InvalidOperationException("Could not create a displayable receptor PDB file.");
            File.Copy(receptorInput, Path.Combine(sessionDir, "receptor_source.pdbqt"), true);

            var poseInfo = ParseVinaPoses(resultInput);
            string splitDir = Path.Combine(Path.GetDirectoryName(resultInput) ?? "", "split");
            // Prefer already-converted split PDB files when the user has used
            // AUTO SPLIT ALL + CONVERT ALL → PDB. If those are not present, use the
            // split PDBQT files and convert them here. This keeps interpretation
            // independent of fragile browser-side PDBQT parsing.
            var splitPdbFiles = Directory.Exists(splitDir)
                ? Directory.EnumerateFiles(splitDir, "*_ligand_*.pdb", SearchOption.TopDirectoryOnly).OrderBy(ParsePoseNumberPdb).ToList()
                : new List<string>();
            var splitPdbqtFiles = Directory.Exists(splitDir)
                ? Directory.EnumerateFiles(splitDir, "*_ligand_*.pdbqt", SearchOption.TopDirectoryOnly).OrderBy(ParsePoseNumber).ToList()
                : new List<string>();

            // If the user has not manually split the result, make the viewer workflow
            // self-contained by splitting it when vina_split is available.
            if (splitPdbFiles.Count == 0 && splitPdbqtFiles.Count == 0)
                splitPdbqtFiles = await TrySplitForViewerAsync(resultInput, splitDir);

            var manifest = new List<ViewerPose>();
            foreach (var source in splitPdbFiles)
            {
                int n = ParsePoseNumberPdb(source);
                if (n < 1) continue;
                string outPdb = Path.Combine(sessionDir, "poses", $"pose_{n}.pdb");
                File.Copy(source, outPdb, true);
                var meta = poseInfo.FirstOrDefault(x => x.Number == n) ?? new VinaPoseMeta(n, double.NaN, double.NaN, double.NaN);
                manifest.Add(new ViewerPose(n, $"poses/pose_{n}.pdb", meta.Affinity, meta.Lb, meta.Ub));
            }
            var existingPoseNumbers = manifest.Select(x => x.Number).ToHashSet();
            foreach (var source in splitPdbqtFiles)
            {
                int n = ParsePoseNumber(source);
                if (n < 1 || existingPoseNumbers.Contains(n)) continue;
                string outPdb = Path.Combine(sessionDir, "poses", $"pose_{n}.pdb");
                if (!await TryConvertToPdbAsync(source, outPdb)) continue;
                var meta = poseInfo.FirstOrDefault(x => x.Number == n) ?? new VinaPoseMeta(n, double.NaN, double.NaN, double.NaN);
                manifest.Add(new ViewerPose(n, $"poses/pose_{n}.pdb", meta.Affinity, meta.Lb, meta.Ub));
                existingPoseNumbers.Add(n);
            }
            manifest = manifest.OrderBy(x => x.Number).ToList();

            // Fallback: if split/conversion is unavailable, convert the complete result
            // to PDB. The browser parser can still display it as one pose.
            if (manifest.Count == 0)
            {
                string fallback = Path.Combine(sessionDir, "poses", "pose_1.pdb");
                if (await TryConvertToPdbAsync(resultInput, fallback))
                {
                    var meta = poseInfo.FirstOrDefault() ?? new VinaPoseMeta(1, double.NaN, double.NaN, double.NaN);
                    manifest.Add(new ViewerPose(1, "poses/pose_1.pdb", meta.Affinity, meta.Lb, meta.Ub));
                }
            }

            if (manifest.Count == 0) throw new InvalidOperationException("The docking result could not be converted to a displayable PDB pose. Run AUTO SPLIT ALL and CONVERT ALL → PDB first, or configure Open Babel in Settings.");

            // Preserve Vina's authoritative pose metadata separately from display files.
            await File.WriteAllTextAsync(Path.Combine(sessionDir, "poses.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(resultInput, Path.Combine(sessionDir, "vina_result.pdbqt"), true);
            File.Copy(Path.Combine(sourceViewer, "index.html"), Path.Combine(sessionDir, "index.html"), true);

            // Embed the prepared display data in the session instead of using browser fetch()
            // for local PDB/JSON resources. This avoids WebView2 local-resource/CORS path
            // failures and makes the viewer independent of the user's current working folder.
            var viewerData = new
            {
                receptor = await File.ReadAllTextAsync(receptorPdb),
                poses = manifest.Select(m => new
                {
                    number = m.Number, file = m.File, affinity = m.Affinity, lb = m.Lb, ub = m.Ub,
                    pdb = File.ReadAllText(Path.Combine(sessionDir, m.File.Replace('/', Path.DirectorySeparatorChar)))
                }).ToList()
            };
            string dataJson = JsonSerializer.Serialize(viewerData, new JsonSerializerOptions { WriteIndented = false });
            await File.WriteAllTextAsync(Path.Combine(sessionDir, "session-data.js"), "window.ELB_DATA=" + dataJson + ";");

            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.elb-docktool", sessionDir, CoreWebView2HostResourceAccessKind.Allow);
            string label = Uri.EscapeDataString($"{Path.GetFileName(receptorInput)} + {Path.GetFileName(resultInput)}");
            web.Source = new Uri($"https://appassets.elb-docktool/index.html?{label}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Docking Interpretation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "savePdbComplex") return;
            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(content)) return;
            string suggested = root.TryGetProperty("filename", out var f) ? f.GetString() ?? "ELB_complex.pdb" : "ELB_complex.pdb";
            using var dlg = new SaveFileDialog
            {
                Title = "Save PDB Complex",
                Filter = "PDB structure (*.pdb)|*.pdb|All files (*.*)|*.*",
                FileName = Path.GetFileName(suggested),
                AddExtension = true,
                DefaultExt = "pdb",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(false));
            MessageBox.Show(this, $"PDB complex saved to:\n{dlg.FileName}", "ELB-DockTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the PDB complex.\n{ex.Message}", "ELB-DockTool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task<string> EnsurePdbAsync(string input, string output)
    {
        string ext = Path.GetExtension(input);
        if (ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ent", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(input, output, true);
            return output;
        }
        if (ext.Equals(".pdbqt", StringComparison.OrdinalIgnoreCase) && await TryConvertToPdbAsync(input, output)) return output;
        return "";
    }

    async Task<bool> TryConvertToPdbAsync(string input, string output)
    {
        var cfg = AppServices.LoadConfig();
        string obabel = AppServices.Resolve(cfg.OpenBabelPath);
        if (!File.Exists(obabel))
        {
            string? detected = AppServices.DetectExecutable("obabel.exe");
            if (!string.IsNullOrWhiteSpace(detected)) obabel = detected;
        }
        if (string.IsNullOrWhiteSpace(obabel) || !File.Exists(obabel)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        var r = await RunProcessAsync(obabel, $"-i {ext} {Q(input)} -opdb -O {Q(output)}", AppServices.BaseDir);
        return r.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
    }

    async Task<List<string>> TrySplitForViewerAsync(string result, string splitDir)
    {
        var cfg = AppServices.LoadConfig();
        string splitter = AppServices.Resolve(cfg.VinaSplitPath);
        if (!File.Exists(splitter))
        {
            string? detected = AppServices.DetectExecutable("vina_split.exe");
            if (!string.IsNullOrWhiteSpace(detected)) splitter = detected;
        }
        if (string.IsNullOrWhiteSpace(splitter) || !File.Exists(splitter)) return new List<string>();
        try
        {
            Directory.CreateDirectory(splitDir);
            var r = await RunProcessAsync(splitter, $"--input {Q(result)}", splitDir);
            if (r.ExitCode != 0) return new List<string>();
            var generated = Directory.EnumerateFiles(splitDir, "*_ligand_*.pdbqt", SearchOption.TopDirectoryOnly).OrderBy(ParsePoseNumber).ToList();
            return generated;
        }
        catch { return new List<string>(); }
    }

    static List<VinaPoseMeta> ParseVinaPoses(string file)
    {
        var list = new List<VinaPoseMeta>();
        if (!File.Exists(file)) return list;
        int model = 0;
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith("MODEL", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"MODEL\s+(\d+)");
                if (m.Success) model = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            if (line.StartsWith("REMARK VINA RESULT:", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"REMARK VINA RESULT:\s+([-+\d.]+)\s+([-+\d.]+)\s+([-+\d.]+)");
                if (m.Success)
                {
                    int n = model > 0 ? model : list.Count + 1;
                    list.Add(new VinaPoseMeta(n,
                        double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                        double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)));
                }
            }
        }
        return list;
    }

    static int ParsePoseNumber(string path)
    {
        var m = Regex.Match(Path.GetFileName(path), @"_ligand_(\d+)\.pdbqt$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
    }

    static int ParsePoseNumberPdb(string path)
    {
        var m = Regex.Match(Path.GetFileName(path), @"_ligand_(\d+)\.pdb$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : int.MaxValue;
    }

    static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(string file, string args, string work)
    {
        var psi = new ProcessStartInfo(file, args) { WorkingDirectory = work, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = new Process { StartInfo = psi };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }

    void CleanupSession()
    {
        try { if (!string.IsNullOrWhiteSpace(sessionDir) && Directory.Exists(sessionDir)) Directory.Delete(sessionDir, true); } catch { }
    }

    sealed record VinaPoseMeta(int Number, double Affinity, double Lb, double Ub);
    sealed record ViewerPose(int Number, string File, double Affinity, double Lb, double Ub);
}
