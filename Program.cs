using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Threading;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Drawing;

namespace ELB_DockTool;

internal static class Program
{
    public const string AppVersion = "v1.2.0";
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(
            e.Exception.Message + "\n\n" + e.Exception.StackTrace,
            "ELB DockTool — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                MessageBox.Show(ex.Message + "\n\n" + ex.StackTrace, "ELB DockTool — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        // Verify the password first. Show the original ELB-DOCK welcome intro only after successful verification.
        using var login = new LoginForm();
        if (login.ShowDialog() != DialogResult.OK) return;

        using var intro = new WelcomeForm();
        intro.ShowDialog();

        Application.Run(new MainForm());
    }
}

public sealed class AppConfig
{
    public string VinaPath { get; set; } = "dist\\vina.exe";
    public string VinaSplitPath { get; set; } = "dist\\vina_split.exe";
    public string OpenBabelPath { get; set; } = "";
    // Meeko is used for modern Vina-compatible receptor parameterization.
    // The Python interpreter is kept external so the proprietary ELB license does not
    // impose restrictions on the separate LGPL-licensed Meeko component.
    public string MeekoPythonPath { get; set; } = "";
    // This is only set when ELB creates its own isolated PDBFixer environment.
    // A global/user Python installation is never marked as ELB-owned.
    public string PdbFixerManagedEnvironment { get; set; } = "";
    public string OutputFolder { get; set; } = "output";
}

public static class AppServices
{
    public static readonly string BaseDir = AppContext.BaseDirectory;
    public static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ELB_DockTool");
    public static readonly string ConfigFile = Path.Combine(ConfigDir, "settings.json");

    public static AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFile)) ?? new AppConfig();
        }
        catch { }
        return new AppConfig();
    }

    public static void SaveConfig(AppConfig c)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDir, path));
    }

    public static bool Exists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(Resolve(path));

    public static string DetectExecutable(string fileName)
    {
        string[] roots =
        {
            BaseDir,
            Path.Combine(BaseDir, "dist"),
            Path.Combine(BaseDir, "tools"),
            Path.Combine(BaseDir, "bin")
        };

        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                string direct = Path.Combine(root, fileName);
                if (File.Exists(direct)) return direct;
                var found = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
            catch { }
        }

        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        catch { }

        return "";
    }
}

public sealed record PdbFixerStatus(
    string State, string Detail, string Python, string PythonVersion,
    string PdbFixerVersion, string OpenMmVersion, bool IsManaged, bool IsUsable)
{
    public static PdbFixerStatus NotInstalled(string detail) =>
        new("NOT INSTALLED", detail, "", "", "", "", false, false);
}

public sealed class GridSpec
{
    public decimal CenterX { get; set; }
    public decimal CenterY { get; set; }
    public decimal CenterZ { get; set; }
    public decimal SizeX { get; set; } = 20;
    public decimal SizeY { get; set; } = 20;
    public decimal SizeZ { get; set; } = 20;
}


public readonly record struct AtomPoint(double X, double Y, double Z, string Element, bool IsBackbone);
public readonly record struct BackbonePoint(double X, double Y, double Z, string Chain, int ResidueNumber, string ResidueName);

public sealed class Grid3DViewerForm : Form
{
    readonly GridSpec grid;
    readonly ProteinGrid3DView view;
    readonly Color bg, panelBg, panel2, border, purple, textColor, muted;
    readonly Label values;
    NumericUpDown? centerXInput, centerYInput, centerZInput;
    NumericUpDown? sizeXInput, sizeYInput, sizeZInput;
    bool updatingGridInputs;

    public Grid3DViewerForm(string receptorPath, GridSpec grid, Color bg, Color panelBg, Color panel2, Color border, Color purple, Color textColor, Color muted)
    {
        this.grid = grid; this.bg = bg; this.panelBg = panelBg; this.panel2 = panel2;
        this.border = border; this.purple = purple; this.textColor = textColor; this.muted = muted;

        Text = $"ELB DockTool {Program.AppVersion} — 3D Grid Viewer";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1220, 800);
        MinimumSize = new Size(980, 680);
        BackColor = bg;
        ForeColor = textColor;
        Font = new Font("Segoe UI", 9.5F);

        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = panelBg, Padding = new Padding(18, 12, 18, 10) };
        var title = new Label { Text = "3D Grid Visualization", Font = new Font("Segoe UI", 16F, FontStyle.Bold), ForeColor = textColor, AutoSize = true, Location = new Point(18, 10) };
        var file = new Label { Text = Path.GetFileName(receptorPath), Font = new Font("Segoe UI", 9.5F), ForeColor = muted, AutoEllipsis = true, Location = new Point(19, 43), Size = new Size(430, 25) };
        values = new Label { Text = GridText(), Font = new Font("Consolas", 9F), ForeColor = textColor, AutoEllipsis = true, Location = new Point(455, 45), Size = new Size(700, 25) };
        header.Controls.Add(title); header.Controls.Add(file); header.Controls.Add(values);

        view = new ProteinGrid3DView(receptorPath, grid, bg, panel2, border, purple, textColor, muted) { Dock = DockStyle.Fill };

        var controls = BuildGridControls(receptorPath);
        view.GridChanged += RefreshGrid;
        // Grid edits are committed continuously. Closing the viewer (X or CLOSE)
        // must never require APPLY / FIT GRID to preserve the final coordinates.
        FormClosing += (_, _) => RefreshGrid();
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = panelBg, Padding = new Padding(14, 8, 14, 8) };
        var hint = new Label { Text = "Drag = rotate   •   Drag anywhere on grid = move freely   •   Drag X/Y/Z handle = resize   •   Shift + drag = pan   •   Wheel = zoom   •   R = reset   •   F = fit", AutoSize = true, ForeColor = muted, Location = new Point(16, 15) };
        var close = new Button { Text = "CLOSE", Size = new Size(95, 30), Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(990, 9), FlatStyle = FlatStyle.Flat, BackColor = panel2, ForeColor = textColor };
        close.FlatAppearance.BorderColor = border; close.Click += (_, _) => Close();
        footer.Controls.Add(hint); footer.Controls.Add(close);
        footer.Resize += (_, _) => close.Left = footer.ClientSize.Width - close.Width - 14;

        Controls.Add(view); Controls.Add(controls); Controls.Add(footer); Controls.Add(header);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.R) view.ResetView(); else if (e.KeyCode == Keys.F) view.FitView(); };
        Resize += (_, _) => { if (ClientSize.Width > 1100) values.Width = ClientSize.Width - values.Left - 30; };
    }

    string GridText() =>
        $"Center  X {grid.CenterX:0.00}   Y {grid.CenterY:0.00}   Z {grid.CenterZ:0.00}     |     Size  X {grid.SizeX:0.00}   Y {grid.SizeY:0.00}   Z {grid.SizeZ:0.00} Å";

    Panel BuildGridControls(string receptorPath)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Right, Width = 310, BackColor = panelBg,
            Padding = new Padding(16, 14, 16, 10)
        };
        panel.Paint += (_, e) => { using var p = new Pen(border); e.Graphics.DrawLine(p, 0, 0, 0, panel.Height); };

        var heading = new Label
        {
            Text = "GRID CONTROL", ForeColor = textColor,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Location = new Point(16, 12), Size = new Size(270, 28)
        };
        panel.Controls.Add(heading);

        var sub = new Label
        {
            Text = "Set the box manually or calculate its center from a native ligand / residue.",
            ForeColor = muted, Location = new Point(16, 42), Size = new Size(274, 42)
        };
        panel.Controls.Add(sub);

        var centerBox = new GroupBox
        {
            Text = "Grid center (Å)", ForeColor = textColor,
            Location = new Point(12, 88), Size = new Size(282, 112)
        };
        centerXInput = AddGridNumber(centerBox, "X", grid.CenterX, 10, 24, v => { grid.CenterX = v; RefreshGrid(); });
        centerYInput = AddGridNumber(centerBox, "Y", grid.CenterY, 96, 24, v => { grid.CenterY = v; RefreshGrid(); });
        centerZInput = AddGridNumber(centerBox, "Z", grid.CenterZ, 182, 24, v => { grid.CenterZ = v; RefreshGrid(); });
        panel.Controls.Add(centerBox);

        var sizeBox = new GroupBox
        {
            Text = "Grid box size (Å)", ForeColor = textColor,
            Location = new Point(12, 208), Size = new Size(282, 112)
        };
        sizeXInput = AddGridNumber(sizeBox, "X", grid.SizeX, 10, 24, v => { if (v > 0) { grid.SizeX = v; RefreshGrid(); } });
        sizeYInput = AddGridNumber(sizeBox, "Y", grid.SizeY, 96, 24, v => { if (v > 0) { grid.SizeY = v; RefreshGrid(); } });
        sizeZInput = AddGridNumber(sizeBox, "Z", grid.SizeZ, 182, 24, v => { if (v > 0) { grid.SizeZ = v; RefreshGrid(); } });
        panel.Controls.Add(sizeBox);

        var manual = MakeViewerButton("FIT VIEW", 260);
        manual.Location = new Point(24, 330);
        manual.Click += (_, _) => { RefreshGrid(); view.FitView(); };
        panel.Controls.Add(manual);

        var styleLabel = new Label { Text = "Protein representation", ForeColor = textColor, Location = new Point(24, 374), Size = new Size(260, 22) };
        panel.Controls.Add(styleLabel);
        var style = new ComboBox { Location = new Point(24, 398), Size = new Size(260, 31), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(9,15,25), ForeColor = textColor, FlatStyle = FlatStyle.Flat };
        style.Items.AddRange(new object[] { "Ribbon", "Backbone Trace", "Atoms" });
        style.SelectedIndex = 0;
        style.SelectedIndexChanged += (_, _) =>
        {
            var selectedStyle = style.SelectedIndex switch
            {
                1 => "Backbone",
                2 => "Atoms",
                _ => "Ribbon"
            };
            view.SetProteinStyle(selectedStyle);
        };
        panel.Controls.Add(style);

        var auto = MakeViewerButton("AUTO-GRID FROM NATIVE LIGAND", 260);
        auto.Location = new Point(24, 436);
        auto.Click += (_, _) => AutoGridFromLigand();
        panel.Controls.Add(auto);

        var residueLabel = new Label { Text = "Select amino acid / residue", ForeColor = textColor, Location = new Point(24, 480), Size = new Size(260, 24) };
        panel.Controls.Add(residueLabel);

        var residues = new ComboBox
        {
            Location = new Point(24, 506), Size = new Size(260, 32),
            DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(9, 15, 25),
            ForeColor = textColor, FlatStyle = FlatStyle.Flat
        };
        // Keep the viewer completely unchanged while the dropdown is being used.
        // Choosing a residue only records the pending selection; the grid and
        // highlight are changed only when the explicit button below is clicked.
        residues.Items.Add("None");
        foreach (var r in ProteinGrid3DView.ReadResidueCenters(receptorPath))
            residues.Items.Add(r);
        residues.SelectedIndex = 0;
        panel.Controls.Add(residues);
        residues.SelectedIndexChanged += (_, _) =>
        {
            // Intentionally no viewer/grid update here. Selection is a pending
            // choice until the user presses CENTER + HIGHLIGHT SELECTED.
        };

        var centerResidue = MakeViewerButton("CENTER + HIGHLIGHT SELECTED", 260);
        centerResidue.Location = new Point(24, 544);
        centerResidue.Click += (_, _) =>
        {
            if (residues.SelectedItem is ProteinGrid3DView.ResidueCenter r)
            {
                view.SetSelectedResidue(r);
                grid.CenterX = (decimal)r.X;
                grid.CenterY = (decimal)r.Y;
                grid.CenterZ = (decimal)r.Z;
                RefreshGrid();
                view.FitView();
            }
            else
            {
                // None is also an explicit action: clear the highlight without
                // changing the current grid coordinates.
                view.SetSelectedResidue(null);
                RefreshGrid();
            }
        };
        panel.Controls.Add(centerResidue);

        var note = new Label
        {
            Text = "Choose a residue first. Nothing moves or highlights until you click CENTER + HIGHLIGHT SELECTED. Mouse: drag the grid to move it; drag X/Y/Z handles to resize.",
            ForeColor = muted, Location = new Point(24, 588), Size = new Size(260, 92)
        };
        panel.Controls.Add(note);
        return panel;
    }

    Button MakeViewerButton(string text, int width)
    {
        var b = new Button
        {
            Text = text, Size = new Size(width, 36), FlatStyle = FlatStyle.Flat,
            BackColor = panel2, ForeColor = textColor, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
        };
        b.FlatAppearance.BorderColor = border;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(34, 45, 66);
        return b;
    }

    NumericUpDown AddGridNumber(Control parent, string label, decimal value, int x, int y, Action<decimal> changed)
    {
        var l = new Label { Text = label, ForeColor = muted, Location = new Point(x, y), Size = new Size(72, 20) };
        var n = new NumericUpDown
        {
            Value = Math.Max(-10000, Math.Min(10000, value)),
            Minimum = -10000, Maximum = 10000, DecimalPlaces = 2, Increment = 0.5M,
            Size = new Size(78, 27), Location = new Point(x, y + 22),
            BackColor = Color.FromArgb(9, 15, 25), ForeColor = textColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        n.ValueChanged += (_, _) => { if (!updatingGridInputs) changed(n.Value); };
        parent.Controls.Add(l); parent.Controls.Add(n);
        return n;
    }

    void RefreshGrid()
    {
        values.Text = GridText();
        if (!updatingGridInputs)
        {
            updatingGridInputs = true;
            try
            {
                if (centerXInput != null) centerXInput.Value = Math.Max(centerXInput.Minimum, Math.Min(centerXInput.Maximum, grid.CenterX));
                if (centerYInput != null) centerYInput.Value = Math.Max(centerYInput.Minimum, Math.Min(centerYInput.Maximum, grid.CenterY));
                if (centerZInput != null) centerZInput.Value = Math.Max(centerZInput.Minimum, Math.Min(centerZInput.Maximum, grid.CenterZ));
                if (sizeXInput != null) sizeXInput.Value = Math.Max(sizeXInput.Minimum, Math.Min(sizeXInput.Maximum, grid.SizeX));
                if (sizeYInput != null) sizeYInput.Value = Math.Max(sizeYInput.Minimum, Math.Min(sizeYInput.Maximum, grid.SizeY));
                if (sizeZInput != null) sizeZInput.Value = Math.Max(sizeZInput.Minimum, Math.Min(sizeZInput.Maximum, grid.SizeZ));
            }
            finally { updatingGridInputs = false; }
        }
        view.Invalidate();
    }

    void AutoGridFromLigand()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select native ligand for automatic grid",
            Filter = "Molecule files|*.pdb;*.pdbqt;*.mol2;*.sdf;*.mol;*.xyz|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var coords = ProteinGrid3DView.ReadCoordinates(dlg.FileName);
            if (coords.Count == 0) throw new Exception("No atom coordinates were found in the selected ligand.");

            double minX = coords.Min(a => a.X), maxX = coords.Max(a => a.X);
            double minY = coords.Min(a => a.Y), maxY = coords.Max(a => a.Y);
            double minZ = coords.Min(a => a.Z), maxZ = coords.Max(a => a.Z);
            const double padding = 4.0;

            grid.CenterX = (decimal)((minX + maxX) / 2.0);
            grid.CenterY = (decimal)((minY + maxY) / 2.0);
            grid.CenterZ = (decimal)((minZ + maxZ) / 2.0);
            grid.SizeX = (decimal)Math.Max(8, (maxX - minX) + padding);
            grid.SizeY = (decimal)Math.Max(8, (maxY - minY) + padding);
            grid.SizeZ = (decimal)Math.Max(8, (maxZ - minZ) + padding);

            RefreshGrid();
            view.FitView();
            MessageBox.Show(
                $"Automatic grid created from:\n{Path.GetFileName(dlg.FileName)}\n\n" +
                $"Center: {grid.CenterX:0.00}, {grid.CenterY:0.00}, {grid.CenterZ:0.00} Å\n" +
                $"Size:   {grid.SizeX:0.00}, {grid.SizeY:0.00}, {grid.SizeZ:0.00} Å\n\n" +
                "A 4 Å padding was added around the ligand bounds.",
                "ELB DockTool — Auto Grid", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not calculate the ligand grid:\n\n" + ex.Message,
                "ELB DockTool — Auto Grid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
public sealed class ProteinGrid3DView : Control
{
    readonly Color bg, panel, border, purple, textColor, muted;
    readonly GridSpec grid;
    readonly List<AtomPoint> atoms = new();
    readonly List<AtomPoint> backbone = new();
    readonly List<BackbonePoint> backboneTrace = new();
    string proteinStyle = "Ribbon";
    ResidueCenter? selectedResidue;
    bool movingGrid;
    Point moveStartMouse;
    decimal moveStartX, moveStartY, moveStartZ;
    double yaw = -0.65, pitch = 0.35, zoom = 1.0, panX, panY;
    Point lastMouse;
    bool rotating, panning, resizing;
    char resizeAxis = '\0';
    double resizeStartSize;
    const double MinGridSize = 1.0;
    const double MaxGridSize = 200.0;
    double modelCenterX, modelCenterY, modelCenterZ, modelRadius = 1;

    public event Action? GridChanged;

    public ProteinGrid3DView(string receptorPath, GridSpec grid, Color bg, Color panel, Color border, Color purple, Color textColor, Color muted)
    {
        this.bg = bg; this.panel = panel; this.border = border; this.purple = purple; this.textColor = textColor; this.muted = muted; this.grid = grid;
        DoubleBuffered = true; BackColor = bg; TabStop = true;
        LoadAtoms(receptorPath);
        FitView();
        MouseDown += OnMouseDown; MouseMove += OnMouseMove; MouseUp += OnMouseUp;
        MouseWheel += OnWheel;
        Resize += (_, _) => Invalidate();
    }

    public readonly record struct ResidueCenter(string Name, string Chain, int Number, double X, double Y, double Z)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Chain) ? $"{Name} {Number}" : $"{Chain}: {Name} {Number}";
    }

    public static List<AtomPoint> ReadCoordinates(string path)
    {
        var result = new List<AtomPoint>();
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".mol2")
        {
            bool inAtoms = false;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("@<TRIPOS>ATOM", StringComparison.OrdinalIgnoreCase)) { inAtoms = true; continue; }
                if (line.StartsWith("@<TRIPOS>", StringComparison.OrdinalIgnoreCase)) { inAtoms = false; continue; }
                if (!inAtoms || string.IsNullOrWhiteSpace(line)) continue;
                var p = Regex.Split(line.Trim(), @"\s+");
                if (p.Length >= 5 &&
                    double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    result.Add(new AtomPoint(x, y, z, "C", false));
            }
            return result;
        }

        if (ext == ".sdf" || ext == ".mol")
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length >= 4)
            {
                int atomCount = 0;
                var counts = lines[3];
                if (counts.Length >= 3) int.TryParse(counts.Substring(0, 3).Trim(), out atomCount);
                for (int i = 4; i < lines.Length && i < 4 + atomCount; i++)
                {
                    var p = Regex.Split(lines[i].Trim(), @"\s+");
                    if (p.Length >= 4 &&
                        double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                        double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                        double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                        result.Add(new AtomPoint(x, y, z, p[3], false));
                }
            }
            return result;
        }

        if (ext == ".xyz")
        {
            var lines = File.ReadAllLines(path);
            int start = 2;
            for (int i = start; i < lines.Length; i++)
            {
                var p = Regex.Split(lines[i].Trim(), @"\s+");
                if (p.Length >= 4 &&
                    double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    result.Add(new AtomPoint(x, y, z, p[0], false));
            }
            return result;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (!(line.StartsWith("ATOM") || line.StartsWith("HETATM"))) continue;
            if (line.Length < 54) continue;
            if (!double.TryParse(Slice(line, 30, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!double.TryParse(Slice(line, 38, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            if (!double.TryParse(Slice(line, 46, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
            string atomName = Slice(line, 12, 4).Trim();
            string element = line.Length >= 78 ? Slice(line, 76, 2).Trim() : "";
            if (string.IsNullOrWhiteSpace(element)) element = Regex.Match(atomName, @"[A-Za-z]+").Value;
            element = element.Length > 0 ? element.Substring(0, 1).ToUpperInvariant() : "C";
            result.Add(new AtomPoint(x, y, z, element, string.Equals(atomName, "CA", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    public static List<ResidueCenter> ReadResidueCenters(string path)
    {
        var groups = new Dictionary<string, List<AtomPoint>>(StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, (string Name, string Chain, int Number)>(StringComparer.OrdinalIgnoreCase);
        var aminoAcids = new HashSet<string>(new[]
        {
            "ALA","ARG","ASN","ASP","CYS","GLN","GLU","GLY","HIS","ILE",
            "LEU","LYS","MET","PHE","PRO","SER","THR","TRP","TYR","VAL","MSE"
        }, StringComparer.OrdinalIgnoreCase);

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("ATOM")) continue;
            if (line.Length < 54) continue;
            string resName = Slice(line, 17, 3).Trim().ToUpperInvariant();
            if (!aminoAcids.Contains(resName)) continue;
            int.TryParse(Slice(line, 22, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resNo);
            string chain = Slice(line, 21, 1).Trim();
            string key = resName + "|" + chain + "|" + resNo;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<AtomPoint>();
            if (double.TryParse(Slice(line, 30, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(Slice(line, 38, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                double.TryParse(Slice(line, 46, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                list.Add(new AtomPoint(x, y, z, "C", false));
                labels[key] = (resName, chain, resNo);
            }
        }

        return groups
            .Where(kv => kv.Value.Count > 0)
            .Select(kv =>
            {
                var c = kv.Value;
                return new ResidueCenter(
                    labels[kv.Key].Name,
                    labels[kv.Key].Chain,
                    labels[kv.Key].Number,
                    c.Average(a => a.X), c.Average(a => a.Y), c.Average(a => a.Z));
            })
            .OrderBy(r => r.Number).ThenBy(r => r.Name)
            .ToList();
    }

    void LoadAtoms(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (!(line.StartsWith("ATOM") || line.StartsWith("HETATM"))) continue;
            if (line.Length < 54) continue;
            if (!double.TryParse(Slice(line, 30, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
            if (!double.TryParse(Slice(line, 38, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) continue;
            if (!double.TryParse(Slice(line, 46, 8), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
            string atomName = Slice(line, 12, 4).Trim();
            string element = line.Length >= 78 ? Slice(line, 76, 2).Trim() : "";
            if (string.IsNullOrWhiteSpace(element)) element = Regex.Match(atomName, @"[A-Za-z]+").Value;
            element = element.Length > 0 ? element.Substring(0, 1).ToUpperInvariant() : "C";
            bool isBackbone = string.Equals(atomName, "CA", StringComparison.OrdinalIgnoreCase);
            var a = new AtomPoint(x, y, z, element, isBackbone);
            atoms.Add(a);

            if (isBackbone)
            {
                string resName = Slice(line, 17, 3).Trim().ToUpperInvariant();
                string chain = Slice(line, 21, 1).Trim();
                int.TryParse(Slice(line, 22, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var resNo);
                backbone.Add(a);
                backboneTrace.Add(new BackbonePoint(x, y, z, chain, resNo, resName));
            }
        }

        // Avoid a sluggish first render on unusually large structures while retaining the shape.
        if (atoms.Count > 16000)
        {
            var reduced = new List<AtomPoint>(8000);
            int step = (int)Math.Ceiling(atoms.Count / 8000.0);
            for (int i = 0; i < atoms.Count; i += step) reduced.Add(atoms[i]);
            atoms.Clear(); atoms.AddRange(reduced);
        }
        if (atoms.Count == 0) throw new Exception("No ATOM/HETATM coordinates were found in the receptor file.");
    }

    static string Slice(string s, int start, int length) => start >= s.Length ? "" : s.Substring(start, Math.Min(length, s.Length - start)).Trim();

    public void SetProteinStyle(string style) { proteinStyle = style; Invalidate(); }

    public void SetSelectedResidue(ResidueCenter? residue)
    {
        selectedResidue = residue;
        Invalidate();
    }

    public void ResetView() { yaw = -0.65; pitch = 0.35; zoom = 1; panX = panY = 0; Invalidate(); }
    public void FitView()
    {
        double minX = atoms.Min(a => a.X), maxX = atoms.Max(a => a.X), minY = atoms.Min(a => a.Y), maxY = atoms.Max(a => a.Y), minZ = atoms.Min(a => a.Z), maxZ = atoms.Max(a => a.Z);
        minX = Math.Min(minX, (double)grid.CenterX - (double)grid.SizeX / 2); maxX = Math.Max(maxX, (double)grid.CenterX + (double)grid.SizeX / 2);
        minY = Math.Min(minY, (double)grid.CenterY - (double)grid.SizeY / 2); maxY = Math.Max(maxY, (double)grid.CenterY + (double)grid.SizeY / 2);
        minZ = Math.Min(minZ, (double)grid.CenterZ - (double)grid.SizeZ / 2); maxZ = Math.Max(maxZ, (double)grid.CenterZ + (double)grid.SizeZ / 2);
        modelCenterX = (minX + maxX) / 2; modelCenterY = (minY + maxY) / 2; modelCenterZ = (minZ + maxZ) / 2;
        modelRadius = Math.Max(1, Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2) + Math.Pow(maxZ - minZ, 2)) / 2);
        zoom = 1; panX = panY = 0; Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.Clear(bg);
        if (proteinStyle == "RibbonSurface") DrawSurfaceEffect(e.Graphics);
        if (proteinStyle == "Atoms") DrawAtoms(e.Graphics, false);
        else DrawBackbone(e.Graphics, proteinStyle != "Backbone");
        DrawSelectedResidue(e.Graphics);
        DrawGrid(e.Graphics);
        DrawOverlay(e.Graphics);
    }

    PointF Project(double x, double y, double z, out double depth)
    {
        x -= modelCenterX; y -= modelCenterY; z -= modelCenterZ;
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        double x1 = x * cy - z * sy; double z1 = x * sy + z * cy;
        double y1 = y * cp - z1 * sp; depth = y * sp + z1 * cp;
        float scale = (float)(Math.Min(ClientSize.Width, ClientSize.Height) * 0.42 / modelRadius * zoom);
        return new PointF(ClientSize.Width / 2f + (float)(x1 * scale + panX), ClientSize.Height / 2f - (float)(y1 * scale + panY));
    }

    void DrawSelectedResidue(Graphics g)
    {
        if (selectedResidue is not ResidueCenter r) return;

        var p = Project(r.X, r.Y, r.Z, out var depth);
        float pulse = 1.0f;
        float outer = 13f;
        float inner = 6f;

        using var glow = new SolidBrush(Color.FromArgb(48, 255, 205, 65));
        g.FillEllipse(glow, p.X - outer, p.Y - outer, outer * 2, outer * 2);
        using var ring = new Pen(Color.FromArgb(245, 255, 205, 65), 2.4f);
        g.DrawEllipse(ring, p.X - outer, p.Y - outer, outer * 2, outer * 2);
        using var marker = new SolidBrush(Color.FromArgb(250, 255, 214, 70));
        g.FillEllipse(marker, p.X - inner, p.Y - inner, inner * 2, inner * 2);

        using var cross = new Pen(Color.FromArgb(220, 255, 238, 150), 1.5f);
        g.DrawLine(cross, p.X - 19, p.Y, p.X - 8, p.Y);
        g.DrawLine(cross, p.X + 8, p.Y, p.X + 19, p.Y);
        g.DrawLine(cross, p.X, p.Y - 19, p.X, p.Y - 8);
        g.DrawLine(cross, p.X, p.Y + 8, p.X, p.Y + 19);

        string chain = string.IsNullOrWhiteSpace(r.Chain) ? "" : $"Chain {r.Chain}  ";
        string label = $"{chain}{r.Name} {r.Number}";
        using var labelBg = new SolidBrush(Color.FromArgb(220, 12, 18, 28));
        using var labelBorder = new Pen(Color.FromArgb(220, 255, 205, 65), 1f);
        using var labelFont = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        var sz = g.MeasureString(label, labelFont);
        var rect = new RectangleF(p.X + 18, p.Y - sz.Height - 8, sz.Width + 12, sz.Height + 8);
        if (rect.Right > ClientSize.Width - 6) rect.X = p.X - rect.Width - 18;
        if (rect.Top < 6) rect.Y = p.Y + 18;
        using var path = new GraphicsPath();
        path.AddRectangle(rect);
        g.FillPath(labelBg, path);
        g.DrawPath(labelBorder, path);
        using var labelBrush = new SolidBrush(Color.FromArgb(255, 255, 224, 120));
        g.DrawString(label, labelFont, labelBrush, rect.X + 6, rect.Y + 4);
    }

    void DrawGrid(Graphics g)
    {
        double cx = (double)grid.CenterX, cy = (double)grid.CenterY, cz = (double)grid.CenterZ;
        double sx = (double)grid.SizeX / 2, sy = (double)grid.SizeY / 2, sz = (double)grid.SizeZ / 2;
        var corners = new PointF[8]; double[] xs = { cx - sx, cx + sx }, ys = { cy - sy, cy + sy }, zs = { cz - sz, cz + sz };
        for (int i = 0; i < 8; i++) corners[i] = Project(xs[(i >> 0) & 1], ys[(i >> 1) & 1], zs[(i >> 2) & 1], out _);
        using var pen = new Pen(Color.FromArgb(180, 170, 95, 255), 2f);
        using var faint = new Pen(Color.FromArgb(70, 170, 95, 255), 1f);
        int[,] edges = { {0,1},{0,2},{0,4},{1,3},{1,5},{2,3},{2,6},{3,7},{4,5},{4,6},{5,7},{6,7} };
        for (int i = 0; i < edges.GetLength(0); i++) g.DrawLine(pen, corners[edges[i,0]], corners[edges[i,1]]);
        // Three center-to-face guides make the box orientation easier to understand.
        var center = Project(cx, cy, cz, out _); using var centerPen = new Pen(Color.FromArgb(150, 145, 74, 255), 1.2f) { DashStyle = DashStyle.Dash };
        g.DrawLine(centerPen, center, corners[0]); g.DrawLine(centerPen, center, corners[7]);
        using var brush = new SolidBrush(Color.FromArgb(28, 145, 74, 255));
        var pts = new[] { corners[0], corners[1], corners[3], corners[2], corners[6], corners[4], corners[5], corners[7] };
        using var path = new GraphicsPath(); path.AddPolygon(pts.Take(4).ToArray());
        g.FillPath(brush, path);

        // Mouse-resize handles: drag the X/Y/Z handle to change that dimension.
        var hx = Project(cx + sx, cy, cz, out _);
        var hy = Project(cx, cy + sy, cz, out _);
        var hz = Project(cx, cy, cz + sz, out _);
        using var handleBrush = new SolidBrush(Color.FromArgb(235, 185, 120, 255));
        using var handlePen = new Pen(Color.FromArgb(240, 245, 235, 255), 1.2f);
        var handleData = new[] { ("X", hx), ("Y", hy), ("Z", hz) };
        foreach (var item in handleData)
        {
            var hp = item.Item2;
            g.FillEllipse(handleBrush, hp.X - 5, hp.Y - 5, 10, 10);
            g.DrawEllipse(handlePen, hp.X - 5, hp.Y - 5, 10, 10);
            using var labelBrush = new SolidBrush(textColor);
            g.DrawString(item.Item1, new Font("Segoe UI", 8F, FontStyle.Bold), labelBrush, hp.X + 7, hp.Y - 9);
        }
    }

    void DrawBackbone(Graphics g, bool ribbonMode)
    {
        if (backboneTrace.Count < 2) return;
        var ordered = backboneTrace
            .OrderBy(x => x.Chain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ResidueNumber)
            .ToList();
        int total = Math.Max(1, ordered.Count - 1);
        int globalIndex = 0;
        var segment = new List<BackbonePoint>();
        string? chain = null;
        int previousResidue = int.MinValue;
        foreach (var a in ordered)
        {
            bool continuous = segment.Count > 0 &&
                string.Equals(chain, a.Chain, StringComparison.OrdinalIgnoreCase) &&
                a.ResidueNumber >= previousResidue && a.ResidueNumber - previousResidue <= 1;
            if (!continuous && segment.Count >= 2)
            {
                DrawRibbonSegment(g, segment, globalIndex, total, ribbonMode);
                globalIndex += segment.Count;
                segment.Clear();
            }
            segment.Add(a);
            chain = a.Chain;
            previousResidue = a.ResidueNumber;
        }
        if (segment.Count >= 2) DrawRibbonSegment(g, segment, globalIndex, total, ribbonMode);
    }

    void DrawRibbonSegment(Graphics g, List<BackbonePoint> points, int globalIndex, int total, bool ribbonMode)
    {
        var projected = points.Select(a => Project(a.X, a.Y, a.Z, out _)).ToArray();
        if (projected.Length < 2) return;
        float width = ribbonMode ? 15f : 3f;
        float shadowWidth = ribbonMode ? 19f : 6f;
        using var shadow = new Pen(Color.FromArgb(80, 5, 9, 16), shadowWidth)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        for (int i = 0; i < projected.Length - 1; i++)
        {
            var p0 = projected[Math.Max(0, i - 1)];
            var p1 = projected[i];
            var p2 = projected[i + 1];
            var p3 = projected[Math.Min(projected.Length - 1, i + 2)];
            float c1x = p1.X + (p2.X - p0.X) / 6f;
            float c1y = p1.Y + (p2.Y - p0.Y) / 6f;
            float c2x = p2.X - (p3.X - p1.X) / 6f;
            float c2y = p2.Y - (p3.Y - p1.Y) / 6f;
            var c = ribbonMode ? SpectrumColor(globalIndex + i, total) : Color.FromArgb(210, 195, 207, 225);
            using var pen = new Pen(c, width)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawBezier(shadow, p1, new PointF(c1x, c1y), new PointF(c2x, c2y), p2);
            g.DrawBezier(pen, p1, new PointF(c1x, c1y), new PointF(c2x, c2y), p2);
            if (ribbonMode)
            {
                using var highlight = new Pen(Color.FromArgb(75, 255, 255, 255), 2.0f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawBezier(highlight, p1, new PointF(c1x, c1y), new PointF(c2x, c2y), p2);
            }
        }
    }

    static Color SpectrumColor(int index, int total)
    {
        if (total <= 1) return Color.FromArgb(170, 80, 245);
        double t = Math.Max(0.0, Math.Min(1.0, (double)index / (total - 1)));
        // Blue -> cyan -> green -> yellow -> red, similar to common molecular
        // spectrum coloring while remaining independent of 3Dmol.js.
        double h = (2.0 / 3.0) * (1.0 - t);
        double r = Math.Abs(h * 6 - 3) - 1;
        double g = 2 - Math.Abs(h * 6 - 2);
        double b = 2 - Math.Abs(h * 6 - 4);
        r = Math.Max(0, Math.Min(1, r));
        g = Math.Max(0, Math.Min(1, g));
        b = Math.Max(0, Math.Min(1, b));
        return Color.FromArgb(235, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    void DrawSurfaceEffect(Graphics g)
    {
        var projected = new List<(PointF P, double D, AtomPoint A)>();
        foreach (var a in atoms)
        {
            if (a.Element.Equals("H", StringComparison.OrdinalIgnoreCase)) continue;
            projected.Add((Project(a.X, a.Y, a.Z, out var d), d, a));
        }
        projected.Sort((a, b) => a.D.CompareTo(b.D));
        int stride = Math.Max(1, projected.Count / 6500);
        for (int i = 0; i < projected.Count; i += stride)
        {
            var q = projected[i];
            float r = q.A.Element.Equals("S", StringComparison.OrdinalIgnoreCase) ? 5.2f :
                      q.A.Element.Equals("P", StringComparison.OrdinalIgnoreCase) ? 4.9f : 4.3f;
            double depth = Math.Max(0, Math.Min(1, (q.D + modelRadius) / (2 * modelRadius)));
            int alpha = 28 + (int)(45 * (1 - depth));
            using var brush = new SolidBrush(Color.FromArgb(alpha, 185, 192, 202));
            g.FillEllipse(brush, q.P.X - r, q.P.Y - r, r * 2, r * 2);
        }
    }

    void DrawAtoms(Graphics g, bool includeBackbone)
    {
        var projected = new List<(PointF P, double D, AtomPoint A)>();
        foreach (var a in atoms) projected.Add((Project(a.X, a.Y, a.Z, out var d), d, a));
        projected.Sort((a,b) => a.D.CompareTo(b.D));
        foreach (var q in projected)
        {
            if (!includeBackbone && q.A.IsBackbone) continue;
            float r = q.A.IsBackbone ? 2.5f : 2.0f;
            if (q.A.Element.Equals("O", StringComparison.OrdinalIgnoreCase)) r = 2.2f;
            using var b = new SolidBrush(AtomColor(q.A.Element)); g.FillEllipse(b, q.P.X-r, q.P.Y-r, r*2, r*2);
        }
    }


    Color AtomColor(string element) => element.ToUpperInvariant() switch
    {
        "O" => Color.FromArgb(225, 95, 105),
        "N" => Color.FromArgb(95, 150, 245),
        "S" => Color.FromArgb(230, 195, 75),
        "P" => Color.FromArgb(235, 150, 75),
        "F" or "CL" => Color.FromArgb(100, 210, 130),
        "BR" or "I" => Color.FromArgb(175, 115, 205),
        "H" => Color.FromArgb(205, 210, 220),
        _ => Color.FromArgb(175, 185, 198)
    };

    void DrawOverlay(Graphics g)
    {
        using var box = new SolidBrush(Color.FromArgb(205, 10, 16, 27));
        g.FillRectangle(box, 14, 14, 310, 86);
        using var p = new Pen(border); g.DrawRectangle(p, 14, 14, 310, 86);
        using var b = new SolidBrush(textColor); using var m = new SolidBrush(muted); using var purpleBrush = new SolidBrush(purple);
        g.FillEllipse(purpleBrush, 28, 30, 8, 8); g.DrawString("Docking grid", Font, b, 44, 25);
        g.DrawString($"Center: {grid.CenterX:0.00}, {grid.CenterY:0.00}, {grid.CenterZ:0.00} Å", Font, m, 28, 47);
        g.DrawString($"Size:   {grid.SizeX:0.00}, {grid.SizeY:0.00}, {grid.SizeZ:0.00} Å", Font, m, 28, 68);
    }

    bool HitGridBody(Point location)
    {
        double cx = (double)grid.CenterX, cy = (double)grid.CenterY, cz = (double)grid.CenterZ;
        double sx = (double)grid.SizeX / 2, sy = (double)grid.SizeY / 2, sz = (double)grid.SizeZ / 2;
        var corners = new List<PointF>(8);
        double[] xs = { cx - sx, cx + sx }, ys = { cy - sy, cy + sy }, zs = { cz - sz, cz + sz };
        for (int i = 0; i < 8; i++) corners.Add(Project(xs[(i >> 0) & 1], ys[(i >> 1) & 1], zs[(i >> 2) & 1], out _));
        var hull = ConvexHull(corners);
        if (hull.Count >= 3)
        {
            using var path = new GraphicsPath();
            path.AddPolygon(hull.ToArray());
            if (path.IsVisible(location)) return true;
        }
        for (int i = 0; i < corners.Count; i++)
            for (int j = i + 1; j < corners.Count; j++)
            {
                if (((i ^ j) != 1) && ((i ^ j) != 2) && ((i ^ j) != 4)) continue;
                if (DistanceToSegment(location, corners[i], corners[j]) <= 11) return true;
            }
        return false;
    }

    static double DistanceToSegment(Point p, PointF a, PointF b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double wx = p.X - a.X, wy = p.Y - a.Y;
        double len2 = vx * vx + vy * vy;
        double t = len2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (wx * vx + wy * vy) / len2));
        double dx = p.X - (a.X + t * vx), dy = p.Y - (a.Y + t * vy);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static List<PointF> ConvexHull(List<PointF> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count <= 2) return pts;
        static float Cross(PointF o, PointF a, PointF b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<PointF>();
        foreach (var p in pts)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<PointF>();
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            var p = pts[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    void MoveGridFromMouse(Point location)
    {
        double dx = location.X - moveStartMouse.X;
        double dy = location.Y - moveStartMouse.Y;
        double scale = PixelsPerAngstromScreen();
        if (scale <= 0.001) return;

        double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        double rightX = cy, rightY = 0, rightZ = -sy;
        double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        double upX = -sy * sp, upY = cp, upZ = -cy * sp;

        double worldRight = Math.Max(-60.0, Math.Min(60.0, dx / scale));
        double worldUp = Math.Max(-60.0, Math.Min(60.0, -dy / scale));
        grid.CenterX = moveStartX + (decimal)(rightX * worldRight + upX * worldUp);
        grid.CenterY = moveStartY + (decimal)(rightY * worldRight + upY * worldUp);
        grid.CenterZ = moveStartZ + (decimal)(rightZ * worldRight + upZ * worldUp);
        GridChanged?.Invoke();
        Invalidate(); Parent?.Invalidate();
    }

    double PixelsPerAngstromScreen()
    {
        return Math.Max(2.0, Math.Min(ClientSize.Width, ClientSize.Height) * 0.42 / Math.Max(1, modelRadius) * zoom);
    }

    char HitResizeHandle(Point location, out PointF handlePoint)
    {
        double cx = (double)grid.CenterX, cy = (double)grid.CenterY, cz = (double)grid.CenterZ;
        double sx = (double)grid.SizeX / 2, sy = (double)grid.SizeY / 2, sz = (double)grid.SizeZ / 2;
        var center = Project(cx, cy, cz, out _);
        var handles = new[]
        {
            ('X', Project(cx + sx, cy, cz, out _)),
            ('Y', Project(cx, cy + sy, cz, out _)),
            ('Z', Project(cx, cy, cz + sz, out _))
        };
        char best = '\0'; double bestDist = 20 * 20; handlePoint = PointF.Empty;
        foreach (var h in handles)
        {
            double dx = location.X - h.Item2.X, dy = location.Y - h.Item2.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= bestDist) { bestDist = d2; best = h.Item1; handlePoint = h.Item2; }
        }
        return best;
    }

    double PixelsPerAngstrom(char axis)
    {
        var c = Project((double)grid.CenterX, (double)grid.CenterY, (double)grid.CenterZ, out _);
        PointF p = axis switch
        {
            'X' => Project((double)grid.CenterX + 1, (double)grid.CenterY, (double)grid.CenterZ, out _),
            'Y' => Project((double)grid.CenterX, (double)grid.CenterY + 1, (double)grid.CenterZ, out _),
            _ => Project((double)grid.CenterX, (double)grid.CenterY, (double)grid.CenterZ + 1, out _)
        };
        return Math.Max(0.001, Math.Sqrt(Math.Pow(p.X - c.X, 2) + Math.Pow(p.Y - c.Y, 2)));
    }

    void ResizeGridFromMouse(Point location)
    {
        var center = Project((double)grid.CenterX, (double)grid.CenterY, (double)grid.CenterZ, out _);
        double ax = 1, ay = 0;
        if (resizeAxis == 'X')
        {
            var p = Project((double)grid.CenterX + 1, (double)grid.CenterY, (double)grid.CenterZ, out _);
            ax = p.X - center.X; ay = p.Y - center.Y;
        }
        else if (resizeAxis == 'Y')
        {
            var p = Project((double)grid.CenterX, (double)grid.CenterY + 1, (double)grid.CenterZ, out _);
            ax = p.X - center.X; ay = p.Y - center.Y;
        }
        else
        {
            var p = Project((double)grid.CenterX, (double)grid.CenterY, (double)grid.CenterZ + 1, out _);
            ax = p.X - center.X; ay = p.Y - center.Y;
        }
        double len = Math.Sqrt(ax * ax + ay * ay);
        if (len < 0.001) return;
        ax /= len; ay /= len;
        double screenDelta = (location.X - lastMouse.X) * ax + (location.Y - lastMouse.Y) * ay;
        double deltaSize = screenDelta * 2.0 / Math.Max(2.0, PixelsPerAngstrom(resizeAxis));
        deltaSize = Math.Max(-4.0, Math.Min(4.0, deltaSize));
        double currentSize = resizeAxis == 'X' ? (double)grid.SizeX : resizeAxis == 'Y' ? (double)grid.SizeY : (double)grid.SizeZ;
        decimal newSize = (decimal)Math.Max(MinGridSize, Math.Min(MaxGridSize, currentSize + deltaSize));
        if (resizeAxis == 'X') grid.SizeX = newSize;
        else if (resizeAxis == 'Y') grid.SizeY = newSize;
        else grid.SizeZ = newSize;
        lastMouse = location;
        GridChanged?.Invoke();
        Invalidate();
        Parent?.Invalidate();
    }

    void OnMouseDown(object? s, MouseEventArgs e)
    {
        Focus(); lastMouse = e.Location;
        if (e.Button == MouseButtons.Left && !ModifierKeys.HasFlag(Keys.Shift))
        {
            char axis = HitResizeHandle(e.Location, out var handle);
            if (axis != '\0')
            {
                resizing = true; resizeAxis = axis;
                resizeStartSize = axis == 'X' ? (double)grid.SizeX : axis == 'Y' ? (double)grid.SizeY : (double)grid.SizeZ;
                Cursor = Cursors.SizeAll;
                return;
            }
        }
        if (e.Button == MouseButtons.Left && HitGridBody(e.Location))
        {
            movingGrid = true; moveStartMouse=e.Location; moveStartX=grid.CenterX; moveStartY=grid.CenterY; moveStartZ=grid.CenterZ; Cursor=Cursors.SizeAll; return;
        }
        if (e.Button == MouseButtons.Left && ModifierKeys.HasFlag(Keys.Shift)) { panning = true; Cursor = Cursors.SizeAll; }
        else if (e.Button == MouseButtons.Left) { rotating = true; Cursor = Cursors.SizeAll; }
    }
    void OnMouseMove(object? s, MouseEventArgs e)
    {
        if (resizing) { ResizeGridFromMouse(e.Location); return; }
        if (movingGrid) { MoveGridFromMouse(e.Location); return; }
        int dx = e.X - lastMouse.X, dy = e.Y - lastMouse.Y; lastMouse = e.Location;
        if (rotating) { yaw += dx * 0.008; pitch += dy * 0.008; pitch = Math.Max(-1.45, Math.Min(1.45, pitch)); Invalidate(); }
        else if (panning) { panX += dx; panY -= dy; Invalidate(); }
        else
        {
            char axis = HitResizeHandle(e.Location, out _);
            Cursor = axis == '\0' ? Cursors.Default : Cursors.SizeAll;
        }
    }
    void OnMouseUp(object? s, MouseEventArgs e)
    {
        resizing = movingGrid = rotating = panning = false; resizeAxis = '\0'; Cursor = Cursors.Default; Invalidate();
    }
    void OnWheel(object? s, MouseEventArgs e) { zoom *= Math.Pow(1.0018, e.Delta); zoom = Math.Max(0.15, Math.Min(8, zoom)); Invalidate(); }
}


public sealed class ReceptorAtomRecord
{
    public string RecordType { get; init; } = "";
    public int Serial { get; init; }
    public string AtomName { get; init; } = "";
    public string ResidueName { get; init; } = "";
    public string Chain { get; init; } = "";
    public string ResidueNumber { get; init; } = "";
    public string InsertionCode { get; init; } = "";
    public string AltLoc { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public int LineIndex { get; init; }
    public string Element { get; init; } = "";
    public bool IsWater { get; init; }
    public bool IsHetAtom => string.Equals(RecordType, "HETATM", StringComparison.OrdinalIgnoreCase);
    public string ResidueKey => $"{Chain}|{ResidueName}|{ResidueNumber}|{InsertionCode}";
    public string DisplayResidueKey => string.IsNullOrWhiteSpace(Chain)
        ? $"{ResidueName} {ResidueNumber}{InsertionCode}"
        : $"Chain {Chain}  {ResidueName} {ResidueNumber}{InsertionCode}";
    public string DisplayAtom => $"{Serial,5}  {AtomName,-4}  {ResidueName,3} {Chain,1}{ResidueNumber,4}{InsertionCode,1}  {X,8:0.000} {Y,8:0.000} {Z,8:0.000}";
    public string DisplayWater => $"{DisplayResidueKey}  ({(RecordType == "HETATM" ? "HETATM" : "ATOM")})";
}

public static class ReceptorStructureInspector
{
    static readonly HashSet<string> WaterResidues = new(StringComparer.OrdinalIgnoreCase)
        { "HOH", "WAT", "DOD", "H2O" };

    public static List<ReceptorAtomRecord> ReadPdb(string path)
    {
        var atoms = new List<ReceptorAtomRecord>();
        int lineIndex = 0;
        foreach (string raw in File.ReadLines(path))
        {
            string record = raw.Length >= 6 ? raw[..6].Trim().ToUpperInvariant() : "";
            if (record != "ATOM" && record != "HETATM") { lineIndex++; continue; }
            if (raw.Length < 54) { lineIndex++; continue; }
            if (!double.TryParse(raw.Substring(30, 8).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(raw.Substring(38, 8).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
                !double.TryParse(raw.Substring(46, 8).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
            { lineIndex++; continue; }

            int.TryParse(raw.Substring(6, 5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int serial);
            string atomName = raw.Substring(12, 4).Trim();
            string altLoc = raw.Length > 16 ? raw[16].ToString().Trim() : "";
            string residue = raw.Substring(17, 3).Trim().ToUpperInvariant();
            string chain = raw.Length > 21 ? raw[21].ToString().Trim() : "";
            string resSeq = raw.Substring(22, 4).Trim();
            string iCode = raw.Length > 26 ? raw[26].ToString().Trim() : "";
            string element = raw.Length >= 78 ? raw.Substring(76, 2).Trim() : "";
            if (string.IsNullOrWhiteSpace(element))
                element = InferElement(atomName);

            atoms.Add(new ReceptorAtomRecord
            {
                RecordType = record,
                Serial = serial,
                AtomName = atomName,
                ResidueName = residue,
                Chain = chain,
                ResidueNumber = resSeq,
                InsertionCode = iCode,
                AltLoc = altLoc,
                X = x, Y = y, Z = z,
                LineIndex = lineIndex,
                Element = element,
                IsWater = WaterResidues.Contains(residue)
            });
            lineIndex++;
        }
        return atoms;
    }

    static string InferElement(string atomName)
    {
        string s = new string(atomName.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        if (s.Length >= 2 && (s.StartsWith("CL") || s.StartsWith("BR"))) return s[..2];
        return s.Length > 0 ? s[..1] : "?";
    }

    public static bool DeleteSelectedFromPdb(string input, string output,
        IEnumerable<ReceptorAtomRecord> residues,
        IEnumerable<ReceptorAtomRecord> hetAtoms,
        IEnumerable<ReceptorAtomRecord> waters,
        IEnumerable<string>? chains = null)
    {
        var residueKeys = new HashSet<string>(residues.Select(a => a.ResidueKey), StringComparer.OrdinalIgnoreCase);
        var hetLineIndexes = new HashSet<int>(hetAtoms.Select(a => a.LineIndex));
        var waterKeys = new HashSet<string>(waters.Select(a => a.ResidueKey), StringComparer.OrdinalIgnoreCase);
        var chainSet = new HashSet<string>((chains ?? Enumerable.Empty<string>()).Select(c => c ?? ""), StringComparer.OrdinalIgnoreCase);
        if (residueKeys.Count == 0 && hetLineIndexes.Count == 0 && waterKeys.Count == 0 && chainSet.Count == 0) return false;

        var lines = File.ReadAllLines(input).ToList();
        var atoms = ReadPdb(input);
        var atomByLine = atoms.ToDictionary(a => a.LineIndex);
        var deletedSerials = new HashSet<int>(atoms.Where(a =>
            (a.IsWater && waterKeys.Contains(a.ResidueKey)) ||
            (a.RecordType == "ATOM" && !a.IsWater && residueKeys.Contains(a.ResidueKey)) ||
            (a.IsHetAtom && !a.IsWater && hetLineIndexes.Contains(a.LineIndex)) ||
            (a.RecordType == "ATOM" && !a.IsWater && chainSet.Contains(string.IsNullOrWhiteSpace(a.Chain) ? "" : a.Chain.Trim())))
            .Select(a => a.Serial));
        using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
        for (int i = 0; i < lines.Count; i++)
        {
            string record = lines[i].Length >= 6 ? lines[i][..6].Trim().ToUpperInvariant() : "";
            if (record == "CONECT" && deletedSerials.Count > 0)
            {
                bool referencesDeleted = false;
                if (lines[i].Length >= 11 && int.TryParse(lines[i].Substring(6, 5).Trim(), out int c0) && deletedSerials.Contains(c0)) referencesDeleted = true;
                for (int pos = 11; !referencesDeleted && pos + 5 <= lines[i].Length; pos += 5)
                    if (int.TryParse(lines[i].Substring(pos, 5).Trim(), out int ci) && deletedSerials.Contains(ci)) referencesDeleted = true;
                if (referencesDeleted) continue;
            }
            if (!atomByLine.TryGetValue(i, out var atom)) { writer.WriteLine(lines[i]); continue; }
            bool delete = (atom.IsWater && waterKeys.Contains(atom.ResidueKey)) ||
                          (atom.RecordType == "ATOM" && !atom.IsWater && residueKeys.Contains(atom.ResidueKey)) ||
                          (atom.IsHetAtom && !atom.IsWater && hetLineIndexes.Contains(atom.LineIndex)) ||
                          (atom.RecordType == "ATOM" && !atom.IsWater && chainSet.Contains(string.IsNullOrWhiteSpace(atom.Chain) ? "" : atom.Chain.Trim()));
            if (!delete) writer.WriteLine(lines[i]);
        }
        return true;
    }

    public static string Describe(ReceptorAtomRecord a) =>
        $"Atom {a.Serial}  {a.AtomName}   {a.ResidueName} {a.ResidueNumber}{a.InsertionCode}  " +
        $"Chain {(string.IsNullOrWhiteSpace(a.Chain) ? "—" : a.Chain)}\r\n" +
        $"Element: {a.Element}\r\n" +
        $"X = {a.X.ToString("0.000", CultureInfo.InvariantCulture)} Å    " +
        $"Y = {a.Y.ToString("0.000", CultureInfo.InvariantCulture)} Å    " +
        $"Z = {a.Z.ToString("0.000", CultureInfo.InvariantCulture)} Å";

    public static string DescribeGroup(ReceptorHetGroup group)
    {
        var atoms = group.Atoms;
        var first = atoms[0];
        double cx = atoms.Average(a => a.X), cy = atoms.Average(a => a.Y), cz = atoms.Average(a => a.Z);
        var sb = new StringBuilder();
        sb.AppendLine($"HETATM group: {first.ResidueName} {first.ResidueNumber}   Chain {(string.IsNullOrWhiteSpace(first.Chain) ? "—" : first.Chain)}");
        sb.AppendLine($"Atoms: {atoms.Count}");
        sb.AppendLine($"Centroid (mean of atom coordinates): X {cx.ToString("0.000", CultureInfo.InvariantCulture)}  Y {cy.ToString("0.000", CultureInfo.InvariantCulture)}  Z {cz.ToString("0.000", CultureInfo.InvariantCulture)} Å");
        sb.AppendLine("Atom coordinates from PDB columns 31–54 (Å):");
        foreach (var a in atoms.OrderBy(a => a.Serial))
            sb.AppendLine($"{a.Serial,5} {a.AtomName,-4} {a.Element,2}  X {a.X,8:0.000}  Y {a.Y,8:0.000}  Z {a.Z,8:0.000}");
        return sb.ToString();
    }

    public static string DescribeResidue(string type, IEnumerable<ReceptorAtomRecord> sourceAtoms)
    {
        var atoms = sourceAtoms.OrderBy(a => a.Serial).ToList();
        if (atoms.Count == 0) return "No structure records selected.";
        var first = atoms[0];
        double cx = atoms.Average(a => a.X), cy = atoms.Average(a => a.Y), cz = atoms.Average(a => a.Z);
        var sb = new StringBuilder();
        sb.AppendLine($"{type}: {first.ResidueName} {first.ResidueNumber}{first.InsertionCode}   Chain {(string.IsNullOrWhiteSpace(first.Chain) ? "—" : first.Chain)}");
        sb.AppendLine($"Atoms: {atoms.Count}");
        sb.AppendLine($"Centroid (mean of atom coordinates): X {cx.ToString("0.000", CultureInfo.InvariantCulture)}  Y {cy.ToString("0.000", CultureInfo.InvariantCulture)}  Z {cz.ToString("0.000", CultureInfo.InvariantCulture)} Å");
        sb.AppendLine("Exact atom coordinates from PDB fixed-width X/Y/Z columns (Å):");
        foreach (var a in atoms)
            sb.AppendLine($"{a.RecordType,-6} {a.Serial,5} {a.AtomName,-4} {a.Element,2}  X {a.X,8:0.000}  Y {a.Y,8:0.000}  Z {a.Z,8:0.000}");
        return sb.ToString();
    }
}

public sealed class ReceptorHetGroup
{
    public string ResidueKey { get; }
    public List<ReceptorAtomRecord> Atoms { get; }
    public string DisplayName
    {
        get
        {
            var first = Atoms[0];
            string chain = string.IsNullOrWhiteSpace(first.Chain) ? "—" : first.Chain;
            string alt = string.IsNullOrWhiteSpace(first.InsertionCode) ? "" : first.InsertionCode;
            return $"{first.ResidueName} {first.ResidueNumber}{alt}  Chain {chain}  ({Atoms.Count} atom{(Atoms.Count == 1 ? "" : "s")})";
        }
    }
    public ReceptorHetGroup(string residueKey, List<ReceptorAtomRecord> atoms) { ResidueKey = residueKey; Atoms = atoms; }
}

public sealed class MainForm : Form
{
    // Molecule Converter preparation controls are kept on the converter page so
    // docking remains focused on docking parameters only.
    CheckBox? converterCanonical;
    CheckBox? converterGen3d;
    CheckBox? converterAddH;
    CheckBox? converterRemoveH;
    CheckBox? converterPh;
    NumericUpDown? converterPhValue;
    CheckBox? converterMinimize;
    ComboBox? converterFf;
    NumericUpDown? converterSteps;
    readonly Color Bg = Color.FromArgb(7, 12, 21);
    readonly Color PanelBg = Color.FromArgb(15, 23, 36);
    readonly Color Panel2 = Color.FromArgb(19, 29, 45);
    readonly Color Border = Color.FromArgb(49, 65, 88);
    readonly Color Purple = Color.FromArgb(145, 74, 255);
    readonly Color TextColor = Color.FromArgb(239, 244, 252);
    readonly Color Muted = Color.FromArgb(154, 169, 191);
    readonly Color Green = Color.FromArgb(50, 220, 145);
    readonly Color Red = Color.FromArgb(255, 92, 108);

    AppConfig Config;
    Panel contentHost = null!;
    Panel navDrawer = null!;
    Button menuButton = null!;
    Label pageTitle = null!;
    Label pageSubtitle = null!;
    Label readyLabel = null!;
    Label? receptorPreparationStatusLabel;
    Label? pdbFixerStatusValue;
    Label? pdbFixerPythonValue;
    Label? pdbFixerVersionsValue;
    Label? pdbFixerOwnershipValue;
    bool meekoAvailable;
    string meekoPythonDisplay = "";
    PdbFixerStatus pdbFixerStatus = new("CHECKING", "Checking PDBFixer/OpenMM installation…", "", "", "", "", false, false);
    FlowLayoutPanel? dashboardStatusFlow;
    Label? vinaMonitorStatus;
    Label? vinaMonitorJobs;
    TextBox? vinaMonitorLog;
    readonly Dictionary<string, Panel> pages = new();
    ListBox? dockingReceptorList;
    ListBox? dockingLigandList;
    DataGridView? phytochemicalGrid;
    CancellationTokenSource? phytochemicalFilterCts;
    CancellationTokenSource? phytochemicalSendAllCts;
    ImppatDownloader? imppat;
    readonly List<ImppatDownloader.Compound> phytochemicalCompounds = new();
    bool navOpen;
    readonly Dictionary<string, GridSpec> receptorGrids = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Config = AppServices.LoadConfig();

        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            var detectedBabel = AppServices.DetectExecutable("obabel.exe");
            if (!string.IsNullOrWhiteSpace(detectedBabel))
                Config.OpenBabelPath = detectedBabel;
        }

        Text = $"ELB DockTool {Program.AppVersion} — CADD Suite";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1050, 720);
        Size = new Size(1280, 820);
        BackColor = Bg;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;
        TrySetApplicationIcon();
        BuildShell();
        BuildPages();
        ShowPage("Dashboard");
        _ = RefreshToolStatusAsync();
    }

    void TrySetApplicationIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppServices.BaseDir, "assets", "ELB_DockTool.ico");
            if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        }
        catch { }
    }

    void BuildShell()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 112, BackColor = PanelBg, Padding = new Padding(44, 20, 44, 14) };
        header.Paint += (_, e) => { using var p = new Pen(Border); e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };

        var logo = new LogoPanel { Dock = DockStyle.Right, Width = 76, Margin = new Padding(0, 0, 10, 0) };
        pageTitle = new Label { AutoSize = true, Text = "ELB DockTool", Font = new Font("Segoe UI", 25, FontStyle.Bold), ForeColor = TextColor, Location = new Point(44, 18) };
        pageSubtitle = new Label { AutoSize = true, Text = $"Computer-Aided Drug Design • Dock • Convert • Review • {Program.AppVersion}", Font = new Font("Segoe UI", 10.5F), ForeColor = Muted, Location = new Point(47, 63) };
        readyLabel = new Label { AutoSize = true, Text = "● READY", Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Green, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(0, 26) };
        header.Resize += (_, _) => { readyLabel.Left = header.ClientSize.Width - readyLabel.Width - logo.Width - 65; logo.Invalidate(); };
        header.Controls.Add(logo); header.Controls.Add(readyLabel); header.Controls.Add(pageSubtitle); header.Controls.Add(pageTitle);

        contentHost = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(34, 28, 34, 34), AutoScroll = true };
        menuButton = MakeButton("☰", 44, 44, Purple, TextColor);
        menuButton.FlatStyle = FlatStyle.Flat; menuButton.FlatAppearance.BorderSize = 0; menuButton.Font = new Font("Segoe UI Symbol", 16); menuButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        menuButton.Location = new Point(18, ClientSize.Height - 62); menuButton.BringToFront(); menuButton.Click += (_, _) => ToggleNav();
        Controls.Add(contentHost); Controls.Add(header); Controls.Add(menuButton);
        Resize += (_, _) => menuButton.Location = new Point(18, ClientSize.Height - 62);

        navDrawer = new Panel { Width = 270, Height = ClientSize.Height, BackColor = Panel2, Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom };
        navDrawer.Paint += (_, e) => { using var p = new Pen(Border); e.Graphics.DrawLine(p, navDrawer.Width - 1, 0, navDrawer.Width - 1, navDrawer.Height); };
        BuildNav(); Controls.Add(navDrawer); navDrawer.BringToFront(); menuButton.BringToFront();
    }

    void BuildNav()
    {
        // Compact, non-overlapping navigation header: keep the full product name and
        // CADD WORKSPACE label visible while reserving the right side for the logo.
        var navLogo = new LogoPanel { Location = new Point(186, 6), Size = new Size(76, 76), BackColor = Color.Transparent };
        var title = new Label { Text = "ELB DOCKTOOL", ForeColor = TextColor, Font = new Font("Segoe UI", 12.5F, FontStyle.Bold), AutoSize = false, Size = new Size(150, 25), Location = new Point(24, 24), TextAlign = ContentAlignment.MiddleLeft };
        var sub = new Label { Text = "CADD WORKSPACE", ForeColor = Muted, Font = new Font("Segoe UI", 7.5F, FontStyle.Bold), AutoSize = false, Size = new Size(150, 18), Location = new Point(26, 50), TextAlign = ContentAlignment.MiddleLeft };
        navDrawer.Controls.Add(navLogo); navDrawer.Controls.Add(title); navDrawer.Controls.Add(sub);
        int y = 98;
        foreach (var item in new[] { "Dashboard", "Molecular Docking", "Molecule Converter", "Phytochemical Library", "Results", "Settings" })
        {
            var b = new Button { Text = item, Tag = item, Location = new Point(18, y), Size = new Size(234, 46), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = TextColor, BackColor = Panel2, FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(29, 41, 62);
            b.Click += (_, _) => { ShowPage(item); ToggleNav(false); };
            navDrawer.Controls.Add(b); y += 54;
        }
        var close = MakeButton("Close menu", 234, 40, PanelBg, Muted); close.Location = new Point(18, y + 10); close.Click += (_, _) => ToggleNav(false); navDrawer.Controls.Add(close);
    }

    void ToggleNav(bool? open = null)
    {
        navOpen = open ?? !navOpen; navDrawer.Visible = navOpen; navDrawer.BringToFront(); menuButton.BringToFront();
        menuButton.Text = navOpen ? "×" : "☰";
    }

    void BuildPages()
    {
        pages["Dashboard"] = BuildDashboard();
        pages["Molecular Docking"] = BuildDocking();
        pages["Molecule Converter"] = BuildConverter();
        pages["Phytochemical Library"] = BuildPhytochemicalLibrary();
        pages["Results"] = BuildResults();
        pages["Settings"] = BuildSettings();
        foreach (var p in pages.Values) { p.Dock = DockStyle.Top; p.Visible = false; contentHost.Controls.Add(p); }
    }

    void ShowPage(string name)
    {
        foreach (var kv in pages) kv.Value.Visible = kv.Key == name;
        pageTitle.Text = name == "Dashboard" ? "ELB DockTool" : name;
        pageSubtitle.Text = name switch
        {
            "Dashboard" => $"Computer-Aided Drug Design • Dock • Convert • Review • {Program.AppVersion}",
            "Molecular Docking" => "Prepare receptors and ligands, define per-receptor search grids, then run AutoDock Vina.",
            "Molecule Converter" => "Convert ligand files with Open Babel without overwriting your originals.",
            "Phytochemical Library" => "Search IMPPAT phytochemicals, filter drug-likeness, inspect properties, download structures, and send compounds to Ligands.",
            "Results" => "Review docking output folders, scores and run logs in one place.",
            _ => "Configure executable paths and application defaults."
        };
        if (name == "Results") RefreshResults();
        if (name == "Molecular Docking") _ = RefreshToolStatusAsync();
        if (name == "Settings") _ = RefreshToolStatusAsync();
        contentHost.ScrollControlIntoView(pages[name]);
    }

    Panel PageBase()
    {
        var p = new Panel { BackColor = Bg, Padding = new Padding(0), AutoSize = true, MinimumSize = new Size(760, 0) };
        return p;
    }

    Panel Card(string title, string? subtitle = null)
    {
        var p = new Panel { BackColor = PanelBg, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(22), AutoSize = true, Dock = DockStyle.Top, MinimumSize = new Size(0, 100), Margin = new Padding(0, 0, 0, 18) };
        var t = new Label { Text = title, AutoSize = true, ForeColor = Purple, Font = new Font("Segoe UI", 13, FontStyle.Bold), Location = new Point(22, 18) };
        p.Controls.Add(t);
        if (!string.IsNullOrWhiteSpace(subtitle)) p.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9.5F), Location = new Point(22, 46), MaximumSize = new Size(900, 0) });
        return p;
    }

    Button MakeButton(string text, int w, int h, Color back, Color fore)
    {
        var b = new Button { Text = text, Size = new Size(w, h), BackColor = back, ForeColor = fore, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = Border; b.FlatAppearance.BorderSize = 1; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(Math.Min(back.R + 18, 255), Math.Min(back.G + 18, 255), Math.Min(back.B + 18, 255));
        return b;
    }

    Label MutedLabel(string s) => new() { Text = s, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9.5F) };
    TextBox Box(int width = 320) => new() { Width = width, Height = 32, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10F) };
    ListBox StyledListBox(Point location, Size size) => new()
    {
        Location = location,
        Size = size,
        BackColor = Color.FromArgb(9, 15, 25),
        ForeColor = TextColor,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI", 9.5F),
        IntegralHeight = false,
        ItemHeight = 22,
        HorizontalScrollbar = true,
        ScrollAlwaysVisible = false
    };

    Panel BuildDashboard()
    {
        var p = PageBase();
        var hero = Card("A clean docking workspace", "Designed around the actual workflow: prepare → define active site → dock → review.");
        hero.Height = 180;
        var start = MakeButton("START MOLECULAR DOCKING", 250, 44, Purple, Color.White); start.Location = new Point(22, 88); start.Click += (_, _) => ShowPage("Molecular Docking");
        var conv = MakeButton("OPEN CONVERTER", 190, 44, Panel2, TextColor); conv.Location = new Point(286, 88); conv.Click += (_, _) => ShowPage("Molecule Converter");
        var clearData = MakeButton("CLEAR DATA", 150, 44, Color.FromArgb(70, 28, 38), Color.White);
        clearData.Location = new Point(492, 88);
        clearData.Click += (_, _) => ClearProjectData();
        hero.Controls.Add(start); hero.Controls.Add(conv); hero.Controls.Add(clearData); p.Controls.Add(hero);

        var status = Card("Tool Status", "Live tool availability."); status.Height = 180;
        var statusFlow = new FlowLayoutPanel
        {
            Location = new Point(22, 78),
            Size = new Size(Math.Max(300, status.ClientSize.Width - 44), 70),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoScroll = true,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0)
        };
        dashboardStatusFlow = statusFlow;
        status.Resize += (_, _) =>
        {
            statusFlow.Width = Math.Max(300, status.ClientSize.Width - 44);
        };
        RefreshDashboardStatus();
        status.Controls.Add(statusFlow); p.Controls.Add(status);

        var workflow = Card("Workflow", "Simple receptor-to-docking sequence."); workflow.Height = 205;
        var text = new Label { Text = "1   Input PDB/mmCIF (local structure or PDB ID)\n2   PDB sanity/normalization\n3   PDBFixer repair\n4   Cleanup: remove crystallographic water + non-protein HETATM\n5   Polar H (optional)\n6   Meeko → receptor PDBQT\n7   Define docking box → AutoDock Vina → review results", AutoSize = true, ForeColor = TextColor, Font = new Font("Segoe UI", 10.5F), Location = new Point(22, 62) };
        workflow.Controls.Add(text); p.Controls.Add(workflow);

        // A fresh quote panel is shown every time the dashboard is created. The quote
        // banks contain 1000 CADD quotes and 1000 pharmaceutical/drug-discovery quotes.
        var quoteCard = Card("Daily CADD Insight", null);
        quoteCard.Height = 270;
        var quoteCategory = new Label { AutoSize = true, ForeColor = Green, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Location = new Point(22, 76) };
        var quoteText = new Label { AutoSize = false, ForeColor = TextColor, Font = new Font("Segoe UI", 14F, FontStyle.Italic), Location = new Point(22, 102), Size = new Size(1010, 105), MaximumSize = new Size(1010, 105), AutoEllipsis = false };
        var nextQuote = MakeButton("NEW QUOTE", 135, 36, Panel2, TextColor); nextQuote.Location = new Point(22, 218);
        void ShowRandomQuote()
        {
            bool cadd = Random.Shared.Next(2) == 0;
            var q = QuoteBank.GetRandom(cadd);
            quoteCategory.Text = cadd ? "CADD / MOLECULAR DOCKING" : "PHARMACEUTICAL SCIENCES / DRUG DISCOVERY";
            quoteText.Text = "“" + q + "”";
        }
        nextQuote.Click += (_, _) => ShowRandomQuote();
        ShowRandomQuote();
        quoteCard.Controls.AddRange(new Control[] { quoteCategory, quoteText, nextQuote });
        p.Controls.Add(quoteCard);
        return p;
    }

    Control StatusChip(string name, bool ok)
    {
        // Clean availability bar: color communicates state without status text or dots.
        var state = ok ? Green : Red;
        var panel = new Panel
        {
            Width = 270,
            Height = 52,
            BackColor = ok ? Color.FromArgb(18, 43, 36) : Color.FromArgb(46, 29, 34),
            Margin = new Padding(0, 0, 12, 0),
            BorderStyle = BorderStyle.FixedSingle
        };

        var indicator = new Panel
        {
            Width = 6,
            Height = panel.Height - 2,
            Location = new Point(0, 0),
            BackColor = state
        };

        var label = new Label
        {
            Text = name,
            ForeColor = TextColor,
            AutoSize = true,
            Location = new Point(20, 17),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };

        panel.Controls.Add(indicator);
        panel.Controls.Add(label);
        return panel;
    }

    IEnumerable<(string name, bool ok)> ToolEntries() => new[]
    {
        ("AutoDock Vina", AppServices.Exists(Config.VinaPath)),
        ("vina_split", AppServices.Exists(Config.VinaSplitPath)),
        ("Open Babel", AppServices.Exists(Config.OpenBabelPath)),
        ("Meeko", meekoAvailable),
        ($"PDBFixer — {pdbFixerStatus.State}" + (string.IsNullOrWhiteSpace(pdbFixerStatus.PdbFixerVersion) ? "" : $" ({pdbFixerStatus.PdbFixerVersion})"), pdbFixerStatus.IsUsable)
    };

    string ProjectFolder(string name)
    {
        string folder = Path.Combine(AppServices.BaseDir, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    string ReceptorsFolder() => ProjectFolder("receptors");
    string LigandsFolder() => ProjectFolder("ligands");
    string ConvertFolder() => ProjectFolder(Path.Combine("ligands", "convert"));

    // If the active ligands came from one minimization run, conversions are kept
    // beside that run. Otherwise they use the normal ligands\convert location.
    string ConversionBaseFolderForInputs(IEnumerable<string> inputs)
    {
        var paths = inputs.Where(File.Exists)
            .Select(Path.GetFullPath)
            .ToList();

        if (paths.Count == 0)
            return ConvertFolder();

        string? runFolder = null;
        foreach (var path in paths)
        {
            string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string marker = Path.DirectorySeparatorChar + "minimized" + Path.DirectorySeparatorChar;
            int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return ConvertFolder();

            int runStart = markerIndex + marker.Length;
            int nextSep = normalized.IndexOf(Path.DirectorySeparatorChar, runStart);
            if (nextSep < 0)
                return ConvertFolder();

            string candidate = normalized.Substring(0, nextSep);
            runFolder = runFolder == null ? candidate
                : string.Equals(runFolder, candidate, StringComparison.OrdinalIgnoreCase) ? runFolder : null;

            if (runFolder == null)
                return ConvertFolder();
        }

        return runFolder != null && Directory.Exists(runFolder)
            ? Path.Combine(runFolder, "convert")
            : ConvertFolder();
    }

    string CopyIntoProjectFolder(string source, string folder)
    {
        Directory.CreateDirectory(folder);
        string baseName = SafeName(Path.GetFileNameWithoutExtension(source));
        string ext = Path.GetExtension(source);
        string destination = Path.Combine(folder, baseName + ext);
        int n = 2;
        while (File.Exists(destination) && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            destination = Path.Combine(folder, $"{baseName}_{n}{ext}");
            n++;
        }
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, false);
        return destination;
    }

    void AddStoredFiles(ListBox list, string folder, string role)
    {
        // LOAD SAVED has intentionally different storage rules by role:
        //   • Receptors: only publish\receptors\*.pdbqt
        //   • Ligands: only publish\ligands\convert\*.pdbqt
        // Minimization runs are NEVER searched by LOAD SAVED. Their PDBQT files
        // remain available only through the minimization workflow/list that created them.
        string sourceFolder = role.Equals("receptor", StringComparison.OrdinalIgnoreCase)
            ? ReceptorsFolder()
            : ConvertFolder();

        Directory.CreateDirectory(sourceFolder);

        var files = Directory.EnumerateFiles(
                sourceFolder,
                "*.pdbqt",
                SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetExtension(f), ".pdbqt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var file in files)
        {
            if (role.Equals("receptor", StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidateMoleculeFile(file, "receptor").Ok) continue;
            }
            else
            {
                if (!ValidateMoleculeFile(file, "ligand").Ok) continue;
            }

            if (!list.Items.Contains(file))
            {
                list.Items.Add(file);
                added++;
            }
        }

        MessageBox.Show(
            added > 0 ? $"Loaded {added} saved {role}(s) from:\n{sourceFolder}" : $"No saved {role} files were found in:\n{sourceFolder}",
            "ELB DockTool — Load Saved",
            MessageBoxButtons.OK,
            added > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Information);
    }

    Panel BuildPhytochemicalLibrary()
    {
        var p = PageBase();
        imppat ??= new ImppatDownloader();
        // Phytochemical UI state is kept at form level so Dashboard → CLEAR DATA
        // can cancel active work and clear the visible data everywhere.
        DataGridView? compoundGrid = null;

        // The preview is kept on the right side of the compound list.
        var structureCache = new Dictionary<string, Image?>(StringComparer.OrdinalIgnoreCase);
        int previewRequest = 0;
        var previewTitle = MutedLabel("2D Structure Preview");
        previewTitle.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        var previewId = MutedLabel("Move the mouse over a compound");
        var previewBox = new PictureBox
        {
            Size = new Size(300, 220),
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            SizeMode = PictureBoxSizeMode.Zoom
        };
        var previewPanel = new Panel
        {
            Location = new Point(805, 90),
            Size = new Size(315, 300),
            BackColor = Panel2,
            BorderStyle = BorderStyle.FixedSingle
        };
        previewTitle.Location = new Point(10, 10);
        previewId.Location = new Point(10, 34);
        previewBox.Location = new Point(7, 62);
        previewPanel.Controls.AddRange(new Control[] { previewTitle, previewId, previewBox });

        var searchCard = Card("1. Plant Search", "Search IMPPAT 2.0 by plant species. Results are loaded into the compound list below.");
        searchCard.Height = 125;
        var plantBox = Box(520); plantBox.Location = new Point(22, 72); plantBox.Text = "Ocimum tenuiflorum";
        var search = MakeButton("SEARCH PLANT", 160, 32, Purple, Color.White); search.Location = new Point(555, 72);
        var searchStatus = MutedLabel("Ready"); searchStatus.Location = new Point(730, 79);
        search.Click += async (_, _) =>
        {
            string plant = plantBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(plant)) { MessageBox.Show("Enter a plant species name.", "IMPPAT"); return; }
            search.Enabled = false;
            try
            {
                searchStatus.Text = "Searching IMPPAT...";
                var found = await imppat.FetchPhytochemicalIdsAsync(plant);
                phytochemicalCompounds.Clear(); phytochemicalCompounds.AddRange(found);
                PopulatePhytochemicalGrid(compoundGrid!, phytochemicalCompounds);
                searchStatus.Text = $"{found.Count} compounds";
                previewId.Text = "Move the mouse over a compound";
                previewBox.Image = null;
            }
            catch (Exception ex)
            {
                searchStatus.Text = "Search failed";
                MessageBox.Show(ex.Message, "IMPPAT — Plant Search", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { search.Enabled = true; }
        };
        searchCard.Controls.AddRange(new Control[] { plantBox, search, searchStatus });

        var listCard = Card(
            "2. Compound List",
            "Hover over a compound to preview its 2D structure. Select one to filter, inspect key properties, download SDF/PDBQT, or send it to Docking."
        );
        listCard.Height = 420;
        // Keep the grid below the two-line subtitle so its top border/header is never clipped.
        // The preview is aligned with the grid and remains on the right.

        compoundGrid = new DataGridView
        {
            Location = new Point(22, 90),
            Size = new Size(760, 225),
            BackgroundColor = Color.FromArgb(9, 15, 25),
            ForeColor = TextColor,
            GridColor = Border,
            BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Panel2, ForeColor = TextColor, Font = new Font("Segoe UI", 9, FontStyle.Bold)
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor,
                SelectionBackColor = Color.FromArgb(47, 35, 75), SelectionForeColor = Color.White
            }
        };
        phytochemicalGrid = compoundGrid;
        compoundGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "id", HeaderText = "IMPHY ID", DataPropertyName = "Id", Width = 145 });
        compoundGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Compound", DataPropertyName = "Name", Width = 570 });

        compoundGrid.CellMouseEnter += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= compoundGrid.Rows.Count) return;
            if (compoundGrid.Rows[e.RowIndex].DataBoundItem is not ImppatDownloader.Compound c) return;

            int request = ++previewRequest;
            previewId.Text = $"{c.Id}  •  {c.Name}";
            previewBox.Image = null;

            if (structureCache.TryGetValue(c.Id, out var cached))
            {
                previewBox.Image = cached;
                return;
            }

            previewId.Text = $"{c.Id}  •  Loading structure...";
            try
            {
                string url = $"https://cb.imsc.res.in/imppat/images/2D_IMAGE/PNG/{Uri.EscapeDataString(c.Id)}.png";
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                byte[] bytes = await client.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                var bmp = new Bitmap(tmp);

                structureCache[c.Id] = bmp;
                if (request == previewRequest && !previewPanel.IsDisposed)
                {
                    previewId.Text = $"{c.Id}  •  {c.Name}";
                    previewBox.Image = bmp;
                }
            }
            catch
            {
                structureCache[c.Id] = null;
                if (request == previewRequest && !previewPanel.IsDisposed)
                    previewId.Text = $"{c.Id}  •  Structure unavailable";
            }
        };

        listCard.Controls.Add(compoundGrid);
        listCard.Controls.Add(previewPanel);

        var filterLabel = MutedLabel("Drug-likeness filters:"); filterLabel.Location = new Point(22, 325);
        var filterChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        string[] filterNames = { "lipinski", "ghose", "veber", "egan", "gsk", "pfizer" };
        int fx = 170; // shifted slightly right as requested
        foreach (string name in filterNames)
        {
            var cb = new CheckBox
            {
                Text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name),
                AutoSize = true,
                ForeColor = TextColor,
                Location = new Point(fx, 323),
                BackColor = Color.Transparent
            };
            filterChecks[name] = cb;
            listCard.Controls.Add(cb);
            fx += 88;
        }

        var apply = MakeButton("APPLY FILTERS", 135, 34, Panel2, TextColor); apply.Location = new Point(22, 360);
        var clearFilter = MakeButton("CLEAR FILTERS", 135, 34, Panel2, TextColor); clearFilter.Location = new Point(165, 360);
        var stopFilter = MakeButton("STOP FILTER", 125, 34, Panel2, TextColor); stopFilter.Location = new Point(308, 360);
        stopFilter.Enabled = false;
        var filterStatus = MutedLabel(""); filterStatus.Location = new Point(445, 368);

        apply.Click += async (_, _) =>
        {
            if (phytochemicalFilterCts != null) return;

            var active = filterChecks.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToList();
            if (active.Count == 0)
            {
                PopulatePhytochemicalGrid(compoundGrid!, phytochemicalCompounds);
                filterStatus.Text = "All compounds";
                return;
            }
            if (phytochemicalCompounds.Count == 0) { filterStatus.Text = "Search for a plant first"; return; }

            phytochemicalFilterCts = new CancellationTokenSource();
            var token = phytochemicalFilterCts.Token;
            apply.Enabled = false;
            clearFilter.Enabled = false;
            stopFilter.Enabled = true;

            try
            {
                var passed = new List<ImppatDownloader.Compound>();
                int checkedCount = 0;
                foreach (var c in phytochemicalCompounds)
                {
                    if (token.IsCancellationRequested) break;

                    var props = await imppat.CheckDruglikenessAsync(c.Id, active);
                    checkedCount++;

                    if (token.IsCancellationRequested) break;

                    if (props != null && active.All(f => props.TryGetValue(f, out bool ok) && ok))
                        passed.Add(c);

                    filterStatus.Text = $"Checking {checkedCount}/{phytochemicalCompounds.Count}...";
                    await Task.Yield();
                }

                if (token.IsCancellationRequested)
                {
                    filterStatus.Text = $"Stopped at {checkedCount}/{phytochemicalCompounds.Count}";
                }
                else
                {
                    PopulatePhytochemicalGrid(compoundGrid!, passed);
                    filterStatus.Text = $"{passed.Count} passed";
                }
            }
            catch (OperationCanceledException)
            {
                filterStatus.Text = "Filter stopped";
            }
            finally
            {
                phytochemicalFilterCts?.Dispose();
                phytochemicalFilterCts = null;
                apply.Enabled = true;
                clearFilter.Enabled = true;
                stopFilter.Enabled = false;
            }
        };

        stopFilter.Click += (_, _) =>
        {
            if (phytochemicalFilterCts != null)
            {
                phytochemicalFilterCts.Cancel();
                filterStatus.Text = "Stopping filter...";
            }
        };

        clearFilter.Click += (_, _) =>
        {
            if (phytochemicalFilterCts != null) return;
            foreach (var cb in filterChecks.Values) cb.Checked = false;
            PopulatePhytochemicalGrid(compoundGrid!, phytochemicalCompounds);
            filterStatus.Text = "All compounds";
        };
        listCard.Controls.AddRange(new Control[] { filterLabel, apply, clearFilter, stopFilter, filterStatus });

        var actionCard = Card(
            "3. Properties & Structure",
            "Inspect key properties, download a selected structure, or send one/all visible compounds to Docking → Ligands."
        );
        actionCard.Height = 220;

        // Use a fixed two-row table with generous column widths so every action label remains fully visible at normal Windows DPI.
        // This avoids FlowLayoutPanel clipping/wrapping that previously reduced labels such as
        // "DOWNLOAD PDBQT" and "SEND ALL AS PDBQT" to just "DOWNLOAD"/partial text.
        var actionButtons = new TableLayoutPanel
        {
            Location = new Point(22, 74),
            Size = new Size(920, 86),
            ColumnCount = 4,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        actionButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        actionButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var props = MakeButton("PROPERTIES", 129, 36, Panel2, TextColor);
        var send = MakeButton("SEND TO LIGANDS (PDBQT)", 224, 36, Purple, Color.White);
        var sdf = MakeButton("DOWNLOAD SDF", 174, 36, Panel2, TextColor);
        var pdbqt = MakeButton("DOWNLOAD PDBQT", 220, 36, Panel2, TextColor);
        var openFolder = MakeButton("OPEN LIBRARY FOLDER", 224, 36, Panel2, TextColor);
        var sendAllPdbqt = MakeButton("SEND ALL AS PDBQT", 220, 36, Purple, Color.White);
        var sendAllSdf = MakeButton("SEND ALL AS SDF", 174, 36, Purple, Color.White);
        var stopSendAll = MakeButton("STOP SEND ALL", 184, 36, Panel2, TextColor);
        stopSendAll.Enabled = false;

        actionButtons.Controls.Add(props, 0, 0);
        actionButtons.Controls.Add(send, 1, 0);
        actionButtons.Controls.Add(sdf, 2, 0);
        actionButtons.Controls.Add(pdbqt, 3, 0);
        actionButtons.Controls.Add(openFolder, 0, 1);
        actionButtons.Controls.Add(sendAllPdbqt, 1, 1);
        actionButtons.Controls.Add(sendAllSdf, 2, 1);
        actionButtons.Controls.Add(stopSendAll, 3, 1);

        var actionStatus = MutedLabel("Select a compound above."); actionStatus.Location = new Point(22, 172);

        actionCard.Controls.Add(actionButtons);
        actionCard.Controls.Add(actionStatus);

        ImppatDownloader.Compound? SelectedCompound()
        {
            if (compoundGrid?.CurrentRow?.DataBoundItem is ImppatDownloader.Compound c) return c;
            if (compoundGrid != null && compoundGrid.SelectedRows.Count > 0 &&
                compoundGrid.SelectedRows[0].DataBoundItem is ImppatDownloader.Compound c2) return c2;
            return null;
        }

        props.Click += async (_, _) =>
        {
            var c = SelectedCompound();
            if (c == null) { MessageBox.Show("Select a compound first.", "IMPPAT"); return; }

            using var dlg = new Form
            {
                Text = $"{c.Id} — Key Properties",
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(850, 650),
                MinimumSize = new Size(760, 560),
                BackColor = Bg,
                ForeColor = TextColor,
                Font = Font
            };

            var header = new Label
            {
                Text = $"{c.Id}  •  {c.Name}",
                AutoSize = true,
                ForeColor = TextColor,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                Location = new Point(16, 12)
            };

            var tabs = new TabControl
            {
                Location = new Point(12, 45),
                Size = new Size(810, 520),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            var grids = new Dictionary<string, DataGridView>();
            foreach (string tab in new[] { "physchem", "druglike", "admet" })
            {
                var page = new TabPage(ShowTabTitle(tab)) { BackColor = Bg, ForeColor = TextColor };
                var grid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    RowHeadersVisible = false,
                    AutoGenerateColumns = false,
                    BackgroundColor = Color.FromArgb(9, 15, 25),
                    ForeColor = TextColor,
                    GridColor = Border,
                    BorderStyle = BorderStyle.FixedSingle,
                    EnableHeadersVisualStyles = false,
                    ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
                    {
                        BackColor = Panel2, ForeColor = TextColor, Font = new Font("Segoe UI", 9, FontStyle.Bold)
                    },
                    DefaultCellStyle = new DataGridViewCellStyle
                    {
                        BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor,
                        SelectionBackColor = Color.FromArgb(47, 35, 75), SelectionForeColor = Color.White,
                        Padding = new Padding(6, 3, 6, 3)
                    },
                    AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
                };
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Property", Name = "property", Width = 390 });
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", Name = "value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
                page.Controls.Add(grid);
                tabs.TabPages.Add(page);
                grids[tab] = grid;
            }

            var note = MutedLabel("Only key docking/drug-discovery properties are shown. Database identifiers and ENSP/protein target IDs are intentionally excluded.");
            note.Location = new Point(16, 575);
            note.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            dlg.Controls.AddRange(new Control[] { header, tabs, note });
            dlg.Shown += async (_, _) =>
            {
                foreach (var tab in grids.Keys)
                    _ = LoadImportantPropertyGridAsync(grids[tab], c.Id, tab);
            };
            dlg.ShowDialog(this);
        };

        sdf.Click += async (_, _) => await DownloadSelectedPhytochemical(SelectedCompound(), "sdf", actionStatus, false);
        pdbqt.Click += async (_, _) => await DownloadSelectedPhytochemical(SelectedCompound(), "pdbqt", actionStatus, false);
        send.Click += async (_, _) => await DownloadSelectedPhytochemical(SelectedCompound(), "pdbqt", actionStatus, true);

        async Task SendAllVisibleAsync(string format)
        {
            var visible = compoundGrid!.Rows
                .Cast<DataGridViewRow>()
                .Select(r => r.DataBoundItem as ImppatDownloader.Compound)
                .Where(c => c != null)
                .Cast<ImppatDownloader.Compound>()
                .ToList();

            if (visible.Count == 0)
            {
                MessageBox.Show("There are no compounds in the current list.", "IMPPAT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string fmt = format.ToLowerInvariant();
            string label = fmt == "sdf" ? "SDF" : "PDBQT";
            var confirm = MessageBox.Show(
                $"Send all {visible.Count} visible compounds to Docking → Ligands as {label} files?",
                $"IMPPAT — Send All {label}", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes || phytochemicalSendAllCts != null) return;

            phytochemicalSendAllCts = new CancellationTokenSource();
            var token = phytochemicalSendAllCts.Token;
            sendAllPdbqt.Enabled = false;
            sendAllSdf.Enabled = false;
            stopSendAll.Enabled = true;
            apply.Enabled = false;

            try
            {
                int added = 0, failed = 0;
                for (int i = 0; i < visible.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    actionStatus.Text = $"Sending {i + 1}/{visible.Count} as {label}: {visible[i].Id}...";
                    try
                    {
                        string folder = LigandsFolder();
                        Directory.CreateDirectory(folder);
                        string url = ImppatDownloader.BuildDownloadUrl(visible[i].Id, fmt);
                        string dest = Path.Combine(folder, $"{visible[i].Id}.{fmt}");
                        string result = await imppat!.DownloadFileAsync(url, dest);

                        if (result is "ok" or "skip")
                        {
                            if (dockingLigandList != null &&
                                !dockingLigandList.Items.Cast<string>().Any(x => string.Equals(x, dest, StringComparison.OrdinalIgnoreCase)))
                                dockingLigandList.Items.Add(dest);
                            added++;
                        }
                        else failed++;
                    }
                    catch { failed++; }

                    if (token.IsCancellationRequested) break;
                    await Task.Delay(150);
                }

                actionStatus.Text = token.IsCancellationRequested
                    ? $"Stopped: sent {added}/{visible.Count} as {label}" + (failed > 0 ? $" • {failed} failed/missing" : "")
                    : $"Sent {added}/{visible.Count} to Ligands as {label}" + (failed > 0 ? $" • {failed} failed/missing" : "");
            }
            finally
            {
                phytochemicalSendAllCts?.Dispose();
                phytochemicalSendAllCts = null;
                sendAllPdbqt.Enabled = true;
                sendAllSdf.Enabled = true;
                stopSendAll.Enabled = false;
                apply.Enabled = true;
            }
        }

        sendAllPdbqt.Click += async (_, _) => await SendAllVisibleAsync("pdbqt");
        sendAllSdf.Click += async (_, _) => await SendAllVisibleAsync("sdf");

        stopSendAll.Click += (_, _) =>
        {
            if (phytochemicalSendAllCts != null)
            {
                stopSendAll.Enabled = false;
                actionStatus.Text = "Stopping send-all...";
                phytochemicalSendAllCts.Cancel();
            }
        };

        openFolder.Click += (_, _) =>
        {
            string folder = ProjectFolder("phytochemical_library");
            Directory.CreateDirectory(folder);
            OpenFolder(folder);
        };

        p.Controls.Add(actionCard); p.Controls.Add(listCard); p.Controls.Add(searchCard);
        return p;
    }

    static string ShowTabTitle(string tab) => tab switch
    {
        "physchem" => "Physicochemical",
        "druglike" => "Drug-likeness",
        "admet" => "ADMET",
        _ => tab
    };

    void PopulatePhytochemicalGrid(DataGridView grid, IEnumerable<ImppatDownloader.Compound> compounds)
    {
        grid.DataSource = null;
        grid.DataSource = compounds.ToList();
    }

    static bool PropertyContainsAny(string key, params string[] terms)
    {
        string k = key.ToLowerInvariant();
        return terms.Any(t => k.Contains(t.ToLowerInvariant()));
    }

    static Dictionary<string, string> ImportantProperties(Dictionary<string, string> data, string tab)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in data)
        {
            string key = kv.Key.Trim();
            string value = kv.Value?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            if (Regex.IsMatch(key, @"ENSP\d+", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(value, @"ENSP\d+", RegexOptions.IgnoreCase)) continue;

            bool keep = tab switch
            {
                "physchem" => PropertyContainsAny(key,
                    "molecular weight",
                    "log p",
                    "topological polar surface area",
                    "hydrogen bond acceptors",
                    "hydrogen bond donors",
                    "heavy atoms",
                    "heteroatoms",
                    "nitrogen atoms",
                    "sulfur atoms",
                    "rotatable bonds",
                    "aromatic rings",
                    "total number of rings"),

                "druglike" => PropertyContainsAny(key,
                    "lipinski", "ghose", "veber", "egan", "gsk", "pfizer", "qed"),

                "admet" => PropertyContainsAny(key,
                    "bioavailability score",
                    "solubility class [esol]",
                    "solubility class [silicos-it]",
                    "blood brain barrier",
                    "gastrointestinal absorption",
                    "log k",
                    "pains structural alerts",
                    "brenk structural alerts",
                    "cyp1a2 inhibitor",
                    "cyp2c19 inhibitor",
                    "cyp2c9 inhibitor",
                    "cyp2d6 inhibitor",
                    "cyp3a4 inhibitor",
                    "p-glycoprotein substrate"),

                _ => false
            };

            if (keep)
                output[key] = value;
        }

        return output;
    }

    async Task LoadImportantPropertyGridAsync(DataGridView grid, string imphyId, string tab)
    {
        try
        {
            string template = tab switch
            {
                "physchem" => "https://cb.imsc.res.in/imppat/physicochemicalproperties/{0}",
                "druglike" => "https://cb.imsc.res.in/imppat/druglikeproperties/{0}",
                "admet" => "https://cb.imsc.res.in/imppat/admetproperties/{0}",
                _ => ""
            };

            var data = await imppat!.ParsePropertyTableAsync(
                string.Format(template, Uri.EscapeDataString(imphyId)));

            grid.Rows.Clear();

            if (data == null || data.Count == 0)
            {
                grid.Rows.Add("Status", "No data available");
                return;
            }

            var important = ImportantProperties(data, tab);
            foreach (var kv in important)
                grid.Rows.Add(kv.Key, kv.Value);

            if (grid.Rows.Count == 0)
                grid.Rows.Add("Status", "No key properties available for this compound");
        }
        catch (Exception ex)
        {
            grid.Rows.Clear();
            grid.Rows.Add("Status", ex.Message);
        }
    }

    async Task DownloadSelectedPhytochemical(ImppatDownloader.Compound? compound, string format, Label status, bool sendToLigands)
    {
        if (compound == null) { MessageBox.Show("Select a compound first.", "IMPPAT"); return; }

        try
        {
            string folder = sendToLigands ? LigandsFolder() : ProjectFolder("phytochemical_library");
            Directory.CreateDirectory(folder);

            string url = ImppatDownloader.BuildDownloadUrl(compound.Id, format);
            string dest = Path.Combine(folder, $"{compound.Id}.{format}");
            string result = await imppat!.DownloadFileAsync(url, dest);

            if (sendToLigands && format.Equals("pdbqt", StringComparison.OrdinalIgnoreCase))
            {
                if (dockingLigandList != null &&
                    !dockingLigandList.Items.Cast<string>().Any(x =>
                        string.Equals(x, dest, StringComparison.OrdinalIgnoreCase)))
                    dockingLigandList.Items.Add(dest);

                status.Text = $"Sent to Ligands: {Path.GetFileName(dest)}";
            }
            else
            {
                status.Text = $"{format.ToUpperInvariant()}: {result} — {Path.GetFileName(dest)}";
            }
        }
        catch (Exception ex)
        {
            status.Text = "Failed";
            MessageBox.Show(ex.Message, "IMPPAT — Download", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    Panel BuildDocking()
    {
        Directory.CreateDirectory(ReceptorsFolder());
        Directory.CreateDirectory(LigandsFolder());
        Directory.CreateDirectory(ConvertFolder());

        var p = PageBase();
        var row = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 5, AutoSize = true, Padding = new Padding(0), BackColor = Color.Transparent };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        // Receptor-preparation stage. The existing Receptors panel remains the
        // docking hand-off point; this inspector only edits a working PDB copy.
        var prep = Card("1. Receptor Preparation", "Inspect/edit a working copy → optional PDBFixer repair → one cleanup stage → optional polar H → Meeko PDBQT.");
        prep.Height = 690;

        var prepInputLabel = MutedLabel("Input receptor");
        prepInputLabel.Location = new Point(22, 72);
        var prepInput = Box(400);
        prepInput.Location = new Point(22, 94);
        var prepBrowse = MakeButton("OPEN PDB/CIF", 145, 32, Panel2, TextColor);
        prepBrowse.Location = new Point(430, 94);
        var rcsbPdbLabel = MutedLabel("RCSB Protein Data Bank ID");
        rcsbPdbLabel.Location = new Point(22, 130);
        var rcsbPdbId = Box(150);
        rcsbPdbId.Location = new Point(22, 152);
        rcsbPdbId.MaxLength = 4;
        rcsbPdbId.CharacterCasing = CharacterCasing.Upper;
        var rcsbDownload = MakeButton("DOWNLOAD PDB", 155, 32, Panel2, TextColor);
        rcsbDownload.Location = new Point(182, 152);

        // Right-side structure inspector: the inspector is a separate docking-stage
        // column so it never overlaps the receptor-preparation controls. HETATM
        // entries are grouped by PDB residue/molecule rather than shown atom-by-atom.
        var inspector = new Panel { BackColor = Color.FromArgb(13, 20, 31), BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0), Padding = new Padding(12) };
        var inspectorTitle = new Label { Text = "RECEPTOR STRUCTURE INSPECTOR", Location = new Point(12, 10), AutoSize = true, ForeColor = TextColor, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
        var atomDetails = new TextBox { Location = new Point(12, 38), Size = new Size(420, 120), Multiline = true, ReadOnly = true, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 8.5F), ScrollBars = ScrollBars.Both, WordWrap = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Text = "Select a category below to inspect its entries. SELECT ALL applies only to the active category." };
        inspector.Controls.Add(inspectorTitle); inspector.Controls.Add(atomDetails);

        var residueLabel = new Label { Text = "RESIDUES", Location = new Point(12, 166), AutoSize = true, ForeColor = Purple, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        var residueList = StyledListBox(new Point(12, 189), new Size(420, 78)); residueList.SelectionMode = SelectionMode.MultiSimple;
        var hetLabel = new Label { Text = "HETATM GROUPS", Location = new Point(12, 273), AutoSize = true, ForeColor = Color.FromArgb(255, 174, 82), Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        var hetList = StyledListBox(new Point(12, 296), new Size(420, 82)); hetList.SelectionMode = SelectionMode.MultiSimple;
        var waterLabel = new Label { Text = "WATER MOLECULES", Location = new Point(12, 384), AutoSize = true, ForeColor = Green, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        var waterList = StyledListBox(new Point(12, 407), new Size(420, 62)); waterList.SelectionMode = SelectionMode.MultiSimple;
        var chainLabel = new Label { Text = "CHAINS", Location = new Point(12, 475), AutoSize = true, ForeColor = Color.FromArgb(80, 200, 255), Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        var chainList = StyledListBox(new Point(12, 498), new Size(420, 58)); chainList.SelectionMode = SelectionMode.MultiSimple;
        inspector.Controls.AddRange(new Control[] { residueLabel, residueList, hetLabel, hetList, waterLabel, waterList, chainLabel, chainList });

        var inspectorActions = new FlowLayoutPanel { Location = new Point(12, 568), Size = new Size(645, 76), WrapContents = true, FlowDirection = FlowDirection.LeftToRight, AutoScroll = false, BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0) };
        var selectAllInspector = MakeButton("SELECT ALL", 150, 34, Panel2, TextColor);
        var deleteSelected = MakeButton("DELETE SELECTED", 150, 34, Color.FromArgb(92, 34, 45), Color.White);
        var restoreOriginal = MakeButton("RESTORE ORIGINAL", 150, 34, Panel2, TextColor);
        var viewInspected = MakeButton("VIEW PROTEIN", 150, 34, Purple, Color.White);
        var saveInspected = MakeButton("SAVE PROTEIN", 150, 34, Panel2, TextColor);
        inspectorActions.Controls.AddRange(new Control[] { selectAllInspector, deleteSelected, restoreOriginal, viewInspected, saveInspected });
        inspector.Controls.Add(inspectorActions);

        List<ReceptorAtomRecord> inspectorAtoms = new();
        List<ReceptorHetGroup> inspectorHetGroups = new();
        string? inspectorWorkingPath = null;
        string? inspectorOriginalPath = null;

        void RefreshInspector(string path, bool resetOriginal = true)
        {
            residueList.Items.Clear(); hetList.Items.Clear(); waterList.Items.Clear(); chainList.Items.Clear();
            atomDetails.Text = "Select a category below to inspect its entries. SELECT ALL applies only to the active category.";
            inspectorAtoms = new List<ReceptorAtomRecord>();
            inspectorHetGroups = new List<ReceptorHetGroup>();
            if (resetOriginal || string.IsNullOrWhiteSpace(inspectorOriginalPath)) inspectorOriginalPath = path;
            inspectorWorkingPath = path;
            string ext = Path.GetExtension(path);
            if (!(ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ent", StringComparison.OrdinalIgnoreCase)) || !File.Exists(path))
            {
                atomDetails.Text = "Detailed inspector currently requires a PDB-format receptor.\r\nmmCIF preparation remains delegated to Meeko.";
                return;
            }
            try
            {
                inspectorAtoms = ReceptorStructureInspector.ReadPdb(path);
                foreach (var chain in inspectorAtoms
                    .Where(a => a.RecordType == "ATOM" && !a.IsWater)
                    .Select(a => string.IsNullOrWhiteSpace(a.Chain) ? "(blank)" : a.Chain.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    chainList.Items.Add(chain);

                var residueEntries = inspectorAtoms.Where(a => a.RecordType == "ATOM" && !a.IsWater)
                    .GroupBy(a => a.ResidueKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                foreach (var a in residueEntries) residueList.Items.Add(a);
                residueList.DisplayMember = nameof(ReceptorAtomRecord.DisplayResidueKey);

                inspectorHetGroups = inspectorAtoms.Where(a => a.IsHetAtom && !a.IsWater)
                    .GroupBy(a => a.ResidueKey, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new ReceptorHetGroup(g.Key, g.ToList())).ToList();
                foreach (var g in inspectorHetGroups) hetList.Items.Add(g);
                hetList.DisplayMember = nameof(ReceptorHetGroup.DisplayName);

                foreach (var waterGroup in inspectorAtoms.Where(a => a.IsWater)
                    .GroupBy(a => a.ResidueKey, StringComparer.OrdinalIgnoreCase).Select(g => new ReceptorHetGroup(g.Key, g.ToList()))) waterList.Items.Add(waterGroup);
                waterList.DisplayMember = nameof(ReceptorHetGroup.DisplayName);
                atomDetails.Text = $"Atoms: {inspectorAtoms.Count}\r\nResidues: {residueEntries.Count}\r\nHETATM groups: {hetList.Items.Count}\r\nWater molecules: {waterList.Items.Count}\r\n\r\nHETATM coordinates are read directly from PDB columns 31–54 (X/Y/Z, Å).";
            }
            catch (Exception ex) { atomDetails.Text = "Inspector error: " + ex.Message; }
        }

        ListBox? activeInspectorList = null;
        void SetActiveInspectorList(ListBox list)
        {
            activeInspectorList = list;
        }

        residueList.Enter += (_, _) => SetActiveInspectorList(residueList);
        hetList.Enter += (_, _) => SetActiveInspectorList(hetList);
        waterList.Enter += (_, _) => SetActiveInspectorList(waterList);
        chainList.Enter += (_, _) => SetActiveInspectorList(chainList);
        residueList.MouseDown += (_, _) => SetActiveInspectorList(residueList);
        hetList.MouseDown += (_, _) => SetActiveInspectorList(hetList);
        waterList.MouseDown += (_, _) => SetActiveInspectorList(waterList);
        chainList.MouseDown += (_, _) => SetActiveInspectorList(chainList);

        void UpdateStructureDetails()
        {
            var selectedResidues = residueList.SelectedItems.Cast<ReceptorAtomRecord>().ToList();
            var selectedHet = hetList.SelectedItems.Cast<ReceptorHetGroup>().ToList();
            var selectedWater = waterList.SelectedItems.Cast<ReceptorHetGroup>().ToList();

            if (selectedResidues.Count > 0 && selectedHet.Count == 0 && selectedWater.Count == 0)
            {
                if (selectedResidues.Count == 1)
                {
                    var key = selectedResidues[0].ResidueKey;
                    var atoms = inspectorAtoms.Where(a => a.RecordType == "ATOM" && !a.IsWater && a.ResidueKey.Equals(key, StringComparison.OrdinalIgnoreCase));
                    atomDetails.Text = ReceptorStructureInspector.DescribeResidue("RESIDUE", atoms);
                }
                else
                {
                    atomDetails.Text = $"{selectedResidues.Count} residues selected.\r\nSelect exactly one residue to inspect its atom coordinates.\r\nNo arbitrary residue or atom is chosen.";
                }
                return;
            }

            if (selectedHet.Count > 0 && selectedResidues.Count == 0 && selectedWater.Count == 0)
            {
                if (selectedHet.Count == 1) atomDetails.Text = ReceptorStructureInspector.DescribeGroup(selectedHet[0]);
                else atomDetails.Text = $"{selectedHet.Count} HETATM groups selected.\r\nSelect exactly one group to inspect its exact atom coordinates.\r\nNo arbitrary atom is chosen.";
                return;
            }

            if (selectedWater.Count > 0 && selectedResidues.Count == 0 && selectedHet.Count == 0)
            {
                if (selectedWater.Count == 1) atomDetails.Text = ReceptorStructureInspector.DescribeResidue("WATER MOLECULE", selectedWater[0].Atoms);
                else atomDetails.Text = $"{selectedWater.Count} water molecules selected.\r\nSelect exactly one water molecule to inspect its coordinates.";
                return;
            }

            int groups = selectedResidues.Count + selectedHet.Count + selectedWater.Count;
            atomDetails.Text = groups == 0
                ? "Select a residue, HETATM group, or water molecule to inspect exact structure information."
                : "Selections span multiple structure categories. Select items from one category to inspect their details.";
        }
        residueList.SelectedIndexChanged += (_, _) => UpdateStructureDetails();
        hetList.SelectedIndexChanged += (_, _) => UpdateStructureDetails();
        waterList.SelectedIndexChanged += (_, _) => UpdateStructureDetails();
        chainList.SelectedIndexChanged += (_, _) =>
        {
            if (chainList.SelectedItems.Count == 0) { UpdateStructureDetails(); return; }
            var chains = chainList.SelectedItems.Cast<object>().Select(x => x?.ToString() ?? "").ToList();
            var normalized = chains.Select(x => x == "(blank)" ? "" : x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var atoms = inspectorAtoms.Where(a => a.RecordType == "ATOM" && !a.IsWater &&
                normalized.Contains(string.IsNullOrWhiteSpace(a.Chain) ? "" : a.Chain.Trim())).ToList();
            int residues = atoms.Select(a => a.ResidueKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            atomDetails.Text = $"CHAIN{(chains.Count > 1 ? "S" : "")}: {string.Join(", ", chains)}\r\nAtoms: {atoms.Count}\r\nResidues: {residues}\r\n\r\nSelect one or more chains, then click DELETE SELECTED to remove them from the working receptor.";
        };
        // MultiSimple makes a normal left mouse click toggle an item, so a user
        // can select one item and click it again to deselect it without Ctrl.
        // SELECT ALL acts on the last inspector category the user clicked.
        selectAllInspector.Click += (_, _) =>
        {
            if (activeInspectorList == null)
            {
                MessageBox.Show(
                    "Select RESIDUES, HETATM GROUPS, WATER MOLECULES, or CHAINS first, then click SELECT ALL.",
                    "Receptor Structure Inspector",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            foreach (var list in new[] { residueList, hetList, waterList, chainList })
            {
                if (!ReferenceEquals(list, activeInspectorList))
                    list.ClearSelected();
            }

            activeInspectorList.BeginUpdate();
            try
            {
                activeInspectorList.ClearSelected();
                for (int i = 0; i < activeInspectorList.Items.Count; i++)
                    activeInspectorList.SetSelected(i, true);
            }
            finally
            {
                activeInspectorList.EndUpdate();
            }

            UpdateStructureDetails();
        };
        prepBrowse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog
            {
                Title = "Select receptor structure",
                Filter = "Protein structures|*.pdb;*.ent;*.cif;*.mmcif|PDB files|*.pdb;*.ent|mmCIF files|*.cif;*.mmcif|All files|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (d.ShowDialog() == DialogResult.OK)
            {
                prepInput.Text = d.FileName;
                RefreshInspector(d.FileName);
            }
        };

        rcsbDownload.Click += async (_, _) =>
        {
            string pdbId = rcsbPdbId.Text.Trim().ToUpperInvariant();
            if (!Regex.IsMatch(pdbId, "^[A-Z0-9]{4}$"))
            {
                MessageBox.Show("Enter a valid 4-character RCSB PDB ID, for example 5KIR or 1HSG.", "ELB DockTool — RCSB PDB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            rcsbDownload.Enabled = false;
            try
            {
                string folder = Path.Combine(AppServices.BaseDir, "workspace", "receptor_preparation", "downloads");
                Directory.CreateDirectory(folder);
                string destination = Path.Combine(folder, pdbId + ".pdb");
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ELB-DockTool/1.2");
                byte[] bytes = await client.GetByteArrayAsync($"https://files.rcsb.org/download/{pdbId}.pdb");
                if (bytes.Length == 0) throw new InvalidOperationException("RCSB returned an empty file.");
                await File.WriteAllBytesAsync(destination, bytes);
                prepInput.Text = destination;
                RefreshInspector(destination);
                MessageBox.Show($"Downloaded {pdbId}.pdb and loaded it into the Structure Inspector.", "ELB DockTool — RCSB PDB Download", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not download PDB ID {pdbId} from RCSB. Check your internet connection and the PDB ID.\n\n{ex.Message}", "ELB DockTool — RCSB PDB Download", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { rcsbDownload.Enabled = true; }
        };

        var repairGroup = new GroupBox { Text = "PDBFIXER STRUCTURE REPAIR", Location = new Point(22, 203), Size = new Size(670, 132), ForeColor = Purple, BackColor = PanelBg, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var enablePdbFixer = new CheckBox { Text = "Enable PDBFixer repair", Checked = true, AutoSize = true, ForeColor = TextColor, Location = new Point(12, 24) };
        var addHeavyAtoms = new CheckBox { Text = "Add missing heavy atoms", Checked = true, AutoSize = true, ForeColor = TextColor, Location = new Point(12, 51) };
        var addMissingResidues = new CheckBox { Text = "Add missing residues", Checked = true, AutoSize = true, ForeColor = TextColor, Location = new Point(222, 51) };
        var replaceNonstandard = new CheckBox { Text = "Replace nonstandard residues", Checked = false, AutoSize = true, ForeColor = TextColor, Location = new Point(400, 51) };
        var addPdbFixerHydrogens = new CheckBox { Text = "Add missing hydrogens", Checked = true, AutoSize = true, ForeColor = TextColor, Location = new Point(12, 78) };
        var pdbFixerPhLabel = new Label { Text = "pH", Location = new Point(260, 80), AutoSize = true, ForeColor = TextColor, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var pdbFixerPh = new NumericUpDown { Location = new Point(290, 76), Size = new Size(78, 26), DecimalPlaces = 1, Minimum = 0, Maximum = 14, Increment = 0.5M, Value = 7.4M, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor };
        var repairHint = new Label { Text = "Heterogens/water are intentionally not controlled here. Delete selected items in the Inspector, or use the cleanup checkboxes below.", Location = new Point(12, 105), Size = new Size(640, 20), ForeColor = Muted, Font = new Font("Segoe UI", 8.5F) };
        repairGroup.Controls.AddRange(new Control[] { enablePdbFixer, addHeavyAtoms, addMissingResidues, replaceNonstandard, addPdbFixerHydrogens, pdbFixerPhLabel, pdbFixerPh, repairHint });

        var removeWater = new CheckBox { Text = "Remove crystallographic water (mandatory)", Checked = true, AutoCheck = false, AutoSize = true, ForeColor = Green, Location = new Point(22, 352), Enabled = true };
        var removeHet = new CheckBox { Text = "Remove non-protein HETATM (mandatory)", Checked = true, AutoCheck = false, AutoSize = true, ForeColor = Green, Location = new Point(300, 352), Enabled = true };
        var addPolarHydrogens = new CheckBox { Text = "Add polar hydrogens (Open Babel)", Checked = false, AutoSize = true, ForeColor = TextColor, Location = new Point(22, 381) };
        var deleteBadResidues = new CheckBox { Text = "Delete bad residues (Meeko)", Checked = false, AutoSize = true, ForeColor = TextColor, Location = new Point(355, 381) };
        var keepHetHint = MutedLabel("Water and non-protein HETATM removal are mandatory receptor-cleanup steps. If PDBFixer adds hydrogens, Open Babel polar-H is skipped to avoid duplicate hydrogen handling.");
        keepHetHint.Location = new Point(22, 407);

        var chargeLabel = MutedLabel("Charge assignment"); chargeLabel.Location = new Point(22, 434);
        var charge = new ComboBox { Location = new Point(22, 456), Size = new Size(300, 31), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, FlatStyle = FlatStyle.Flat };
        charge.Items.Add("Gasteiger — recommended for Vina"); charge.SelectedIndex = 0; charge.Enabled = false;

        var prepButton = MakeButton("PREPARE RECEPTOR", 210, 40, Purple, Color.White); prepButton.Location = new Point(22, 506);
        var prepOpen = MakeButton("OPEN OUTPUT", 135, 40, Panel2, TextColor); prepOpen.Location = new Point(244, 506);
        var prepStatus = new Label { Text = "SETUP NEEDED — Meeko required", ForeColor = Color.Gold, AutoSize = false, Location = new Point(395, 507), Size = new Size(300, 38), TextAlign = ContentAlignment.MiddleLeft };
        receptorPreparationStatusLabel = prepStatus;

        viewInspected.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(inspectorWorkingPath) || !File.Exists(inspectorWorkingPath))
            {
                MessageBox.Show("Load a PDB receptor into the Structure Inspector first.", "Receptor Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var viewer = new ReceptorInspectionWebForm(inspectorWorkingPath);
            viewer.ShowDialog(this);
        };

        saveInspected.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(inspectorWorkingPath) || !File.Exists(inspectorWorkingPath))
            {
                MessageBox.Show("Load a receptor into the Structure Inspector first.", "Receptor Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new SaveFileDialog
            {
                Title = "Save inspected receptor protein",
                Filter = "PDB structure (*.pdb)|*.pdb|All files (*.*)|*.*",
                FileName = SafeName(Path.GetFileNameWithoutExtension(inspectorWorkingPath)) + "_inspected.pdb",
                AddExtension = true,
                DefaultExt = "pdb",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.Copy(inspectorWorkingPath, dlg.FileName, true);
                prepInput.Text = dlg.FileName;
                MessageBox.Show($"Current inspected receptor saved to:\n{dlg.FileName}", "ELB DockTool — Save Protein", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "ELB DockTool — Save Protein", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        deleteSelected.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(inspectorWorkingPath) || !File.Exists(inspectorWorkingPath)) return;
            var residues = residueList.SelectedItems.Cast<ReceptorAtomRecord>().ToList();
            var hetGroups = hetList.SelectedItems.Cast<ReceptorHetGroup>().ToList();
            var hets = hetGroups.SelectMany(g => g.Atoms).ToList();
            var waters = waterList.SelectedItems.Cast<ReceptorHetGroup>().ToList();
            var waterAtoms = waters.SelectMany(g => g.Atoms).ToList();
            var selectedChains = chainList.SelectedItems.Cast<object>()
                .Select(x => x?.ToString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x == "(blank)" ? "" : x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (residues.Count == 0 && hets.Count == 0 && waterAtoms.Count == 0 && selectedChains.Count == 0)
            {
                MessageBox.Show("Select one or more residues, HETATM groups, water molecules, or chains first.", "Receptor Inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                string workDir = Path.Combine(AppServices.BaseDir, "workspace", "receptor_preparation", "inspector");
                Directory.CreateDirectory(workDir);
                string outPath = Path.Combine(workDir, SafeName(Path.GetFileNameWithoutExtension(inspectorOriginalPath ?? inspectorWorkingPath)) + "_edited.pdb");
                ReceptorStructureInspector.DeleteSelectedFromPdb(inspectorWorkingPath, outPath, residues, hets, waterAtoms, selectedChains);
                inspectorWorkingPath = outPath;
                prepInput.Text = outPath;
                RefreshInspector(outPath, resetOriginal: false);
                prepStatus.Text = "Selected structure records deleted from working copy.";
                prepStatus.ForeColor = Green;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Receptor Inspector", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };

        restoreOriginal.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(inspectorOriginalPath) && File.Exists(inspectorOriginalPath))
            {
                prepInput.Text = inspectorOriginalPath;
                RefreshInspector(inspectorOriginalPath);
                prepStatus.Text = "Original receptor restored for preparation.";
                prepStatus.ForeColor = Green;
            }
        };

        prepButton.Click += async (_, _) =>
        {
            prepButton.Enabled = false;
            try
            {
                string? prepared = await PrepareReceptorAsync(prepInput.Text.Trim(), enablePdbFixer.Checked, addHeavyAtoms.Checked, addMissingResidues.Checked, replaceNonstandard.Checked, addPdbFixerHydrogens.Checked, pdbFixerPh.Value, true, true, addPolarHydrogens.Checked, deleteBadResidues.Checked, prepStatus);
                if (!string.IsNullOrWhiteSpace(prepared) && dockingReceptorList != null)
                {
                    if (!dockingReceptorList.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), prepared, StringComparison.OrdinalIgnoreCase))) dockingReceptorList.Items.Add(prepared);
                    dockingReceptorList.SelectedIndex = dockingReceptorList.Items.Count - 1;
                    prepStatus.Text = $"Prepared and added to Receptors: {Path.GetFileName(prepared)}";
                    prepStatus.ForeColor = Green;
                }
            }
            finally { prepButton.Enabled = true; }
        };
        prepOpen.Click += (_, _) => { string folder = ReceptorsFolder(); if (Directory.Exists(folder)) OpenFolder(folder); };

        prep.Controls.AddRange(new Control[] { prepInputLabel, prepInput, prepBrowse, rcsbPdbLabel, rcsbPdbId, rcsbDownload, repairGroup, removeWater, removeHet, addPolarHydrogens, deleteBadResidues, keepHetHint, chargeLabel, charge, prepButton, prepOpen, prepStatus });
        RefreshInspector(prepInput.Text.Trim());

        var topStage = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 690, ColumnCount = 2, RowCount = 1,
            BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 18), Padding = new Padding(0)
        };
        topStage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
        topStage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
        prep.Dock = DockStyle.Fill; prep.Margin = new Padding(0, 0, 8, 0); prep.AutoSize = false;
        inspector.Margin = new Padding(8, 0, 0, 0);
        topStage.Controls.Add(prep, 0, 0);
        topStage.Controls.Add(inspector, 1, 0);

        var receptor = Card("2. Receptors", "Manage prepared receptor PDBQT files for docking. Select, save, load, clear, and configure a grid for each receptor.");
        receptor.Height = 330;
        var recList = StyledListBox(new Point(22, 94), new Size(520, 140)); recList.SelectionMode = SelectionMode.MultiExtended;
        dockingReceptorList = recList;
        var recAdd = MakeButton("SELECT RECEPTOR(S)", 205, 38, Purple, Color.White);
        recAdd.Location = new Point(22, 246);
        recAdd.Click += (_, _) => SelectValidatedFiles(recList, "receptor", "Receptor files|*.pdbqt");
        var recClear = MakeButton("CLEAR", 100, 38, Panel2, TextColor);
        recClear.Location = new Point(235, 246);
        recClear.Click += (_, _) => { recList.Items.Clear(); receptorGrids.Clear(); };
        var recFolder = MakeButton("OPEN FOLDER", 130, 38, Panel2, TextColor);
        recFolder.Location = new Point(345, 246);
        recFolder.Click += (_, _) => OpenFolder(ReceptorsFolder());
        var recSave = MakeButton("SAVE RECEPTOR", 145, 38, Panel2, TextColor);
        recSave.Location = new Point(182, 288);
        recSave.Click += (_, _) => SaveSelectedReceptors(recList);
        var gridEdit = MakeButton("GRID SETTINGS", 145, 38, Panel2, TextColor);
        gridEdit.Location = new Point(335, 288);
        gridEdit.Click += (_, _) => EditReceptorGrids(recList);
        receptor.Controls.Add(recList); receptor.Controls.Add(recAdd); receptor.Controls.Add(recClear); receptor.Controls.Add(recFolder); receptor.Controls.Add(recSave); receptor.Controls.Add(gridEdit);

        var ligand = Card("3. Ligands", "Add ligand structures, convert them to PDBQT, minimize them when needed, and save or reload ligand lists.");
        ligand.Height = 425;
        var ligList = StyledListBox(new Point(22, 94), new Size(520, 140)); ligList.SelectionMode = SelectionMode.MultiExtended;
        dockingLigandList = ligList;
        var ligAdd = MakeButton("SELECT LIGAND(S)", 205, 38, Purple, Color.White);
        ligAdd.Location = new Point(22, 246);
        ligAdd.Click += (_, _) => SelectValidatedFiles(ligList, "ligand", "All files supported by Open Babel|*.*|Molecule files|*.pdbqt;*.sdf;*.mol2;*.pdb;*.mol;*.xyz|All files|*.*");
        var ligClear = MakeButton("CLEAR", 100, 38, Panel2, TextColor);
        ligClear.Location = new Point(235, 246);
        ligClear.Click += (_, _) =>
        {
            // CLEAR removes only the currently selected ligand(s). Keep every
            // unselected ligand in the active list unchanged.
            var selected = ligList.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
            foreach (int index in selected)
                if (index >= 0 && index < ligList.Items.Count)
                    ligList.Items.RemoveAt(index);
        };
        var ligClearAll = MakeButton("CLEAR ALL", 100, 38, Panel2, TextColor);
        ligClearAll.Location = new Point(235, 378);
        ligClearAll.Click += (_, _) => ligList.Items.Clear();
        var ligLoad = MakeButton("LOAD SAVED PDBQT", 120, 38, Panel2, TextColor);
        ligLoad.Location = new Point(345, 246);
        ligLoad.Click += (_, _) => AddStoredFiles(ligList, LigandsFolder(), "ligand");
        var ligAuto = MakeButton("DIRECT CONVERT", 150, 38, Panel2, TextColor);
        ligAuto.Location = new Point(22, 290);
        ligAuto.Click += async (_, _) => await AutoConvertLigands(ligList);
        var ligMin = MakeButton("ENERGY MINIMIZATION", 185, 38, Panel2, TextColor);
        ligMin.Location = new Point(182, 290);
        ligMin.Click += async (_, _) => await EnergyMinimizeLigands(ligList);
        var ligFolder = MakeButton("OPEN CONVERT", 130, 38, Panel2, TextColor);
        ligFolder.Location = new Point(375, 290);
        ligFolder.Click += (_, _) => OpenFolder(ConvertFolder());
        var ligSmiles = MakeButton("SMILES → PDBQT", 170, 38, Purple, Color.White);
        ligSmiles.Location = new Point(22, 334);
        ligSmiles.Click += (_, _) => ShowLigandSmilesPdbqtPanel(ligList);
        var ligSave = MakeButton("SAVE LIGANDS", 140, 38, Panel2, TextColor);
        ligSave.Location = new Point(202, 334);
        ligSave.Click += (_, _) => SaveLigandPanel(ligList);
        var ligOpenFolder = MakeButton("OPEN FOLDER", 140, 38, Panel2, TextColor);
        ligOpenFolder.Location = new Point(350, 334);
        ligOpenFolder.Click += (_, _) => OpenFolder(LigandsFolder());
        ligand.Controls.AddRange(new Control[] { ligList, ligAdd, ligClear, ligClearAll, ligLoad, ligAuto, ligMin, ligFolder, ligSmiles, ligSave, ligOpenFolder });

        var settings = Card("4. Vina Search", "Set the AutoDock Vina search parameters used for each docking run.");
        settings.Height = 285;
        var setGrid = new TableLayoutPanel { Location = new Point(22, 72), Width = 520, Height = 220, ColumnCount = 2, RowCount = 5, AutoSize = false };
        setGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        setGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int r = 0; r < 5; r++) setGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        var exhaust = Num(8);
        var modes = Num(9);
        var energy = Num(3);
        var cpu = Num(10);
        cpu.Minimum = 1; cpu.Maximum = 256; cpu.DecimalPlaces = 0; cpu.Increment = 1;
        var seed = Num(0);
        seed.Minimum = -2147483648; seed.Maximum = 2147483647; seed.DecimalPlaces = 0; seed.Increment = 1;
        AddNum(setGrid, "Exhaustiveness", exhaust, 0, 0);
        AddNum(setGrid, "Num modes", modes, 1, 0);
        AddNum(setGrid, "Energy range", energy, 0, 1);
        AddNum(setGrid, "CPU", cpu, 1, 1);
        AddNum(setGrid, "Seed (optional)", seed, 0, 2);
        settings.Controls.Add(setGrid);

        var monitor = Card("5. Vina Execution Monitor", "Monitor the current Vina run, job progress, and exact program output or errors.");
        monitor.Height = 235;
        vinaMonitorStatus = new Label { AutoSize = true, Text = "● READY — Vina has not been started", ForeColor = Green, Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(22, 78) };
        vinaMonitorJobs = new Label { AutoSize = true, Text = "Jobs: 0 / 0", ForeColor = Muted, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Location = new Point(22, 105) };
        vinaMonitorLog = new TextBox { Location = new Point(22, 132), Size = new Size(1090, 82), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, BackColor = Color.FromArgb(7, 12, 21), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9F), WordWrap = false };
        vinaMonitorLog.Text = "Vina output and errors will appear here while docking runs.";
        monitor.Controls.AddRange(new Control[] { vinaMonitorStatus, vinaMonitorJobs, vinaMonitorLog });

        var action = Card("6. Run", "Run docking for every selected receptor–ligand combination and save the results in the selected output folder.");
        action.Height = 150;
        var outLabel = MutedLabel("Output folder");
        outLabel.Location = new Point(22, 66);
        var outBox = Box(390);
        outBox.Text = AppServices.Resolve(Config.OutputFolder);
        outBox.Location = new Point(22, 91);
        var browse = MakeButton("BROWSE", 90, 32, Panel2, TextColor);
        browse.Location = new Point(420, 91);
        browse.Click += (_, _) => { using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) outBox.Text = d.SelectedPath; };
        var run = MakeButton("START DOCKING", 190, 42, Purple, Color.White);
        run.Location = new Point(525, 86);
        run.Click += async (_, _) => await RunDocking(recList, ligList, exhaust, modes, energy, cpu, seed, outBox.Text);
        action.Controls.AddRange(new Control[] { outLabel, outBox, browse, run });

        row.Controls.Add(topStage, 0, 0);
        row.SetColumnSpan(topStage, 2);
        row.Controls.Add(receptor, 0, 1);
        row.Controls.Add(ligand, 1, 1);
        row.Controls.Add(settings, 0, 2);
        row.SetColumnSpan(settings, 2);
        row.Controls.Add(monitor, 0, 3);
        row.SetColumnSpan(monitor, 2);
        row.Controls.Add(action, 0, 4);
        row.SetColumnSpan(action, 2);
        p.Controls.Add(row);
        return p;
    }


    static string OutputOf((int ExitCode, string Stdout, string Stderr) r) =>
        string.IsNullOrWhiteSpace(r.Stdout) ? r.Stderr.Trim() : r.Stdout.Trim();

    static bool TryParsePythonVersion(string text, out int major, out int minor)
    {
        major = minor = 0;
        var m = Regex.Match(text ?? "", @"Python\s+(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out major) && int.TryParse(m.Groups[2].Value, out minor);
    }

    async Task<(string Python, string Version, string InstallPrefix, bool HasMeeko)> ResolveMeekoPythonAsync()
    {
        var candidates = new List<(string File, string ArgsPrefix, string InstallPrefix)>();
        string configured = (Config.MeekoPythonPath ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
            candidates.Add((configured, "", configured));
        if (!candidates.Any(c => c.File.Equals("python", StringComparison.OrdinalIgnoreCase)))
            candidates.Add(("python", "", "python"));

        // Prefer a modern Python exposed by the Windows py launcher. This avoids
        // accidentally selecting legacy Python 2.7 when `python` is on PATH.
        foreach (int minor in new[] { 14, 13, 12, 11, 10 })
        {
            string spec = $"-3.{minor}";
            var v = await RunProcess("py", $"{spec} --version", AppServices.BaseDir);
            if (v.ExitCode != 0) continue;
            var exe = await RunProcess("py", $"{spec} -c \"import sys; print(sys.executable)\"", AppServices.BaseDir);
            string exePath = OutputOf(exe).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                candidates.Add((exePath, "", $"py {spec}"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string fallbackPython = "";
        string fallbackVersion = "";
        string fallbackInstall = "";

        foreach (var candidate in candidates)
        {
            string key = candidate.File + "|" + candidate.ArgsPrefix;
            if (!seen.Add(key)) continue;
            var version = await RunProcess(candidate.File, candidate.ArgsPrefix + (string.IsNullOrEmpty(candidate.ArgsPrefix) ? "" : " ") + "--version", AppServices.BaseDir);
            string versionText = OutputOf(version);
            if (!TryParsePythonVersion(versionText, out int major, out int minor) || major < 3 || (major == 3 && minor < 10))
                continue;

            if (string.IsNullOrWhiteSpace(fallbackPython))
            {
                fallbackPython = candidate.File;
                fallbackVersion = versionText;
                fallbackInstall = candidate.InstallPrefix;
            }

            var meeko = await RunProcess(candidate.File,
                candidate.ArgsPrefix + (string.IsNullOrEmpty(candidate.ArgsPrefix) ? "" : " ") + "-c \"import meeko; print(getattr(meeko, '__version__', 'installed'))\"",
                AppServices.BaseDir);
            if (meeko.ExitCode == 0)
                return (candidate.File, versionText, candidate.InstallPrefix, true);
        }

        return (fallbackPython, fallbackVersion, fallbackInstall, false);
    }

    string DefaultPdbFixerEnvironment() => Path.Combine(AppServices.ConfigDir, "PDBFixerEnv");
    string PdbFixerEnvironmentPython(string environment) => Path.Combine(environment, "Scripts", "python.exe");

    void UpdatePdbFixerStatusControls()
    {
        if (pdbFixerStatusValue != null)
        {
            pdbFixerStatusValue.Text = pdbFixerStatus.State + " — " + pdbFixerStatus.Detail;
            pdbFixerStatusValue.ForeColor = pdbFixerStatus.IsUsable ? Green : (pdbFixerStatus.State == "ERROR" ? Red : Color.Gold);
        }
        if (pdbFixerPythonValue != null)
            pdbFixerPythonValue.Text = string.IsNullOrWhiteSpace(pdbFixerStatus.Python) ? (pdbFixerStatus.State == "CHECKING" ? "Checking…" : "Not detected") : pdbFixerStatus.Python + "  " + pdbFixerStatus.PythonVersion;
        if (pdbFixerVersionsValue != null)
            pdbFixerVersionsValue.Text = pdbFixerStatus.IsUsable ? $"PDBFixer {pdbFixerStatus.PdbFixerVersion}   •   OpenMM {pdbFixerStatus.OpenMmVersion}" : (pdbFixerStatus.State == "CHECKING" ? "Checking PDBFixer/OpenMM…" : "PDBFixer/OpenMM not detected");
        if (pdbFixerOwnershipValue != null)
            pdbFixerOwnershipValue.Text = pdbFixerStatus.IsManaged ? "ELB-managed isolated environment (safe to remove here)" : "External/global Python environment (protected from deletion)";
    }

    async Task RefreshPdbFixerStatusAsync()
    {
        pdbFixerStatus = new PdbFixerStatus("CHECKING", "Checking PDBFixer/OpenMM installation…", "", "", "", "", false, false);
        UpdatePdbFixerStatusControls();
        try
        {
            string managed = (Config.PdbFixerManagedEnvironment ?? "").Trim();
            string managedPython = string.IsNullOrWhiteSpace(managed) ? "" : PdbFixerEnvironmentPython(managed);
            string python = File.Exists(managedPython) ? managedPython : "";
            bool isManaged = !string.IsNullOrWhiteSpace(python);

            if (string.IsNullOrWhiteSpace(python))
            {
                var candidate = await ResolveMeekoPythonAsync();
                python = candidate.Python;
            }
            if (string.IsNullOrWhiteSpace(python))
            {
                pdbFixerStatus = PdbFixerStatus.NotInstalled("No compatible Python interpreter was detected. Install Python 3.10+ or select it in Settings.");
                UpdatePdbFixerStatusControls();
                return;
            }

            var version = await RunProcess(python, "--version", AppServices.BaseDir);
            var probe = await RunProcess(python,
                "-c \"import sys; import importlib.metadata as m; import pdbfixer, openmm; print(m.version('pdbfixer')); print(openmm.version.version)\"", AppServices.BaseDir);
            if (probe.ExitCode == 0)
            {
                var lines = OutputOf(probe).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                pdbFixerStatus = new PdbFixerStatus("INSTALLED", isManaged ? "Installed in ELB-managed isolated environment." : "Installed in a Python environment not managed by ELB.",
                    python, OutputOf(version), lines.ElementAtOrDefault(0) ?? "installed", lines.ElementAtOrDefault(1) ?? "installed", isManaged, true);
                UpdatePdbFixerStatusControls();
            }
            else
            {
                string detail = OutputOf(probe);
                pdbFixerStatus = new PdbFixerStatus("NOT INSTALLED", string.IsNullOrWhiteSpace(detail) ? "PDBFixer and/or OpenMM could not be imported." : detail,
                    python, OutputOf(version), "", "", isManaged, false);
                UpdatePdbFixerStatusControls();
            }
        }
        catch (Exception ex)
        {
            pdbFixerStatus = new PdbFixerStatus("ERROR", ex.Message, "", "", "", "", false, false);
            UpdatePdbFixerStatusControls();
        }
    }

    void ShowPdbFixerDetails(string title, string text, MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        using var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(850, 570), MinimumSize = new Size(680, 420), BackColor = Bg, ForeColor = TextColor };
        var output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, Font = new Font("Consolas", 9F), Text = text };
        var copy = MakeButton("COPY", 100, 36, Panel2, TextColor); copy.Location = new Point(22, 485); copy.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        copy.Click += (_, _) => { Clipboard.SetText(output.Text); };
        var close = MakeButton("CLOSE", 100, 36, Purple, Color.White); close.Location = new Point(132, 485); close.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; close.Click += (_, _) => form.Close();
        form.Controls.Add(output); form.Controls.Add(copy); form.Controls.Add(close); form.ShowDialog(this);
    }

    string PdbFixerManualInstructions(PdbFixerStatus s) =>
        "PDBFixer / OpenMM manual setup\r\n\r\n" +
        "Detected Python: " + (string.IsNullOrWhiteSpace(s.Python) ? "not found" : s.Python) + "\r\n" +
        "Detected version: " + (string.IsNullOrWhiteSpace(s.PythonVersion) ? "unknown" : s.PythonVersion) + "\r\n\r\n" +
        "Recommended command (use the detected interpreter):\r\n" +
        (string.IsNullOrWhiteSpace(s.Python) ? "py -3.14 -m pip install pdbfixer openmm" : Q(s.Python) + " -m pip install pdbfixer openmm") + "\r\n\r\n" +
        "Verify without requiring pdbfixer.exe on PATH:\r\n" +
        (string.IsNullOrWhiteSpace(s.Python) ? "py -3.14 -c \"import pdbfixer, openmm; print('PDBFixer installed'); print(openmm.version.version)\"" : Q(s.Python) + " -c \"import pdbfixer, openmm; print('PDBFixer installed'); print(openmm.version.version)\"") + "\r\n\r\n" +
        "PATH note: a warning that pdbfixer.exe is not on PATH does not mean installation failed. ELB calls Python directly, so PATH is not required.\r\n\r\n" +
        "Current diagnostic:\r\n" + s.Detail;

    async Task InstallPdbFixerAsync()
    {
        var basePython = await ResolveMeekoPythonAsync();
        if (string.IsNullOrWhiteSpace(basePython.Python))
        {
            await RefreshPdbFixerStatusAsync();
            ShowPdbFixerDetails("ELB DockTool — Python required", PdbFixerManualInstructions(pdbFixerStatus), MessageBoxIcon.Warning);
            return;
        }
        string environment = DefaultPdbFixerEnvironment();
        string envPython = PdbFixerEnvironmentPython(environment);
        var progress = new StringBuilder($"ELB-managed PDBFixer installation\r\nBase Python: {basePython.Python}\r\nEnvironment: {environment}\r\n\r\n");
        try
        {
            if (!File.Exists(envPython))
            {
                progress.AppendLine("Creating isolated virtual environment...");
                var venv = await RunProcess(basePython.Python, $"-m venv {Q(environment)}", AppServices.BaseDir);
                progress.AppendLine(OutputOf(venv));
                if (venv.ExitCode != 0 || !File.Exists(envPython)) throw new InvalidOperationException("Virtual-environment creation failed.\r\n" + OutputOf(venv));
            }
            progress.AppendLine("Installing PDBFixer and OpenMM...");
            var install = await RunProcess(envPython, "-m pip install pdbfixer openmm", AppServices.BaseDir);
            progress.AppendLine(install.Stdout); progress.AppendLine(install.Stderr);
            if (install.ExitCode != 0) throw new InvalidOperationException("Package installation failed.\r\n" + OutputOf(install));
            Config.PdbFixerManagedEnvironment = environment;
            AppServices.SaveConfig(Config);
            await RefreshPdbFixerStatusAsync();
            progress.AppendLine().AppendLine("Result: " + pdbFixerStatus.State + " — " + pdbFixerStatus.Detail);
            ShowPdbFixerDetails("ELB DockTool — PDBFixer install result", progress.ToString());
        }
        catch (Exception ex)
        {
            progress.AppendLine().AppendLine("FAILED: " + ex.Message).AppendLine().Append(PdbFixerManualInstructions(pdbFixerStatus));
            ShowPdbFixerDetails("ELB DockTool — PDBFixer installation failed", progress.ToString(), MessageBoxIcon.Warning);
        }
        await RefreshToolStatusAsync();
        ShowPage("Settings");
    }

    async Task DeleteManagedPdbFixerAsync()
    {
        await RefreshPdbFixerStatusAsync();
        string environment = (Config.PdbFixerManagedEnvironment ?? "").Trim();
        string trustedRoot = Path.GetFullPath(AppServices.ConfigDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool owned = !string.IsNullOrWhiteSpace(environment) && Path.GetFullPath(environment).StartsWith(trustedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(environment);
        if (!owned)
        {
            ShowPdbFixerDetails("ELB DockTool — deletion is protected",
                "ELB did not create and does not own the detected PDBFixer installation. No files were removed.\r\n\r\n" +
                "To remove a global/user installation yourself, use its exact interpreter:\r\n" + (string.IsNullOrWhiteSpace(pdbFixerStatus.Python) ? "py -3.14 -m pip uninstall pdbfixer openmm" : Q(pdbFixerStatus.Python) + " -m pip uninstall pdbfixer openmm") +
                "\r\n\r\nThis does not delete Python itself.", MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show($"Remove only the ELB-managed PDBFixer environment?\n\n{environment}\n\nYour global Python installations will not be changed.", "ELB DockTool — Remove managed PDBFixer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            Directory.Delete(environment, true);
            Config.PdbFixerManagedEnvironment = ""; AppServices.SaveConfig(Config);
            await RefreshToolStatusAsync();
            MessageBox.Show("The ELB-managed PDBFixer environment was removed. No global Python installation was changed.", "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show("Could not remove the ELB-managed environment.\n\n" + ex.Message, "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        ShowPage("Settings");
    }

    async Task<string?> RunPdbFixerRepairAsync(string inputPath, string runDir, bool addHeavyAtoms, bool addResidues, bool replaceNonstandard, bool addHydrogens, decimal ph, Label status)
    {
        await RefreshPdbFixerStatusAsync();
        if (!pdbFixerStatus.IsUsable || string.IsNullOrWhiteSpace(pdbFixerStatus.Python))
        {
            MessageBox.Show("PDBFixer repair was enabled, but PDBFixer/OpenMM is not available. Install or test it in Settings first.\n\n" + pdbFixerStatus.Detail,
                "ELB DockTool — PDBFixer required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        string ext = Path.GetExtension(inputPath);
        if (!(ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ent", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("PDBFixer repair in this workflow currently accepts a PDB/ENT working copy. For mmCIF, use the existing Meeko/ProDy path or export a PDB first.",
                "ELB DockTool — PDBFixer input", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        // PDBFixer/OpenMM is deliberately not given the raw PDB. Some legacy or
        // malformed PDB files contain TER/chain-transition records that OpenMM's
        // PDB parser cannot consume (for example, a TER encountered before an
        // active chain exists). Normalize the temporary copy first. The user's
        // original and inspector-edited files are never modified.
        string normalizedInput = Path.Combine(runDir, SafeName(Path.GetFileNameWithoutExtension(inputPath)) + "_sanitized.pdb");
        string normalizationReport = Path.Combine(runDir, "pdb_sanity_report.txt");
        status.Text = "PDB sanity/normalization: checking and normalizing the working copy...";
        status.ForeColor = Color.Gold;
        try
        {
            NormalizePdbForPdbFixer(inputPath, normalizedInput, normalizationReport);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "PDB sanity/normalization failed. The original and inspector-edited copies were not changed.\n\n" + ex.Message,
                "ELB DockTool — PDB sanity check", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }

        string script = Path.Combine(runDir, "elb_pdbfixer_repair.py");
        string output = Path.Combine(runDir, SafeName(Path.GetFileNameWithoutExtension(inputPath)) + "_pdbfixer_repaired.pdb");
        File.WriteAllText(script, @"import sys
from pdbfixer import PDBFixer
from openmm.app import PDBFile
source, target = sys.argv[1], sys.argv[2]
add_heavy = sys.argv[3] == '1'
add_residues = sys.argv[4] == '1'
replace_nonstandard = sys.argv[5] == '1'
add_hydrogens = sys.argv[6] == '1'
ph = float(sys.argv[7])
fixer = PDBFixer(filename=source)
fixer.findMissingResidues()
if not add_residues:
    fixer.missingResidues = {}
fixer.findNonstandardResidues()
if replace_nonstandard:
    fixer.replaceNonstandardResidues()
if add_heavy:
    fixer.findMissingAtoms()
    fixer.addMissingAtoms()
if add_hydrogens:
    fixer.addMissingHydrogens(ph)
with open(target, 'w') as handle:
    PDBFile.writeFile(fixer.topology, fixer.positions, handle, keepIds=True)
print('PDBFixer repair completed')
");
        string flags = string.Join(" ", new[] { addHeavyAtoms, addResidues, replaceNonstandard, addHydrogens }.Select(x => x ? "1" : "0"));
        status.Text = "PDBFixer: repairing the normalized working copy...";
        status.ForeColor = Color.Gold;
        var result = await RunProcess(pdbFixerStatus.Python, $"{Q(script)} {Q(normalizedInput)} {Q(output)} {flags} {ph.ToString(CultureInfo.InvariantCulture)}", AppServices.BaseDir);
        File.WriteAllText(Path.Combine(runDir, "pdbfixer_repair.log"), $"Original input: {inputPath}\r\nSanitized input: {normalizedInput}\r\nOutput: {output}\r\nAdd heavy atoms: {addHeavyAtoms}\r\nAdd residues: {addResidues}\r\nReplace nonstandard residues: {replaceNonstandard}\r\nAdd hydrogens: {addHydrogens}\r\npH: {ph.ToString(CultureInfo.InvariantCulture)}\r\n\r\n{result.Stdout}\r\n{result.Stderr}");
        if (result.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            string details = (result.Stderr + "\n" + result.Stdout).Trim();
            MessageBox.Show("PDBFixer could not repair the working copy. The original and inspector-edited copies were not changed.\n\n" + details,
                "ELB DockTool — PDBFixer repair", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        return output;
    }

    async Task<string?> PrepareReceptorAsync(string inputPath, bool usePdbFixer, bool addHeavyAtoms, bool addMissingResidues, bool replaceNonstandard, bool addPdbFixerHydrogens, decimal pdbFixerPh, bool removeWater, bool removeNonProteinHet, bool addPolarHydrogens, bool deleteBadResidues, Label status)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            MessageBox.Show("Select a valid PDB or mmCIF receptor structure first.",
                "ELB DockTool — Receptor Preparation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        status.Text = "Detecting a compatible Python and Meeko...";
        status.ForeColor = Color.Gold;

        var resolved = await ResolveMeekoPythonAsync();
        if (string.IsNullOrWhiteSpace(resolved.Python))
        {
            MessageBox.Show(
                "ELB could not find a compatible Python 3.10+ interpreter.\n\n" +
                "STEP 1 — VERIFY PYTHON\n" +
                "python --version\n" +
                "py --version\n" +
                "py -0p\n\n" +
                "STEP 2 — INSTALL PYTHON\n" +
                "Install Python 3.14 (64-bit), then open a NEW Command Prompt.\n\n" +
                "STEP 3 — VERIFY PYTHON 3.14\n" +
                "py -3.14 --version\n\n" +
                "STEP 4 — UPGRADE PIP\n" +
                "py -3.14 -m pip install --upgrade pip\n" +
                "Test: py -3.14 -m pip --version\n\n" +
                "STEP 5 — INSTALL REQUIRED PACKAGES\n" +
                "py -3.14 -m pip install numpy\n" +
                "Test: py -3.14 -c \"import numpy; print(numpy.__version__)\"\n\n" +
                "py -3.14 -m pip install scipy\n" +
                "Test: py -3.14 -c \"import scipy; print(scipy.__version__)\"\n\n" +
                "py -3.14 -m pip install rdkit\n" +
                "Test: py -3.14 -c \"import rdkit; print(rdkit.__version__)\"\n\n" +
                "py -3.14 -m pip install gemmi\n" +
                "Test: py -3.14 -c \"import gemmi; print(gemmi.__version__)\"\n\n" +
                "py -3.14 -m pip install tqdm\n" +
                "Test: py -3.14 -c \"import tqdm; print(tqdm.__version__)\"\n\n" +
                "py -3.14 -m pip install prody\n" +
                "Test: py -3.14 -c \"import prody; print(prody.__version__)\"\n\n" +
                "py -3.14 -m pip install meeko\n" +
                "Test: py -3.14 -c \"import meeko; print(meeko.__version__)\"\n\n" +
                "STEP 6 — VERIFY MEEKO RECEPTOR CLI\n" +
                "py -3.14 -m meeko.cli.mk_prepare_receptor --help\n\n" +
                "The Meeko help page should appear. Then restart ELB DockTool and click VERIFY PYTHON / MEEKO.",
                "ELB DockTool — Python / Meeko Setup Required",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            status.Text = "Compatible Python not found";
            status.ForeColor = Red;
            return null;
        }

        string python = resolved.Python;
        Config.MeekoPythonPath = python;
        AppServices.SaveConfig(Config);

        if (!resolved.HasMeeko)
        {
            string installTarget = string.IsNullOrWhiteSpace(resolved.InstallPrefix) ? "py -3.14" : resolved.InstallPrefix;
            string target = installTarget.StartsWith("py ", StringComparison.OrdinalIgnoreCase) || installTarget.Equals("python", StringComparison.OrdinalIgnoreCase)
                ? installTarget
                : Q(installTarget);
            MessageBox.Show(
                "A compatible Python was found, but Meeko is not installed in that Python environment.\n\n" +
                $"Python: {python}\n" +
                $"Version: {resolved.Version}\n\n" +
                "Use the following procedure in a NEW Command Prompt. The commands below use the detected Python environment.\n\n" +
                "STEP 1 — VERIFY PYTHON\n" +
                $"{target} --version\n\n" +
                "STEP 2 — UPGRADE PIP\n" +
                $"{target} -m pip install --upgrade pip\n" +
                $"Test: {target} -m pip --version\n\n" +
                "STEP 3 — INSTALL REQUIRED PACKAGES\n" +
                $"{target} -m pip install numpy\n" +
                $"Test: {target} -c \"import numpy; print(numpy.__version__)\"\n\n" +
                $"{target} -m pip install scipy\n" +
                $"Test: {target} -c \"import scipy; print(scipy.__version__)\"\n\n" +
                $"{target} -m pip install rdkit\n" +
                $"Test: {target} -c \"import rdkit; print(rdkit.__version__)\"\n\n" +
                $"{target} -m pip install gemmi\n" +
                $"Test: {target} -c \"import gemmi; print(gemmi.__version__)\"\n\n" +
                $"{target} -m pip install tqdm\n" +
                $"Test: {target} -c \"import tqdm; print(tqdm.__version__)\"\n\n" +
                $"{target} -m pip install prody\n" +
                $"Test: {target} -c \"import prody; print(prody.__version__)\"\n\n" +
                $"{target} -m pip install meeko\n" +
                $"Test: {target} -c \"import meeko; print(meeko.__version__)\"\n\n" +
                "STEP 4 — VERIFY MEEKO RECEPTOR CLI\n" +
                $"{target} -m meeko.cli.mk_prepare_receptor --help\n\n" +
                "The Meeko help page should appear. Then click VERIFY PYTHON / MEEKO again.",
                "ELB DockTool — Meeko Setup Required",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            status.Text = "Meeko not installed — follow setup instructions";
            status.ForeColor = Red;
            return null;
        }

        string workDir = Path.Combine(AppServices.BaseDir, "workspace", "receptor_preparation");
        Directory.CreateDirectory(workDir);

        string baseName = SafeName(Path.GetFileNameWithoutExtension(inputPath));
        string runDir = Path.Combine(workDir, baseName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(runDir);

        string meekoInput = inputPath;
        string ext = Path.GetExtension(inputPath);

        try
        {
            // The Inspector may already have replaced inputPath with a manually
            // edited working copy. PDBFixer therefore always receives exactly
            // what the user chose to retain, never the untouched original.
            if (usePdbFixer)
            {
                string? repaired = await RunPdbFixerRepairAsync(inputPath, runDir, addHeavyAtoms, addMissingResidues, replaceNonstandard, addPdbFixerHydrogens, pdbFixerPh, status);
                if (string.IsNullOrWhiteSpace(repaired))
                {
                    status.Text = "PDBFixer repair failed or was not available";
                    status.ForeColor = Red;
                    return null;
                }
                meekoInput = repaired;
                ext = ".pdb";
            }

            // CLEANUP is intentionally after PDBFixer and before Meeko. This
            // removes problematic protein CONECT records that can make Meeko
            // interpret ordinary peptide bonds as excess inter-residue bonds.
            // The original input is never modified. HETATM-only CONECT records are
            // retained when possible. For mmCIF, ELB leaves syntax/parsing to Meeko.
            if (ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ent", StringComparison.OrdinalIgnoreCase))
            {
                string cleanupSource = meekoInput;
                meekoInput = Path.Combine(runDir, baseName + "_cleaned.pdb");
                CleanPdbForReceptorPreparation(cleanupSource, meekoInput, removeWater, removeNonProteinHet);
            }
            else
            {
                status.Text = "mmCIF selected — structure parsing delegated to Meeko/ProDy";
                status.ForeColor = Color.Gold;
            }

            // Optional explicit Open Babel polar-H preparation occurs only after
            // PDBFixer + cleanup and immediately before Meeko. The default is OFF.
            // The original input is
            // never modified. This uses Open Babel's documented --addpolarh option.
            if (addPolarHydrogens && !(usePdbFixer && addPdbFixerHydrogens) &&
                (ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".ent", StringComparison.OrdinalIgnoreCase)))
            {
                if (!AppServices.Exists(Config.OpenBabelPath))
                {
                    var detectedBabel = AppServices.DetectExecutable("obabel.exe");
                    if (!string.IsNullOrWhiteSpace(detectedBabel))
                    {
                        Config.OpenBabelPath = detectedBabel;
                        try { AppServices.SaveConfig(Config); } catch { }
                    }
                }

                if (!AppServices.Exists(Config.OpenBabelPath))
                {
                    MessageBox.Show(
                        "Add polar hydrogens is enabled, but Open Babel (obabel.exe) was not found. " +
                        "Install Open Babel or set its path in Settings, then try again.",
                        "ELB DockTool — Open Babel Required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    status.Text = "Setup needed — Open Babel required for polar H";
                    status.ForeColor = Red;
                    return null;
                }

                string polarPdb = Path.Combine(runDir, baseName + "_polarH.pdb");
                status.Text = "Open Babel: adding polar hydrogens to preparation copy...";
                status.ForeColor = Color.Gold;
                var polarResult = await RunProcess(
                    AppServices.Resolve(Config.OpenBabelPath),
                    $"{Q(meekoInput)} -O {Q(polarPdb)} --addpolarh",
                    AppServices.BaseDir);

                if (polarResult.ExitCode != 0 || !File.Exists(polarPdb) || new FileInfo(polarPdb).Length == 0)
                {
                    string details = string.IsNullOrWhiteSpace(polarResult.Stderr) ? polarResult.Stdout : polarResult.Stderr;
                    if (details.Length > 4000) details = details[^4000..];
                    MessageBox.Show(
                        "Open Babel could not add polar hydrogens to the receptor preparation copy.\n\n" +
                        details.Trim(),
                        "ELB DockTool — Polar Hydrogen Preparation",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    status.Text = "Polar hydrogen preparation failed";
                    status.ForeColor = Red;
                    return null;
                }

                string normalizedPolarPdb = Path.Combine(runDir, baseName + "_polarH_normalized.pdb");
                NormalizePdbResidueBlocks(polarPdb, normalizedPolarPdb);
                meekoInput = normalizedPolarPdb;
            }
            else if (addPolarHydrogens && usePdbFixer && addPdbFixerHydrogens)
            {
                status.Text = "PDBFixer added hydrogens; skipping Open Babel polar-H to avoid duplicate hydrogen handling.";
                status.ForeColor = Color.Gold;
            }

            string outputBase = Path.Combine(ReceptorsFolder(), baseName + "_prepared");
            string tempBase = Path.Combine(runDir, baseName + "_prepared");

            status.Text = deleteBadResidues
                ? "Meeko: parameterizing receptor, computing Gasteiger charges, and allowing bad-residue deletion..."
                : "Meeko: parameterizing receptor and computing Gasteiger charges...";
            status.ForeColor = Color.Gold;

            string inputExt = Path.GetExtension(meekoInput);
            bool isMmcif = inputExt.Equals(".cif", StringComparison.OrdinalIgnoreCase) ||
                           inputExt.Equals(".mmcif", StringComparison.OrdinalIgnoreCase);
            string readFlag = isMmcif ? "--read_with_prody " : "--read_pdb ";

            if (isMmcif)
            {
                var prodyCheck = await RunProcess(python, "-c \"import prody; print(getattr(prody, '__version__', 'installed'))\"", AppServices.BaseDir);
                if (prodyCheck.ExitCode != 0)
                {
                    string prodyTarget = string.IsNullOrWhiteSpace(Config.MeekoPythonPath) ? python : Config.MeekoPythonPath.Trim();
                    if (!prodyTarget.StartsWith("py ", StringComparison.OrdinalIgnoreCase) && !prodyTarget.Equals("python", StringComparison.OrdinalIgnoreCase))
                        prodyTarget = Q(prodyTarget);
                    MessageBox.Show(
                        "mmCIF preparation requires ProDy in the same Python environment used by ELB.\n\n" +
                        $"Python: {python}\n\n" +
                        "INSTALL PRODY\n" +
                        $"{prodyTarget} -m pip install prody\n\n" +
                        "TEST PRODY\n" +
                        $"{prodyTarget} -c \"import prody; print(prody.__version__)\"\n\n" +
                        "Then return to ELB and try receptor preparation again.",
                        "ELB DockTool — ProDy Required for mmCIF",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    status.Text = "ProDy not available for mmCIF";
                    status.ForeColor = Red;
                    return null;
                }
            }

            string args =
                "-m meeko.cli.mk_prepare_receptor " +
                readFlag + Q(meekoInput) +
                " --output_basename " + Q(tempBase) +
                " --write_pdbqt " + Q(tempBase + ".pdbqt") +
                " --write_json " + Q(tempBase + ".json") +
                " --write_pdb " + Q(tempBase + ".pdb") +
                " --compute_charges --charge_model gasteiger" +
                (deleteBadResidues ? " --delete_bad_res" : "");

            var result = await RunProcess(python, args, AppServices.BaseDir);

            string generated = tempBase + ".pdbqt";
            if (result.ExitCode != 0 || !File.Exists(generated))
            {
                string details = (result.Stderr + "\n" + result.Stdout).Trim();
                if (details.Length > 7000) details = details[^7000..];
                File.WriteAllText(Path.Combine(runDir, "preparation.log"), details);

                MessageBox.Show(
                    "Receptor preparation failed. ELB did not add the receptor to the docking list.\n\n" +
                    details,
                    "ELB DockTool — Receptor Preparation",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                status.Text = "Preparation failed";
                status.ForeColor = Red;
                return null;
            }

            Directory.CreateDirectory(ReceptorsFolder());
            string finalPdbqt = outputBase + ".pdbqt";
            string finalJson = outputBase + ".json";
            string finalPdb = outputBase + ".pdb";
            string finalLog = outputBase + "_preparation.log";

            int polarHydrogenCount;

            File.Copy(generated, finalPdbqt, true);
            if (File.Exists(tempBase + ".json")) File.Copy(tempBase + ".json", finalJson, true);
            if (File.Exists(tempBase + ".pdb")) File.Copy(tempBase + ".pdb", finalPdb, true);

            polarHydrogenCount = CountPdbqtPolarHydrogens(finalPdbqt);

            string logText = "ELB DockTool receptor preparation\n" +
                             $"Input: {inputPath}\n" +
                             $"PDBFixer: enabled={usePdbFixer}; heavy atoms={addHeavyAtoms}; residues={addMissingResidues}; replace nonstandard={replaceNonstandard}; hydrogens={addPdbFixerHydrogens}; pH={pdbFixerPh.ToString(CultureInfo.InvariantCulture)}\n" +
                             $"Cleanup: remove water={removeWater}; remove non-protein HETATM={removeNonProteinHet}\n" +
                             "Charge model: Gasteiger\n" +
                             $"Polar hydrogens (PDBQT HD atom type): {polarHydrogenCount}\n" +
                             "Meeko receptor parameterization/atom typing completed before PDBQT validation.\n\n" +
                             result.Stdout + "\n" + result.Stderr;
            File.WriteAllText(finalLog, logText);

            var validation = ValidateMoleculeFile(finalPdbqt, "receptor");
            if (!validation.Ok)
            {
                try { File.Delete(finalPdbqt); } catch { }
                MessageBox.Show(
                    "Meeko completed, but ELB's receptor PDBQT validation failed:\n\n" + validation.Message +
                    "\n\nThe file was not added to the docking list.",
                    "ELB DockTool — Receptor Validation",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                status.Text = "Validation failed";
                status.ForeColor = Red;
                return null;
            }

            // Keep the preparation workspace for reproducibility/debugging. The final
            // receptor files are published into the same receptors folder used by the
            // existing SAVE/LOAD/GRID workflow.
            status.Text = "Receptor prepared successfully";
            status.ForeColor = Green;
            return finalPdbqt;
        }
        catch (Exception ex)
        {
            status.Text = "Preparation failed";
            status.ForeColor = Red;
            MessageBox.Show(ex.Message, "ELB DockTool — Receptor Preparation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    static int CountPdbqtPolarHydrogens(string pdbqtPath)
    {
        if (!File.Exists(pdbqtPath)) return 0;
        int count = 0;
        foreach (string line in File.ReadLines(pdbqtPath))
        {
            if (!line.StartsWith("ATOM", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("HETATM", StringComparison.OrdinalIgnoreCase)) continue;
            // AutoDock PDBQT uses the atom-type field at the end; HD denotes a polar hydrogen.
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0 && string.Equals(fields[^1], "HD", StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    static void NormalizePdbForPdbFixer(string input, string output, string reportPath)
    {
        if (!File.Exists(input))
            throw new FileNotFoundException("Input PDB was not found.", input);

        string[] lines = File.ReadAllLines(input);
        int atomCount = 0;
        int terInput = 0;
        int terIgnored = 0;
        int terKept = 0;
        int terInserted = 0;
        int malformedAtomLines = 0;
        int blankLines = 0;
        int chains = 0;
        string? previousChain = null;
        bool haveAtom = false;
        bool lastWasTer = false;
        var seenChains = new HashSet<string>(StringComparer.Ordinal);
        var atomBlock = new List<string>();
        var headerLines = new List<string>();
        var tailLines = new List<string>();
        bool inAtomBlock = false;

        // Normalize only the parser-sensitive structure syntax. Valid TER records
        // are retained when they occur after a real coordinate record; stray,
        // leading, duplicate, or otherwise empty-chain TER records are removed.
        // If the chain changes without a TER, ELB inserts one between the two
        // coordinate blocks. This prevents OpenMM/PDBFixer from seeing a TER while
        // no active chain exists, without destroying legitimate chain breaks.
        foreach (string raw in lines)
        {
            string record = raw.Length >= 6 ? raw.Substring(0, 6).Trim().ToUpperInvariant() : "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                blankLines++;
                continue;
            }

            if (record == "TER")
            {
                terInput++;
                if (haveAtom && !lastWasTer)
                {
                    atomBlock.Add("TER");
                    terKept++;
                    lastWasTer = true;
                }
                else
                {
                    terIgnored++;
                }
                continue;
            }

            if (record == "ATOM" || record == "HETATM")
            {
                if (raw.Length < 54)
                {
                    malformedAtomLines++;
                    throw new InvalidDataException($"An {record} record is shorter than the required PDB coordinate fields (54 characters). Check the source PDB formatting near: {raw.Trim()}");
                }

                string chain = raw.Length >= 22 ? raw.Substring(21, 1) : "";
                if (seenChains.Add(chain)) chains++;

                if (haveAtom && !lastWasTer && previousChain != null && !string.Equals(previousChain, chain, StringComparison.Ordinal))
                {
                    atomBlock.Add("TER");
                    terInserted++;
                }

                atomBlock.Add(raw.TrimEnd());
                atomCount++;
                haveAtom = true;
                inAtomBlock = true;
                previousChain = chain;
                lastWasTer = false;
                continue;
            }

            if (record == "END")
                continue;

            if (!inAtomBlock)
                headerLines.Add(raw.TrimEnd());
            else
                tailLines.Add(raw.TrimEnd());
        }

        if (atomCount == 0)
            throw new InvalidDataException("No ATOM/HETATM coordinate records were found in the selected PDB.");

        using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
        {
            foreach (string line in headerLines) writer.WriteLine(line);
            foreach (string line in atomBlock) writer.WriteLine(line);
            foreach (string line in tailLines)
            {
                string record = line.Length >= 6 ? line.Substring(0, 6).Trim().ToUpperInvariant() : "";
                if (record == "ENDMDL" || record == "CONECT" || record == "MASTER")
                    writer.WriteLine(line);
            }
            writer.WriteLine("END");
        }

        string pdbId = SafeName(Path.GetFileNameWithoutExtension(input)).ToUpperInvariant();
        string obsoleteNote = pdbId == "1FB4"
            ? "WARNING: 1FB4 is an obsolete PDB entry and has been superseded by 2FB4. Use the current entry when possible.\r\n"
            : "";

        File.WriteAllText(reportPath,
            "ELB DockTool PDB sanity/normalization report\r\n" +
            "===========================================\r\n" +
            $"Input: {input}\r\n" +
            $"Output: {output}\r\n" +
            $"ATOM/HETATM records: {atomCount}\r\n" +
            $"Input TER records: {terInput}\r\n" +
            $"Valid TER records retained: {terKept}\r\n" +
            $"Stray/duplicate TER records ignored: {terIgnored}\r\n" +
            $"TER records inserted at chain transitions: {terInserted}\r\n" +
            $"Detected chains: {chains}\r\n" +
            $"Blank lines ignored: {blankLines}\r\n" +
            $"Short/malformed atom records: {malformedAtomLines}\r\n" +
            obsoleteNote +
            "Normalization actions:\r\n" +
            "- Removed blank lines.\r\n" +
            "- Retained TER only when it followed a real coordinate block and was not a duplicate.\r\n" +
            "- Inserted TER when the chain identifier changed without a separator.\r\n" +
            "- Preserved ATOM/HETATM coordinate records verbatim.\r\n" +
            "- Preserved header metadata where possible.\r\n" +
            "- Preserved CONECT records for the later cleanup stage.\r\n" +
            "- Wrote a clean terminal END record.\r\n");
    }

    static void NormalizePdbResidueBlocks(string input, string output)
    {
        // Open Babel can append newly added hydrogens away from their parent
        // residues. Meeko expects atoms belonging to a residue to be contiguous.
        // Reorder only the temporary PDB produced for the polar-H path; the
        // user's original receptor is never modified.
        var lines = File.ReadAllLines(input);
        var atomLines = new List<string>();
        var otherLines = new List<string>();
        var residueOrder = new List<string>();
        var residueAtoms = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string line in lines)
        {
            string record = line.Length >= 6 ? line[..6].Trim() : string.Empty;
            if (record.Equals("ATOM", StringComparison.OrdinalIgnoreCase) ||
                record.Equals("HETATM", StringComparison.OrdinalIgnoreCase))
            {
                atomLines.Add(line);

                // PDB fixed-width residue identity: columns 18-27
                // (residue name, chain, residue number, insertion code).
                string key = line.Length >= 27 ? line.Substring(17, 10) : line;
                if (!residueAtoms.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    residueAtoms[key] = list;
                    residueOrder.Add(key);
                }
                list.Add(line);
            }
            else
            {
                otherLines.Add(line);
            }
        }

        if (atomLines.Count == 0)
        {
            File.Copy(input, output, true);
            return;
        }

        using var writer = new StreamWriter(output, false);
        // Keep header/title information before the atom block where possible.
        foreach (string line in otherLines)
        {
            string record = line.Length >= 6 ? line[..6].Trim() : string.Empty;
            if (record.Equals("HEADER", StringComparison.OrdinalIgnoreCase) ||
                record.Equals("TITLE", StringComparison.OrdinalIgnoreCase) ||
                record.Equals("REMARK", StringComparison.OrdinalIgnoreCase) ||
                record.Equals("CRYST1", StringComparison.OrdinalIgnoreCase) ||
                record.Equals("MODEL", StringComparison.OrdinalIgnoreCase))
            {
                writer.WriteLine(line);
            }
        }

        string? previousChain = null;
        foreach (string key in residueOrder)
        {
            string chain = key.Length >= 5 ? key.Substring(4, 1) : string.Empty;
            if (previousChain != null && !string.Equals(previousChain, chain, StringComparison.Ordinal))
            {
                writer.WriteLine("TER");
            }

            foreach (string atom in residueAtoms[key])
                writer.WriteLine(atom);

            previousChain = chain;
        }

        // Preserve connectivity and final records after the normalized atom block.
        foreach (string line in otherLines)
        {
            string record = line.Length >= 6 ? line[..6].Trim() : string.Empty;
            if (record.Equals("CONECT", StringComparison.OrdinalIgnoreCase) ||
                record.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                writer.WriteLine(line);
            }
        }
    }

    static void CleanPdbForReceptorPreparation(string input, string output, bool removeWater, bool removeNonProteinHet)
    {
        string[] waterNames = { "HOH", "WAT", "DOD", "H2O" };
        var lines = File.ReadAllLines(input);
        var atomRecordBySerial = new Dictionary<int, string>();

        // Build a serial-number map first so CONECT records can be filtered
        // without guessing from atom names.
        foreach (string raw in lines)
        {
            string record = raw.Length >= 6 ? raw.Substring(0, 6).Trim().ToUpperInvariant() : "";
            if ((record == "ATOM" || record == "HETATM") && raw.Length >= 11 &&
                int.TryParse(raw.Substring(6, 5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int serial))
                atomRecordBySerial[serial] = record;
        }

        using var writer = new StreamWriter(output, false, new UTF8Encoding(false));
        foreach (string raw in lines)
        {
            string record = raw.Length >= 6 ? raw.Substring(0, 6).Trim().ToUpperInvariant() : "";

            if (record == "CONECT")
            {
                var serials = new List<int>();
                for (int pos = 6; pos + 5 <= raw.Length; pos += 5)
                    if (int.TryParse(raw.Substring(pos, 5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int serial))
                        serials.Add(serial);

                // Protein ATOM connectivity is intentionally omitted. Meeko's
                // receptor perception should infer the standard residue bonds;
                // retaining stale/over-specified protein CONECT records can cause
                // errors such as "excess inter-residue bonds". Preserve CONECT
                // records that involve only HETATM atoms.
                if (serials.Any(x => atomRecordBySerial.TryGetValue(x, out string? type) && type == "ATOM"))
                    continue;

                writer.WriteLine(raw);
                continue;
            }

            bool atom = record == "ATOM";
            bool het = record == "HETATM";
            if (!atom && !het)
            {
                writer.WriteLine(raw);
                continue;
            }

            string residue = raw.Length >= 20 ? raw.Substring(17, 3).Trim().ToUpperInvariant() : "";
            if (removeWater && waterNames.Contains(residue, StringComparer.OrdinalIgnoreCase))
                continue;

            if (het && removeNonProteinHet)
                continue;

            writer.WriteLine(raw);
        }
    }

    void SaveSelectedReceptors(ListBox recList)
    {
        if (recList.Items.Count == 0)
        {
            MessageBox.Show("Select at least one receptor first.", "Save Receptor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int saved = 0;
        var errors = new List<string>();
        foreach (string source in recList.SelectedItems.Cast<string>().ToList())
        {
            try
            {
                var check = ValidateMoleculeFile(source, "receptor");
                if (!check.Ok)
                {
                    errors.Add($"{Path.GetFileName(source)} — {check.Message}");
                    continue;
                }
                string savedPath = CopyIntoProjectFolder(source, ReceptorsFolder());
                if (!recList.Items.Contains(savedPath)) recList.Items.Add(savedPath);
                saved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(source)} — {ex.Message}");
            }
        }

        if (saved == 0 && recList.SelectedItems.Count == 0)
        {
            MessageBox.Show("Select the receptor file(s) in the list, then click SAVE RECEPTOR.", "Save Receptor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string message = $"Saved {saved} receptor file(s) in:\n{ReceptorsFolder()}";
        if (errors.Count > 0) message += "\n\nProblems:\n" + string.Join("\n", errors.Take(8));
        MessageBox.Show(message, "ELB DockTool — Save Receptor", MessageBoxButtons.OK, errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    void SyncReceptorGrids(ListBox recList)
    {
        foreach (string receptor in recList.Items)
            if (!receptorGrids.ContainsKey(receptor)) receptorGrids[receptor] = new GridSpec();
        var active = new HashSet<string>(recList.Items.Cast<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var key in receptorGrids.Keys.Where(k => !active.Contains(k)).ToList()) receptorGrids.Remove(key);
    }

    void EditReceptorGrids(ListBox recList)
    {
        if (recList.Items.Count == 0) { MessageBox.Show("Select at least one receptor first.", "Grid Settings", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        SyncReceptorGrids(recList);
        using var f = new Form { Text = "Per-Receptor Docking Grid", StartPosition = FormStartPosition.CenterParent, Size = new Size(980, 560), MinimumSize = new Size(900, 500), BackColor = Bg, ForeColor = TextColor, Font = new Font("Segoe UI", 9.5F) };
        var info = new Label { Text = "Set a separate active-site center and box size for each selected receptor (Å). Click the eye to inspect the protein and grid in 3D.", AutoSize = true, ForeColor = Muted, Location = new Point(22, 18) }; f.Controls.Add(info);
        var grid = new DataGridView { Location = new Point(22, 52), Size = new Size(914, 360), BackgroundColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, GridColor = Border, BorderStyle = BorderStyle.FixedSingle, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, MultiSelect = false };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, SelectionBackColor = Color.FromArgb(58, 30, 100), SelectionForeColor = Color.White, Font = new Font("Segoe UI", 9.5F), Alignment = DataGridViewContentAlignment.MiddleLeft };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(12, 19, 31), ForeColor = TextColor, SelectionBackColor = Color.FromArgb(58, 30, 100), SelectionForeColor = Color.White };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(25, 35, 52), ForeColor = TextColor, SelectionBackColor = Color.FromArgb(25, 35, 52), SelectionForeColor = TextColor, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };

        grid.Columns.Add("Receptor", "Receptor");
        grid.Columns.Add("Center X", "Center X");
        grid.Columns.Add("Center Y", "Center Y");
        grid.Columns.Add("Center Z", "Center Z");
        grid.Columns.Add("Size X", "Size X");
        grid.Columns.Add("Size Y", "Size Y");
        grid.Columns.Add("Size Z", "Size Z");
        var viewColumn = new DataGridViewButtonColumn { Name = "View3D", HeaderText = "3D", Text = "👁", UseColumnTextForButtonValue = true, Width = 58, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, FlatStyle = FlatStyle.Flat };
        grid.Columns.Add(viewColumn);

        foreach (string receptor in recList.Items)
        {
            if (!receptorGrids.TryGetValue(receptor, out var g))
            {
                g = new GridSpec();
                receptorGrids[receptor] = g;
            }
            grid.Rows.Add(Path.GetFileName(receptor), g.CenterX, g.CenterY, g.CenterZ, g.SizeX, g.SizeY, g.SizeZ);
        }

        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != grid.Columns["View3D"].Index) return;
            try
            {
                string receptor = recList.Items[e.RowIndex]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(receptor) || !File.Exists(receptor))
                {
                    MessageBox.Show("The selected receptor file could not be found. Reload/save the receptor and try again.", "3D Grid Viewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var g = ReadGridFromRow(grid.Rows[e.RowIndex]);
                using var viewer = new Grid3DViewerForm(receptor, g, Bg, PanelBg, Panel2, Border, Purple, TextColor, Muted);
                viewer.ShowDialog(f);

                // The 3D viewer edits this same GridSpec instance. Commit the values
                // back to the selected receptor row as soon as the viewer closes so
                // Apply/Fit, auto-grid, residue centering, and mouse resizing are not lost.
                receptorGrids[receptor] = g;
                grid.Rows[e.RowIndex].Cells[1].Value = g.CenterX.ToString("0.00", CultureInfo.InvariantCulture);
                grid.Rows[e.RowIndex].Cells[2].Value = g.CenterY.ToString("0.00", CultureInfo.InvariantCulture);
                grid.Rows[e.RowIndex].Cells[3].Value = g.CenterZ.ToString("0.00", CultureInfo.InvariantCulture);
                grid.Rows[e.RowIndex].Cells[4].Value = g.SizeX.ToString("0.00", CultureInfo.InvariantCulture);
                grid.Rows[e.RowIndex].Cells[5].Value = g.SizeY.ToString("0.00", CultureInfo.InvariantCulture);
                grid.Rows[e.RowIndex].Cells[6].Value = g.SizeZ.ToString("0.00", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to open the 3D view. " + ex.Message, "3D Grid Viewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        f.Controls.Add(grid);
        var save = MakeButton("SAVE GRID SETTINGS", 190, 42, Purple, Color.White); save.Location = new Point(22, 430); save.Click += (_, _) =>
        {
            try
            {
                int rowCount = Math.Min(recList.Items.Count, grid.Rows.Count);
                for (int i = 0; i < rowCount; i++)
                {
                    string receptor = recList.Items[i]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(receptor)) continue;
                    receptorGrids[receptor] = ReadGridFromRow(grid.Rows[i]);
                }
                f.DialogResult = DialogResult.OK;
                f.Close();
            }
            catch (Exception ex) { MessageBox.Show("Invalid grid value. " + ex.Message, "Grid Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        var cancel = MakeButton("CANCEL", 100, 42, Panel2, TextColor); cancel.Location = new Point(224, 430); cancel.Click += (_, _) => f.Close(); f.Controls.Add(save); f.Controls.Add(cancel);
        f.ShowDialog(this);
    }

    GridSpec ReadGridFromRow(DataGridViewRow row)
    {
        var g = new GridSpec
        {
            CenterX = decimal.Parse(row.Cells[1].Value?.ToString() ?? "0", CultureInfo.InvariantCulture),
            CenterY = decimal.Parse(row.Cells[2].Value?.ToString() ?? "0", CultureInfo.InvariantCulture),
            CenterZ = decimal.Parse(row.Cells[3].Value?.ToString() ?? "0", CultureInfo.InvariantCulture),
            SizeX = decimal.Parse(row.Cells[4].Value?.ToString() ?? "20", CultureInfo.InvariantCulture),
            SizeY = decimal.Parse(row.Cells[5].Value?.ToString() ?? "20", CultureInfo.InvariantCulture),
            SizeZ = decimal.Parse(row.Cells[6].Value?.ToString() ?? "20", CultureInfo.InvariantCulture)
        };
        if (g.SizeX <= 0 || g.SizeY <= 0 || g.SizeZ <= 0) throw new Exception("Grid sizes must be greater than zero.");
        return g;
    }

    NumericUpDown Num(decimal v) => new() { Value = v, Minimum = -10000, Maximum = 10000, DecimalPlaces = 2, Increment = 0.5M, Width = 120, Height = 30, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10F) };
    void AddNum(TableLayoutPanel g, string label, NumericUpDown n, int col, int row) { var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent }; f.Controls.Add(MutedLabel(label)); f.Controls.Add(n); g.Controls.Add(f, col, row); }

    async Task AutoConvertLigands(ListBox list)
    {
        // Convert the ligand files already selected in the ligand list.
        // All generated files are kept inside the tool's convert folder.
        if (list.Items.Count == 0)
        {
            MessageBox.Show(
                "Select one or more ligand files first, then click DIRECT CONVERT PDBQT.",
                "ELB DockTool — Direct Convert",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var inputsForRun = list.Items.Cast<object>()
            .Select(x => x?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();

        string conversionBase = ConversionBaseFolderForInputs(inputsForRun);
        string? runDir = CreateConversionRunFolder("AutoConvert", conversionBase);
        if (runDir == null) return;

        var result = await ConvertLigandListToPdbqt(list, runDir, copyExistingPdbqt: true);
        string summary = $"Converted: {result.Converted}\nAlready PDBQT: {result.AlreadyPdbqt}\nFailed: {result.Failed}" +
                         $"\n\nSaved in:\n{runDir}";
        if (result.Problems.Count > 0)
            summary += "\n\nProblems:\n" + string.Join("\n", result.Problems.Take(8));

        MessageBox.Show(
            summary,
            "ELB DockTool — Direct Convert",
            MessageBoxButtons.OK,
            result.Failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    string? CreateConversionRunFolder(string stage, string? baseFolder = null)
    {
        string root = Path.Combine(baseFolder ?? ConvertFolder(), stage + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        try
        {
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not create the convert folder:\n\n" + ex.Message,
                "ELB DockTool — Convert", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    async Task<(int Converted, int AlreadyPdbqt, int Failed, List<string> Problems)> ConvertLigandListToPdbqt(
        ListBox list, string outputDir, bool copyExistingPdbqt)
    {
        int converted = 0, alreadyPdbqt = 0, failed = 0;
        var problems = new List<string>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            var detected = AppServices.DetectExecutable("obabel.exe");
            if (!string.IsNullOrWhiteSpace(detected))
            {
                Config.OpenBabelPath = detected;
                try { AppServices.SaveConfig(Config); } catch { }
            }
        }

        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            MessageBox.Show(
                "Open Babel (obabel.exe) was not found. Install Open Babel or set its path in Settings, then try again.",
                "ELB DockTool — Open Babel",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return (0, 0, list.Items.Count, new List<string> { "Open Babel was not found." });
        }

        var inputs = list.Items.Cast<object>()
            .Select(x => x?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();

        foreach (var input in inputs)
        {
            try
            {
                if (!File.Exists(input))
                {
                    failed++;
                    problems.Add($"{Path.GetFileName(input)} — file does not exist.");
                    continue;
                }

                string ext = Path.GetExtension(input);
                string name = SafeName(Path.GetFileNameWithoutExtension(input));
                string outFile = Path.Combine(outputDir, name + "_converted.pdbqt");

                if (string.Equals(ext, ".pdbqt", StringComparison.OrdinalIgnoreCase))
                {
                    var check = ValidateMoleculeFile(input, "ligand");
                    if (!check.Ok)
                    {
                        failed++;
                        problems.Add($"{Path.GetFileName(input)} — {check.Message}");
                        continue;
                    }

                    if (copyExistingPdbqt)
                    {
                        File.Copy(input, outFile, true);
                        replacements[input] = outFile;
                    }
                    else
                    {
                        replacements[input] = input;
                    }

                    alreadyPdbqt++;
                    continue;
                }

                // Use an explicit input format and a short temporary output path.
                // Open Babel can report "Cannot write to ... 0 molecules converted"
                // when a nested/long destination path is rejected. Writing to a
                // short temp file first avoids that Windows path/permission edge case;
                // the finished PDBQT is then copied into the requested run folder.
                string inputFormat = ext.TrimStart('.');
                string tempOut = Path.Combine(Path.GetTempPath(), "ELB_DockTool_" + Guid.NewGuid().ToString("N") + ".pdbqt");
                (int ExitCode, string Stdout, string Stderr) r = (-1, "", "");
                try
                {
                    r = await RunProcess(
                        AppServices.Resolve(Config.OpenBabelPath),
                        $"-i {inputFormat} {Q(input)} -o pdbqt -O {Q(tempOut)}",
                        AppServices.BaseDir);

                    if (r.ExitCode == 0 && File.Exists(tempOut) && new FileInfo(tempOut).Length > 0)
                    {
                        Directory.CreateDirectory(outputDir);
                        File.Copy(tempOut, outFile, true);
                    }
                }
                finally
                {
                    try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
                }

                var checkOut = File.Exists(outFile)
                    ? ValidateMoleculeFile(outFile, "ligand")
                    : (Ok: false, Message: "no valid PDBQT output was created");

                if (r.ExitCode == 0 && checkOut.Ok)
                {
                    replacements[input] = outFile;
                    converted++;
                }
                else
                {
                    failed++;
                    string detail = string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout : r.Stderr;
                    if (string.IsNullOrWhiteSpace(detail)) detail = checkOut.Message;
                    problems.Add($"{Path.GetFileName(input)} — {detail.Trim()}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                problems.Add($"{Path.GetFileName(input)} — {ex.Message}");
            }
        }

        // Replace the selected source paths with the generated PDBQT paths.
        foreach (var pair in replacements)
        {
            int index = list.Items.IndexOf(pair.Key);
            if (index >= 0) list.Items[index] = pair.Value;
        }

        return (converted, alreadyPdbqt, failed, problems);
    }

    async Task<bool> EnsureLigandsPdbqt(ListBox list)
    {
        bool allPdbqt = list.Items.Cast<string>().All(p =>
            string.Equals(Path.GetExtension(p), ".pdbqt", StringComparison.OrdinalIgnoreCase));

        if (allPdbqt)
            return true;

        var inputsForRun = list.Items.Cast<object>()
            .Select(x => x?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();

        string conversionBase = ConversionBaseFolderForInputs(inputsForRun);
        string? runDir = CreateConversionRunFolder("AutoConvert", conversionBase);
        if (runDir == null) return false;

        var result = await ConvertLigandListToPdbqt(list, runDir, copyExistingPdbqt: true);
        if (result.Failed > 0)
        {
            MessageBox.Show(
                $"AutoDock preparation could not convert all ligands.\n\n" +
                $"Converted: {result.Converted}\nAlready PDBQT: {result.AlreadyPdbqt}\nFailed: {result.Failed}\n\n" +
                string.Join("\n", result.Problems.Take(8)),
                "ELB DockTool — Ligand Preparation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        return list.Items.Cast<string>().All(p =>
            string.Equals(Path.GetExtension(p), ".pdbqt", StringComparison.OrdinalIgnoreCase));
    }

    async Task EnergyMinimizeLigands(ListBox list)
    {
        if (list.Items.Count == 0)
        {
            MessageBox.Show(
                "Select one or more ligand files first.",
                "ELB DockTool — Energy Minimization",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var f = new Form
        {
            Text = "ELB DockTool — Energy Minimization",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(540, 555),
            MinimumSize = new Size(540, 555),
            BackColor = Bg,
            ForeColor = TextColor,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var title = new Label
        {
            Text = "ENERGY MINIMIZATION",
            AutoSize = true,
            ForeColor = Purple,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Location = new Point(24, 20)
        };

        var info = new Label
        {
            Text = "Minimize the selected ligand structures without converting them to PDBQT.\n" +
                   "The minimized result keeps the original file format and is saved under ligands\\minimized.\n" +
                   "Click AUTO-CONVERT later when you want PDBQT files for docking.",
            AutoSize = true,
            ForeColor = Muted,
            Location = new Point(24, 58)
        };

        var ffLabel = L("FORCE FIELD", 24, 135);
        var ff = new ComboBox
        {
            Location = new Point(24, 158),
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(9, 15, 25),
            ForeColor = TextColor
        };
        ff.Items.AddRange(new object[] { "MMFF94", "MMFF94s", "UFF", "GAFF", "Ghemical" });
        ff.SelectedIndex = 0;

        var stepsLabel = L("MAX STEPS", 275, 135);
        var steps = new NumericUpDown
        {
            Location = new Point(275, 158),
            Width = 150,
            Minimum = 1,
            Maximum = 1000000,
            Value = 2500,
            Increment = 100,
            DecimalPlaces = 0,
            BackColor = Color.FromArgb(9, 15, 25),
            ForeColor = TextColor
        };

        var algoLabel = L("ALGORITHM", 24, 208);
        var algo = new ComboBox
        {
            Location = new Point(24, 231),
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(9, 15, 25),
            ForeColor = TextColor
        };
        algo.Items.AddRange(new object[] { "Conjugate Gradient", "Steepest Descent" });
        algo.SelectedIndex = 0;

        var addHydrogens = new CheckBox
        {
            Text = "Add hydrogens",
            AutoSize = true,
            Location = new Point(24, 272),
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Checked = true
        };

        var gasteiger = new CheckBox
        {
            Text = "Gasteiger charges",
            AutoSize = true,
            Location = new Point(170, 272),
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Checked = false
        };

        var generate3D = new CheckBox
        {
            Text = "Generate 3D",
            AutoSize = true,
            Location = new Point(340, 272),
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Checked = false
        };

        var autoConvert = new CheckBox
        {
            Text = "AUTO-CONVERT MINIMIZED → PDBQT",
            AutoSize = true,
            Location = new Point(24, 310),
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Checked = false
        };

        var autoConvertHint = new Label
        {
            Text = "OFF = keep minimized files in their original format.  ON = also create PDBQT in this minimization run.",
            AutoSize = true,
            Location = new Point(24, 333),
            ForeColor = Muted,
            Font = new Font("Segoe UI", 8.5F)
        };

        var minimize = MakeButton("MINIMIZE", 145, 44, Purple, Color.White);
        minimize.Location = new Point(24, 368);

        var cancel = MakeButton("CANCEL", 100, 44, Panel2, TextColor);
        cancel.Location = new Point(185, 368);

        f.Controls.AddRange(new Control[] { title, info, ffLabel, ff, stepsLabel, steps, algoLabel, algo,
            addHydrogens, gasteiger, generate3D, autoConvert, autoConvertHint, minimize, cancel });

        cancel.Click += (_, _) => f.Close();
        minimize.Click += async (_, _) =>
        {
            minimize.Enabled = false;
            cancel.Enabled = false;

            try
            {
                string? runDir = CreateMinimizationRunFolder(
                    "EnergyMinimization_" + SafeName(ff.Text) + "_" + Decimal.ToInt32(steps.Value));
                if (runDir == null) return;

                int done = 0, failed = 0;
                var problems = new List<string>();
                var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int stepValue = Decimal.ToInt32(steps.Value);
                string ffValue = ff.Text;
                string algorithmArg = algo.SelectedIndex == 1 ? " --sd" : "";

                foreach (string input in list.Items.Cast<string>().ToList())
                {
                    string name = SafeName(Path.GetFileNameWithoutExtension(input));
                    string originalExt = Path.GetExtension(input).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(originalExt))
                        originalExt = ".sdf";

                    string outFile = Path.Combine(
                        runDir,
                        name + "_minimized_" + SafeName(ffValue) + "_" + stepValue + originalExt);

                    string intermediate = Path.Combine(runDir, name + "_minimize_input.sdf");
                    string prepared = Path.Combine(runDir, name + "_minimize_prepared.sdf");
                    string minSdf = Path.Combine(runDir, name + "_minimized_work.sdf");

                    (int ExitCode, string Stdout, string Stderr) r;
                    bool ok = false;

                    try
                    {
                        // Never convert the active ligand list to PDBQT here.
                        // Open Babel uses SDF only as an internal working format,
                        // then writes the minimized structure back to the original
                        // input extension.
                        var toSdf = await RunProcess(
                            AppServices.Resolve(Config.OpenBabelPath),
                            $"{Q(input)} -O {Q(intermediate)}",
                            AppServices.BaseDir);
                        r = toSdf;

                        if (toSdf.ExitCode == 0 && File.Exists(intermediate) && new FileInfo(intermediate).Length > 0)
                        {
                            // Optional ligand-preparation operations are applied before
                            // minimization. They are deliberately independent checkboxes.
                            // The defaults preserve the previous behavior: add hydrogens ON,
                            // Gasteiger charges OFF, generate 3D OFF.
                            string prepOps = "";
                            if (addHydrogens.Checked) prepOps += " -h";
                            if (generate3D.Checked) prepOps += " --gen3d";
                            if (gasteiger.Checked) prepOps += " --partialcharge gasteiger";

                            string minInput = intermediate;
                            if (!string.IsNullOrWhiteSpace(prepOps))
                            {
                                var prep = await RunProcess(
                                    AppServices.Resolve(Config.OpenBabelPath),
                                    $"{Q(intermediate)} -O {Q(prepared)}{prepOps}",
                                    AppServices.BaseDir);

                                if (prep.ExitCode == 0 && File.Exists(prepared) && new FileInfo(prepared).Length > 0)
                                    minInput = prepared;
                                else
                                    r = prep;
                            }

                            var min = await RunProcess(
                                AppServices.Resolve(Config.OpenBabelPath),
                                $"{Q(minInput)} -O {Q(minSdf)} --minimize --ff {Q(ffValue)} --steps {stepValue}{algorithmArg}",
                                AppServices.BaseDir);
                            r = min;

                            if (min.ExitCode == 0 && File.Exists(minSdf) && new FileInfo(minSdf).Length > 0)
                            {
                                var back = await RunProcess(
                                    AppServices.Resolve(Config.OpenBabelPath),
                                    $"{Q(minSdf)} -O {Q(outFile)}",
                                    AppServices.BaseDir);
                                r = back;

                                ok = back.ExitCode == 0 &&
                                     File.Exists(outFile) &&
                                     new FileInfo(outFile).Length > 0 &&
                                     ValidateMoleculeFile(outFile, "ligand").Ok;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        r = (-1, "", ex.Message);
                    }

                    try
                    {
                        string logText =
                            $"ELB DockTool — Energy Minimization{Environment.NewLine}" +
                            $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                            $"Input: {input}{Environment.NewLine}" +
                            $"Original format: {originalExt}{Environment.NewLine}" +
                            $"Force field: {ffValue}{Environment.NewLine}" +
                            $"Steps: {stepValue}{Environment.NewLine}" +
                            $"Algorithm: {(algo.SelectedIndex == 1 ? "Steepest Descent" : "Conjugate Gradient")}{Environment.NewLine}" +
                            $"Add hydrogens: {addHydrogens.Checked}{Environment.NewLine}" +
                            $"Gasteiger charges: {gasteiger.Checked}{Environment.NewLine}" +
                            $"Generate 3D: {generate3D.Checked}{Environment.NewLine}" +
                            $"Output: {outFile}{Environment.NewLine}" +
                            $"Exit code: {r.ExitCode}{Environment.NewLine}{Environment.NewLine}" +
                            "----- STDOUT -----" + Environment.NewLine + r.Stdout + Environment.NewLine +
                            "----- STDERR -----" + Environment.NewLine + r.Stderr + Environment.NewLine;
                        File.WriteAllText(Path.Combine(runDir, name + "_minimization.log"), logText);
                    }
                    catch { }

                    if (ok)
                    {
                        replacements[input] = outFile;
                        done++;
                    }
                    else
                    {
                        failed++;
                        string detail = string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout : r.Stderr;
                        if (string.IsNullOrWhiteSpace(detail))
                            detail = "Open Babel did not create a valid minimized structure in the original format.";
                        problems.Add($"{Path.GetFileName(input)} — {detail.Trim()}");
                    }
                }

                // The minimized structures become the active ligand inputs first.
                // IMPORTANT: minimization itself never changes them to PDBQT.
                foreach (var pair in replacements)
                {
                    int index = list.Items.IndexOf(pair.Key);
                    if (index >= 0) list.Items[index] = pair.Value;
                }

                int autoConverted = 0;
                int autoConvertFailed = 0;
                var autoConvertProblems = new List<string>();

                // Optional one-click conversion. It is deliberately nested inside
                // this minimization run so the provenance is obvious.
                // IMPORTANT: this PDBQT conversion intentionally does NOT add
                // --partialcharge gasteiger again. If Gasteiger was selected above,
                // it was already applied to the minimized SDF before minimization.
                // ligands\minimized\<run>\convert\AutoConvert_<time>
                if (autoConvert.Checked && replacements.Count > 0)
                {
                    using var minimizedList = new ListBox();
                    foreach (var minimizedPath in replacements.Values)
                        minimizedList.Items.Add(minimizedPath);

                    string nestedConvertBase = Path.Combine(runDir, "convert");
                    string? nestedRunDir = CreateConversionRunFolder("AutoConvert", nestedConvertBase);
                    if (nestedRunDir != null)
                    {
                        var convertedResult = await ConvertLigandListToPdbqt(
                            minimizedList, nestedRunDir, copyExistingPdbqt: true);

                        autoConverted = convertedResult.Converted + convertedResult.AlreadyPdbqt;
                        autoConvertFailed = convertedResult.Failed;
                        autoConvertProblems.AddRange(convertedResult.Problems);

                        // Replace only the minimized paths that successfully became PDBQT.
                        foreach (string originalMinimized in replacements.Values.ToList())
                        {
                            int pos = minimizedList.Items.IndexOf(originalMinimized);
                            if (pos >= 0)
                            {
                                string pdbqt = minimizedList.Items[pos]?.ToString() ?? "";
                                if (string.Equals(Path.GetExtension(pdbqt), ".pdbqt", StringComparison.OrdinalIgnoreCase))
                                {
                                    int index = list.Items.IndexOf(originalMinimized);
                                    if (index >= 0) list.Items[index] = pdbqt;
                                }
                            }
                        }
                    }
                }

                string message =
                    $"Minimized: {done}\nFailed: {failed}\n\n" +
                    $"Force field: {ffValue}\nSteps: {stepValue}\n" +
                    $"Add hydrogens: {addHydrogens.Checked}\n" +
                    $"Gasteiger charges: {gasteiger.Checked}\n" +
                    $"Generate 3D: {generate3D.Checked}\n\n" +
                    $"Minimized structures saved in:\n{runDir}\n\n";

                if (autoConvert.Checked)
                {
                    message += $"Auto-converted to PDBQT: {autoConverted}\n" +
                               $"Auto-convert failed: {autoConvertFailed}\n\n" +
                               "PDBQT files are saved inside the convert folder of this minimization run.";
                    if (autoConvertProblems.Count > 0)
                        message += "\n\nConversion problems:\n" + string.Join("\n", autoConvertProblems.Take(8));
                }
                else
                {
                    message += "PDBQT conversion was NOT performed. The minimized structures remain in their original formats. " +
                               "Use AUTO-CONVERT later when you are ready for docking.";
                }

                if (problems.Count > 0)
                    message += "\n\nMinimization problems:\n" + string.Join("\n", problems.Take(8));

                MessageBox.Show(
                    message,
                    "ELB DockTool — Energy Minimization",
                    MessageBoxButtons.OK,
                    failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

                if (done > 0)
                    f.DialogResult = DialogResult.OK;
            }
            finally
            {
                minimize.Enabled = true;
                cancel.Enabled = true;
            }
        };

        f.ShowDialog(this);
    }

    string? CreateMinimizationRunFolder(string stage)
    {
        string root = Path.Combine(LigandsFolder(), "minimized", stage + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        try
        {
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not create the minimization folder:\n\n" + ex.Message,
                "ELB DockTool — Energy Minimization",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return null;
        }
    }

    void SaveLigandPanel(ListBox list)
    {
        if (list.Items.Count == 0)
        {
            MessageBox.Show("There are no ligands in the panel to save.", "ELB DockTool — Save Ligands", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            string folder = LigandsFolder();
            string manifest = Path.Combine(folder, "ligands_panel.saved.txt");
            var savedLines = new List<string>();
            var errors = new List<string>();

            for (int i = 0; i < list.Items.Count; i++)
            {
                string source = list.Items[i]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(source)) continue;

                try
                {
                    if (!File.Exists(source))
                    {
                        errors.Add($"{Path.GetFileName(source)} — file not found");
                        continue;
                    }

                    // Files already inside the project ligands folder are persistent.
                    // External files are copied into the project folder so LOAD SAVED
                    // can restore the panel even after the original source is moved.
                    string fullSource = Path.GetFullPath(source);
                    string fullLigands = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string persistent = fullSource.StartsWith(fullLigands, StringComparison.OrdinalIgnoreCase)
                        ? fullSource
                        : CopyIntoProjectFolder(fullSource, folder);

                    bool selected = list.SelectedIndices.Contains(i);
                    savedLines.Add((selected ? "1" : "0") + "\t" + persistent);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(source)} — {ex.Message}");
                }
            }

            File.WriteAllLines(manifest, savedLines, Encoding.UTF8);

            string message = $"Saved {savedLines.Count} ligand(s) and the current Ligands panel state.\n\n{manifest}";
            if (errors.Count > 0) message += "\n\nProblems:\n" + string.Join("\n", errors.Take(8));
            MessageBox.Show(message, "ELB DockTool — Save Ligands", MessageBoxButtons.OK,
                errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ELB DockTool — Save Ligands", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void LoadSavedLigandPanel(ListBox list)
    {
        string folder = LigandsFolder();
        string manifest = Path.Combine(folder, "ligands_panel.saved.txt");

        if (!File.Exists(manifest))
        {
            // Backward-compatible fallback: load saved PDBQT files from the main ligands folder.
            AddStoredFiles(list, folder, "ligand");
            return;
        }

        int added = 0;
        int missing = 0;
        foreach (string raw in File.ReadAllLines(manifest, Encoding.UTF8))
        {
            string line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            bool selected = false;
            string file = line;
            int tab = line.IndexOf('\t');
            if (tab > 0 && (line[0] == '0' || line[0] == '1'))
            {
                selected = line[0] == '1';
                file = line[(tab + 1)..].Trim();
            }

            if (!File.Exists(file)) { missing++; continue; }
            if (!ValidateMoleculeFile(file, "ligand").Ok) continue;

            int existingIndex = list.Items.Cast<string>().ToList().FindIndex(x =>
                string.Equals(x, file, StringComparison.OrdinalIgnoreCase));

            if (existingIndex < 0)
            {
                list.Items.Add(file);
                existingIndex = list.Items.Count - 1;
                added++;
            }

            if (selected && existingIndex >= 0)
                list.SetSelected(existingIndex, true);
        }

        MessageBox.Show(
            $"Loaded {added} saved ligand(s) and restored the saved selection." + (missing > 0 ? $"\nMissing files: {missing}" : ""),
            "ELB DockTool — Load Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SelectFiles(ListBox list, string filter)
    {
        using var d = new OpenFileDialog { Multiselect = true, Filter = filter };
        if (d.ShowDialog() == DialogResult.OK) foreach (var f in d.FileNames) if (!list.Items.Contains(f)) list.Items.Add(f);
    }

    void SelectValidatedFiles(ListBox list, string role, string filter)
    {
        using var d = new OpenFileDialog { Multiselect = true, Filter = filter };
        if (d.ShowDialog() != DialogResult.OK) return;

        var accepted = new List<string>();
        var rejected = new List<string>();

        foreach (var file in d.FileNames)
        {
            var check = ValidateMoleculeFile(file, role);
            if (check.Ok)
            {
                string stored = role.Equals("receptor", StringComparison.OrdinalIgnoreCase)
                    ? CopyIntoProjectFolder(file, ReceptorsFolder())
                    : CopyIntoProjectFolder(file, LigandsFolder());

                if (!list.Items.Contains(stored))
                {
                    list.Items.Add(stored);
                    accepted.Add(Path.GetFileName(stored));
                }
            }
            else
            {
                rejected.Add($"• {Path.GetFileName(file)} — {check.Message}");
            }
        }

        if (rejected.Count > 0)
        {
            string roleName = role.Equals("receptor", StringComparison.OrdinalIgnoreCase) ? "receptor" : "ligand";
            MessageBox.Show(
                $"These files were not added as {roleName}(s):\n\n{string.Join("\n", rejected)}\n\n" +
                "The file selector now checks the molecular file contents instead of trusting only the filename extension.",
                "ELB DockTool — File Validation",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    (bool Ok, string Message) ValidateMoleculeFile(string file, string role)
    {
        if (!File.Exists(file))
            return (false, "file does not exist.");

        string ext = Path.GetExtension(file).ToLowerInvariant();
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex)
        {
            return (false, "could not read file: " + ex.Message);
        }

        if (string.IsNullOrWhiteSpace(text))
            return (false, "file is empty.");

        if (role.Equals("receptor", StringComparison.OrdinalIgnoreCase))
        {
            if (ext != ".pdbqt")
                return (false, "receptors must be PDBQT files.");

            int atoms = CountPdbqtAtoms(text);
            bool hasLigandTree = Regex.IsMatch(text, @"(?m)^\s*(ROOT|BRANCH|TORSDOF)\b", RegexOptions.IgnoreCase);

            if (atoms == 0)
                return (false, "no PDBQT ATOM/HETATM records were found.");
            if (hasLigandTree)
                return (false, "this PDBQT contains ligand-style ROOT/BRANCH/TORSDOF records, so it looks like a ligand, not a receptor.");

            return (true, "");
        }

        // Ligand validation. PDBQT has enough structure to distinguish a normal
        // AutoDock ligand from a protein/receptor PDBQT.
        if (ext == ".pdbqt")
        {
            int atoms = CountPdbqtAtoms(text);
            bool hasLigandTree = Regex.IsMatch(text, @"(?m)^\s*(ROOT|BRANCH|TORSDOF)\b", RegexOptions.IgnoreCase);

            if (atoms == 0)
                return (false, "no PDBQT ATOM/HETATM records were found.");
            if (!hasLigandTree)
                return (false, "this PDBQT looks like a receptor/protein (no ROOT/BRANCH/TORSDOF ligand structure was found).");

            return (true, "");
        }

        // PDB is ambiguous, so detect an obvious protein/receptor rather than
        // rejecting every PDB ligand.
        if (ext == ".pdb")
        {
            var proteinResidues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ALA","ARG","ASN","ASP","CYS","GLN","GLU","GLY","HIS","ILE",
                "LEU","LYS","MET","PHE","PRO","SER","THR","TRP","TYR","VAL",
                "HID","HIE","HIP","CYX","ASH","GLH","LYN"
            };

            var residues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int atomLines = 0;
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (!(line.StartsWith("ATOM", StringComparison.OrdinalIgnoreCase) ||
                      line.StartsWith("HETATM", StringComparison.OrdinalIgnoreCase))) continue;

                atomLines++;
                if (line.Length >= 20)
                {
                    string res = line.Substring(17, Math.Min(3, line.Length - 17)).Trim();
                    if (proteinResidues.Contains(res)) residues.Add(res);
                }
            }

            if (atomLines >= 20 && residues.Count >= 3)
                return (false, "this PDB looks like a protein/receptor (multiple amino-acid residues were detected).");

            return (true, "");
        }

        // SDF/MOL2/MOL/SMI/XYZ are treated as ligand inputs and are converted
        // when necessary. They are not silently accepted as receptors.
        return (true, "");
    }

    static int CountPdbqtAtoms(string text)
    {
        int count = 0;
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (line.StartsWith("ATOM", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("HETATM", StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    async Task RunDocking(ListBox recList, ListBox ligList, NumericUpDown ex, NumericUpDown modes, NumericUpDown energy, NumericUpDown cpu, NumericUpDown seed, string output)
    {
        SetVinaMonitor("● CHECKING — validating Vina and selected inputs", Muted, "Preparing docking validation…", "Jobs: 0 / 0");
        if (!AppServices.Exists(Config.VinaPath))
        {
            SetVinaMonitor("✕ ERROR — Vina executable not found", Red, "Set the correct vina.exe path in Settings.", "Jobs: 0 / 0");
            MessageBox.Show("AutoDock Vina is not found. Set the correct vina.exe path in Settings.", "Docking", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (recList.Items.Count == 0 || ligList.Items.Count == 0)
        {
            SetVinaMonitor("✕ ERROR — missing docking inputs", Red, "Select at least one receptor and one ligand.", "Jobs: 0 / 0");
            MessageBox.Show("Select at least one receptor and one ligand.", "Docking", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // AutoDock always runs on PDBQT. If the user has not pressed
        // AUTO-CONVERT yet, prepare every selected ligand automatically and
        // keep the generated PDBQT in the tool's convert folder.
        SetVinaMonitor("● PREPARING — converting ligands to PDBQT", Muted, "Preparing selected ligands with Open Babel…", "Jobs: 0 / 0");
        if (!await EnsureLigandsPdbqt(ligList))
        {
            SetVinaMonitor("✕ ERROR — ligand preparation failed", Red, "One or more ligands could not be converted to PDBQT.", "Jobs: 0 / 0");
            return;
        }

        var invalidReceptors = recList.Items.Cast<string>()
            .Select(p => (Path: p, Check: ValidateMoleculeFile(p, "receptor")))
            .Where(x => !x.Check.Ok).ToList();
        var invalidLigands = ligList.Items.Cast<string>()
            .Select(p => (Path: p, Check: ValidateMoleculeFile(p, "ligand")))
            .Where(x => !x.Check.Ok).ToList();

        if (invalidReceptors.Count > 0 || invalidLigands.Count > 0)
        {
            var problems = new List<string>();
            foreach (var x in invalidReceptors) problems.Add($"RECEPTOR: {Path.GetFileName(x.Path)} — {x.Check.Message}");
            foreach (var x in invalidLigands) problems.Add($"LIGAND: {Path.GetFileName(x.Path)} — {x.Check.Message}");
            SetVinaMonitor("✕ ERROR — invalid docking inputs", Red, string.Join(Environment.NewLine, problems), "Jobs: 0 / 0");
            MessageBox.Show(
                "Docking was not started because the selected files do not match their roles:\n\n" +
                string.Join("\n", problems) +
                "\n\nSelect the correct receptor and ligand files and try again.",
                "ELB DockTool — Invalid Docking Inputs", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        SyncReceptorGrids(recList);
        foreach (string receptor in recList.Items)
        {
            if (!receptorGrids.TryGetValue(receptor, out var grid))
                grid = receptorGrids[receptor] = new GridSpec();
            if (grid.SizeX <= 0 || grid.SizeY <= 0 || grid.SizeZ <= 0)
            {
                SetVinaMonitor("✕ ERROR — invalid search grid", Red, $"Grid sizes for {Path.GetFileName(receptor)} must be greater than zero.", "Jobs: 0 / 0");
                MessageBox.Show($"Grid sizes for {Path.GetFileName(receptor)} must be greater than zero. Open GRID SETTINGS and correct them.", "Docking", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        Directory.CreateDirectory(output);
        var vina = AppServices.Resolve(Config.VinaPath);
        int total = recList.Items.Count * ligList.Items.Count, done = 0, failed = 0;
        SetVinaMonitor("● READY — starting AutoDock Vina", Green, $"Executable: {vina}", $"Jobs: 0 / {total}");
        using var progress = new ProgressForm(total);
        progress.Show(this);
        var failures = new List<string>();
        try
        {
            foreach (string receptor in recList.Items)
            foreach (string ligand in ligList.Items)
            {
                if (progress.Cancelled)
                {
                    SetVinaMonitor("⚠ CANCELLED — docking stopped by user", Muted, $"Completed: {done}; Failed: {failed}; Remaining: {total - done - failed}", $"Jobs: {done + failed} / {total}");
                    return;
                }

                string receptorName = Path.GetFileNameWithoutExtension(receptor);
                string ligandName = Path.GetFileNameWithoutExtension(ligand);
                string dir = Path.Combine(output, SafeName(receptorName));
                Directory.CreateDirectory(dir);
                string ligandPdbqt = ligand;

                SetVinaMonitor("● PREPARING — " + ligandName, Muted, $"Receptor: {receptorName}{Environment.NewLine}Ligand: {ligandName}", $"Jobs: {done + failed} / {total}");
                if (!string.Equals(Path.GetExtension(ligand), ".pdbqt", StringComparison.OrdinalIgnoreCase))
                {
                    ligandPdbqt = await ConvertToPdbqt(ligand, dir);
                    if (string.IsNullOrWhiteSpace(ligandPdbqt))
                    {
                        failed++;
                        string msg = $"{receptorName} + {ligandName}: ligand conversion to PDBQT failed.";
                        failures.Add(msg);
                        progress.Step("FAILED: " + msg);
                        SetVinaMonitor("✕ FAILED — ligand preparation", Red, msg, $"Jobs: {done + failed} / {total}");
                        continue;
                    }
                }

                string outFile = Path.Combine(dir, SafeName(ligandName) + "_out.pdbqt");
                string logFile = Path.Combine(dir, SafeName(ligandName) + "_docking.log");
                string executionLog = Path.Combine(dir, SafeName(ligandName) + "_execution.log");
                if (!receptorGrids.TryGetValue(receptor, out var grid))
                    grid = receptorGrids[receptor] = new GridSpec();
                string seedArg = seed.Value != 0 ? $" --seed {Decimal.ToInt32(seed.Value)}" : "";
                int cpuValue = Decimal.ToInt32(cpu.Value);
                int exhaustivenessValue = Math.Max(1, Decimal.ToInt32(ex.Value));
                int modesValue = Math.Max(1, Decimal.ToInt32(modes.Value));
                decimal energyValue = energy.Value;
                string args = $"--receptor {Q(receptor)} --ligand {Q(ligandPdbqt)} --center_x {grid.CenterX.ToString(CultureInfo.InvariantCulture)} --center_y {grid.CenterY.ToString(CultureInfo.InvariantCulture)} --center_z {grid.CenterZ.ToString(CultureInfo.InvariantCulture)} --size_x {grid.SizeX.ToString(CultureInfo.InvariantCulture)} --size_y {grid.SizeY.ToString(CultureInfo.InvariantCulture)} --size_z {grid.SizeZ.ToString(CultureInfo.InvariantCulture)} --cpu {cpuValue} --exhaustiveness {exhaustivenessValue} --num_modes {modesValue} --energy_range {energyValue.ToString(CultureInfo.InvariantCulture)}{seedArg} --out {Q(outFile)} --log {Q(logFile)}";
                SetVinaMonitor("● RUNNING VINA", Green, $"Receptor: {receptorName}{Environment.NewLine}Ligand: {ligandName}{Environment.NewLine}Command: vina.exe {args}", $"Jobs: {done + failed} / {total}");
                var result = await RunProcess(vina, args, AppServices.BaseDir);
                string combined = (string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr).Trim();
                string execText = $"ELB DockTool — AutoDock Vina execution log{Environment.NewLine}" +
                                  $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                                  $"Receptor: {receptor}{Environment.NewLine}" +
                                  $"Ligand: {ligand}{Environment.NewLine}" +
                                  $"Command: \"{vina}\" {args}{Environment.NewLine}" +
                                  $"Exit code: {result.ExitCode}{Environment.NewLine}{Environment.NewLine}" +
                                  "----- STDOUT -----" + Environment.NewLine + result.Stdout + Environment.NewLine +
                                  "----- STDERR -----" + Environment.NewLine + result.Stderr + Environment.NewLine;
                try { File.WriteAllText(executionLog, execText); } catch { }

                bool outputOk = result.ExitCode == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0 && HasVinaPoses(outFile);
                if (outputOk)
                {
                    done++;
                    progress.Step($"SUCCESS: {ligandName} × {receptorName}");
                    SetVinaMonitor("✓ SUCCESS — Vina completed", Green, $"{receptorName} + {ligandName}{Environment.NewLine}Output: {outFile}{Environment.NewLine}Exit code: 0", $"Jobs: {done + failed} / {total}");
                }
                else
                {
                    failed++;
                    string error = string.IsNullOrWhiteSpace(combined)
                        ? $"Vina exited with code {result.ExitCode} and did not produce a valid docking result."
                        : combined;
                    if (error.Length > 5000) error = error[^5000..];
                    string msg = $"{receptorName} + {ligandName}: Vina failed (exit code {result.ExitCode}).{Environment.NewLine}{error}";
                    failures.Add(msg);
                    progress.Step($"FAILED: {ligandName} × {receptorName}");
                    SetVinaMonitor("✕ FAILED — AutoDock Vina error", Red, msg + Environment.NewLine + $"Execution log: {executionLog}", $"Jobs: {done + failed} / {total}");
                }
            }
        }
        finally { progress.Close(); }

        RefreshResults();
        if (failed == 0 && done == total)
        {
            SetVinaMonitor("✓ ALL JOBS COMPLETED", Green, $"{done} of {total} docking jobs produced valid Vina output.", $"Jobs: {done} / {total}");
            MessageBox.Show($"Docking completed successfully. {done} of {total} job(s) produced valid output.\nOutput: {output}", "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else if (done > 0)
        {
            SetVinaMonitor("⚠ PARTIALLY COMPLETED", Color.Goldenrod, $"Successful: {done}{Environment.NewLine}Failed: {failed}{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(2))}", $"Jobs: {done + failed} / {total}");
            MessageBox.Show($"Docking partially completed.\nSuccessful: {done}\nFailed: {failed}\n\nSee the Vina Execution Monitor and *_execution.log files for the exact Vina errors.\nOutput: {output}", "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            SetVinaMonitor("✕ ALL JOBS FAILED", Red, string.Join(Environment.NewLine + Environment.NewLine, failures.Take(3)), $"Jobs: {failed} / {total}");
            MessageBox.Show($"Docking failed. 0 of {total} job(s) produced valid output.\n\nThe exact AutoDock Vina error is shown in the Vina Execution Monitor and saved as *_execution.log inside each receptor folder.\nOutput: {output}", "ELB DockTool — Vina Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SetVinaMonitor(string status, Color color, string log, string jobs)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetVinaMonitor(status, color, log, jobs))); return; }
        if (vinaMonitorStatus != null) { vinaMonitorStatus.Text = status; vinaMonitorStatus.ForeColor = color; }
        if (vinaMonitorJobs != null) vinaMonitorJobs.Text = jobs;
        if (vinaMonitorLog != null) { vinaMonitorLog.Text = log; vinaMonitorLog.SelectionStart = vinaMonitorLog.TextLength; vinaMonitorLog.ScrollToCaret(); }
    }

    static bool HasVinaPoses(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length == 0) return false;
            string text = File.ReadAllText(file);
            return Regex.IsMatch(text, @"(?m)^MODEL\s+1\s*$") || text.Contains("REMARK VINA RESULT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    async Task<string?> ConvertToPdbqt(string input, string outputDir)
    {
        if (!AppServices.Exists(Config.OpenBabelPath)) return null;
        string outFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input) + "_converted.pdbqt");
        var r = await RunProcess(AppServices.Resolve(Config.OpenBabelPath), $"-i {Path.GetExtension(input).TrimStart('.')} {Q(input)} -o pdbqt -O {Q(outFile)}", AppServices.BaseDir);
        return r.ExitCode == 0 && File.Exists(outFile) ? outFile : null;
    }

    static string SafeName(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return string.IsNullOrWhiteSpace(s) ? "item" : s; }
    static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcess(string file, string args, string work)
    {
        var psi = new ProcessStartInfo(file, args) { WorkingDirectory = work, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = new Process { StartInfo = psi }; p.Start(); var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); return (p.ExitCode, await o, await e);
    }

    Panel BuildConverter()
    {
        var p = PageBase();
        var card = Card("Molecule Converter", "Convert molecules with Open Babel. Choose preparation options here before conversion; SMILES input is also opened from this panel.");
        card.Height = 755;

        var inputList = StyledListBox(new Point(22, 94), new Size(720, 150)); inputList.Name = "smilesConverterInputList"; inputList.SelectionMode = SelectionMode.MultiExtended;
        var upload = MakeButton("UPLOAD LIGANDS", 190, 40, Purple, Color.White); upload.Location = new Point(22, 258); upload.Click += (_, _) => SelectFiles(inputList, "Molecule files|*.pdbqt;*.sdf;*.mol2;*.pdb;*.mol;*.xyz");
        var clear = MakeButton("CLEAR", 100, 40, Panel2, TextColor); clear.Location = new Point(224, 258); clear.Click += (_, _) => inputList.Items.Clear();
        var smiles = MakeButton("SMILES INPUT", 145, 40, Panel2, TextColor); smiles.Location = new Point(338, 258); smiles.Click += (_, _) => ShowSmilesInputPanel();

        var inFmt = new ComboBox { Location = new Point(22, 332), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor };
        inFmt.Items.AddRange(new object[] { "Auto-detect", "pdbqt", "sdf", "mol2", "pdb", "mol", "xyz" }); inFmt.SelectedIndex = 0;
        var outFmt = new ComboBox { Location = new Point(280, 332), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor };
        outFmt.Items.AddRange(new object[] { "pdbqt", "sdf", "mol2", "pdb", "mol", "xyz" }); outFmt.SelectedIndex = 0;
        var outDir = Box(360); outDir.Location = new Point(22, 402); outDir.Text = Path.Combine(AppServices.BaseDir, "output", "converted");
        var browse = MakeButton("BROWSE", 90, 32, Panel2, TextColor); browse.Location = new Point(392, 402); browse.Click += (_, _) => { using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) outDir.Text = d.SelectedPath; };
        var openFolder = MakeButton("OPEN FOLDER", 130, 32, Panel2, TextColor); openFolder.Location = new Point(490, 402); openFolder.Click += (_, _) =>
        {
            var folder = outDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(folder)) { MessageBox.Show("Please choose an output folder first.", "Converter", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { Directory.CreateDirectory(folder); OpenFolder(folder); }
            catch (Exception ex) { MessageBox.Show("Could not open the output folder:\n\n" + ex.Message, "Converter", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };

        // Open Babel preparation — clean two-row layout.
        // Row 1: structure preparation options.
        // Row 2: optional energy minimization settings.
        var options = new GroupBox
        {
            Text = "Open Babel preparation (used for file conversion and SMILES)",
            ForeColor = TextColor,
            Location = new Point(22, 455),
            Size = new Size(1250, 180),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        converterCanonical = new CheckBox
        { Text = "Canonicalize atom order", Checked = true, AutoSize = true,
          ForeColor = TextColor, Location = new Point(22, 30) };
        converterGen3d = new CheckBox
        { Text = "Generate 3D coordinates", Checked = true, AutoSize = true,
          ForeColor = TextColor, Location = new Point(280, 30) };
        converterAddH = new CheckBox
        { Text = "Add hydrogens", AutoSize = true, ForeColor = TextColor,
          Location = new Point(550, 30) };
        converterRemoveH = new CheckBox
        { Text = "Remove hydrogens", AutoSize = true, ForeColor = TextColor,
          Location = new Point(760, 30) };
        converterPh = new CheckBox
        { Text = "Add H for pH", AutoSize = true, ForeColor = TextColor,
          Location = new Point(980, 30) };
        converterPhValue = new NumericUpDown
        { Minimum = 0, Maximum = 14, DecimalPlaces = 1, Increment = 0.1M, Value = 7.4M,
          Width = 72, Location = new Point(1110, 27), Enabled = false,
          BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor };

        converterPh.CheckedChanged += (_, _) => converterPhValue.Enabled = converterPh.Checked;
        converterAddH.CheckedChanged += (_, _) => { if (converterAddH.Checked) converterRemoveH.Checked = false; };
        converterRemoveH.CheckedChanged += (_, _) => { if (converterRemoveH.Checked) converterAddH.Checked = false; };

        converterMinimize = new CheckBox
        { Text = "Energy minimize", AutoSize = true, ForeColor = TextColor,
          Location = new Point(22, 78) };

        var ffLabel = new Label
        { Text = "Force field", AutoSize = true, ForeColor = Muted,
          Location = new Point(250, 81), TextAlign = ContentAlignment.MiddleLeft };
        converterFf = new ComboBox
        { Location = new Point(330, 77), Width = 145, Height = 27,
          DropDownStyle = ComboBoxStyle.DropDownList,
          BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, Enabled = false };
        converterFf.Items.AddRange(new object[] { "MMFF94", "MMFF94s", "UFF", "GAFF", "Ghemical" });
        converterFf.SelectedIndex = 0;

        var stepLabel = new Label
        { Text = "Minimization steps", AutoSize = true, ForeColor = Muted,
          Location = new Point(545, 81), TextAlign = ContentAlignment.MiddleLeft };
        converterSteps = new NumericUpDown
        { Minimum = 1, Maximum = 10000, Value = 2500, Width = 90, Height = 27,
          Location = new Point(685, 77), BackColor = Color.FromArgb(9, 15, 25),
          ForeColor = TextColor, Enabled = false };

        converterMinimize.CheckedChanged += (_, _) =>
        {
            converterFf.Enabled = converterMinimize.Checked;
            converterSteps.Enabled = converterMinimize.Checked;
        };

        options.Controls.AddRange(new Control[]
        {
            converterCanonical, converterGen3d, converterAddH, converterRemoveH, converterPh, converterPhValue,
            converterMinimize, ffLabel, converterFf, stepLabel, converterSteps
        });

        var convert = MakeButton("CONVERT ALL", 180, 42, Purple, Color.White); convert.Location = new Point(525, 650); convert.Click += async (_, _) => await ConvertAll(inputList, inFmt, outFmt, outDir.Text);
        card.Controls.AddRange(new Control[] { inputList, upload, clear, smiles, L("INPUT FORMAT", 22, 306), inFmt, L("OUTPUT FORMAT", 280, 306), outFmt, L("OUTPUT FOLDER", 22, 376), outDir, browse, openFolder, options, convert });
        p.Controls.Add(card); return p;
    }

    sealed class SmilesEntry
    {
        public string Name = "";
        public string Smiles = "";
    }

    void ShowSmilesInputPanel()
    {
        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            var detected = AppServices.DetectExecutable("obabel.exe");
            if (!string.IsNullOrWhiteSpace(detected))
            {
                Config.OpenBabelPath = detected;
                try { AppServices.SaveConfig(Config); } catch { }
            }
        }
        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            MessageBox.Show("Open Babel (obabel.exe) was not found. Install Open Babel or set its path in Settings first.", "ELB DockTool — SMILES", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var f = new Form
        {
            Text = "ELB DockTool — SMILES Input",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(1040, 650),
            MinimumSize = new Size(920, 560),
            BackColor = Bg,
            ForeColor = TextColor
        };

        var title = new Label { Text = "SMILES INPUT", AutoSize = true, ForeColor = Purple, Font = new Font("Segoe UI", 16, FontStyle.Bold), Location = new Point(24, 18) };
        var hint = new Label { Text = "Enter and name your SMILES here, then add the selected entries to the Molecule Converter. Conversion options and output formats stay on the Converter page.", AutoSize = true, ForeColor = Muted, Location = new Point(24, 50) };
        var rows = new Panel { Location = new Point(24, 84), Size = new Size(970, 400), AutoScroll = true, BackColor = Color.FromArgb(8, 13, 22), BorderStyle = BorderStyle.FixedSingle };
        var header = new Label { Text = "ADD   NAME                                      SMILES", AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), Location = new Point(10, 8) };
        rows.Controls.Add(header);
        var entries = new List<(CheckBox Add, TextBox Name, TextBox Smiles)>();
        int rowY = 36;
        Action addRow = null!;
        addRow = () =>
        {
            var add = new CheckBox { Checked = true, AutoSize = true, Location = new Point(12, rowY + 7), ForeColor = TextColor };
            var name = new TextBox { Location = new Point(42, rowY), Width = 220, Height = 30, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "e.g. celecoxib" };
            var smi = new TextBox { Location = new Point(278, rowY), Width = 550, Height = 30, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Paste SMILES here" };
            var remove = MakeButton("REMOVE", 90, 30, Panel2, TextColor); remove.Location = new Point(842, rowY);
            remove.Click += (_, _) => { rows.Controls.Remove(add); rows.Controls.Remove(name); rows.Controls.Remove(smi); rows.Controls.Remove(remove); entries.RemoveAll(e => ReferenceEquals(e.Name, name)); ReflowSmilesRows(rows, entries); };
            rows.Controls.AddRange(new Control[] { add, name, smi, remove });
            entries.Add((add, name, smi));
            rowY += 44;
            ReflowSmilesRows(rows, entries);
        };
        addRow();

        var addMore = MakeButton("+ ADD SMILES", 130, 38, Panel2, TextColor); addMore.Location = new Point(24, 500); addMore.Click += (_, _) => addRow();
        var clear = MakeButton("CLEAR ROWS", 120, 38, Panel2, TextColor); clear.Location = new Point(166, 500); clear.Click += (_, _) => { foreach (Control c in rows.Controls.Cast<Control>().ToList()) if (c != header) rows.Controls.Remove(c); entries.Clear(); rowY = 36; addRow(); };
        var addToConverter = MakeButton("ADD SELECTED TO CONVERTER", 230, 42, Purple, Color.White); addToConverter.Location = new Point(740, 498);
        addToConverter.Click += (_, _) =>
        {
            if (!pages.TryGetValue("Molecule Converter", out var page))
            {
                MessageBox.Show("Molecule Converter page is not available.", "SMILES Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var list = page.Controls.Find("smilesConverterInputList", true).FirstOrDefault() as ListBox;
            if (list == null) { MessageBox.Show("Converter input list is not available.", "SMILES Input", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string staging = Path.Combine(ConvertFolder(), "SMILES_INPUT");
            Directory.CreateDirectory(staging);
            int added = 0;
            foreach (var e in entries.Where(e => e.Add.Checked && !string.IsNullOrWhiteSpace(e.Name.Text) && !string.IsNullOrWhiteSpace(e.Smiles.Text)))
            {
                string name = SafeName(e.Name.Text.Trim());
                string file = Path.Combine(staging, name + ".smi");
                File.WriteAllText(file, e.Smiles.Text.Trim() + " " + name + Environment.NewLine);
                if (!list.Items.Contains(file)) { list.Items.Add(file); added++; }
            }
            MessageBox.Show(added == 0 ? "No new named SMILES were added." : $"Added {added} SMILES entry/entries to the Molecule Converter.\n\nChoose the output format and preparation options there, then click CONVERT ALL.", "SMILES Input", MessageBoxButtons.OK, MessageBoxIcon.Information);
            f.Close();
        };
        var close = MakeButton("CLOSE", 100, 42, Panel2, TextColor); close.Location = new Point(630, 498); close.Click += (_, _) => f.Close();
        f.Controls.AddRange(new Control[] { title, hint, rows, addMore, clear, close, addToConverter });
        f.ShowDialog(this);
    }

    void ShowLigandSmilesPdbqtPanel(ListBox ligandList)
    {
        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            var detected = AppServices.DetectExecutable("obabel.exe");
            if (!string.IsNullOrWhiteSpace(detected)) { Config.OpenBabelPath = detected; try { AppServices.SaveConfig(Config); } catch { } }
        }
        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            MessageBox.Show("Open Babel (obabel.exe) was not found. Install Open Babel or set its path in Settings first.", "ELB DockTool — Ligand SMILES", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var f = new Form { Text = "ELB DockTool — Ligand SMILES → PDBQT", StartPosition = FormStartPosition.CenterParent, Size = new Size(1120, 760), MinimumSize = new Size(1000, 680), BackColor = Bg, ForeColor = TextColor };
        var title = new Label { Text = "LIGAND SMILES → PDBQT", AutoSize = true, ForeColor = Purple, Font = new Font("Segoe UI", 16, FontStyle.Bold), Location = new Point(24, 18) };
        var hint = new Label { Text = "Paste one ligand SMILES. ELB DockTool always creates a 3D SDF first, then converts the prepared structure to PDBQT for docking.", AutoSize = true, ForeColor = Muted, Location = new Point(24, 50) };
        var nameLabel = L("LIGAND NAME", 24, 92); var name = Box(300); name.Location = new Point(24, 118); name.PlaceholderText = "e.g. celecoxib";
        var smilesLabel = L("SMILES", 390, 92); var smi = Box(570); smi.Location = new Point(390, 118); smi.PlaceholderText = "Paste SMILES here";
        var prep = new GroupBox { Text = "Docking ligand preparation", ForeColor = TextColor, Location = new Point(24, 175), Size = new Size(1060, 255), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var threeD = new Label { Text = "✓ 3D SDF generation is compulsory", AutoSize = true, ForeColor = Green, Location = new Point(22, 30), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        var addH = new CheckBox { Text = "Add hydrogens", AutoSize = true, ForeColor = TextColor, Location = new Point(22, 78) };
        var ph = new CheckBox { Text = "Add H for pH", AutoSize = true, ForeColor = TextColor, Location = new Point(220, 78) };
        var phValue = new NumericUpDown { Minimum = 0, Maximum = 14, DecimalPlaces = 1, Increment = 0.1M, Value = 7.4M, Width = 70, Location = new Point(345, 75), Enabled = false, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor };
        ph.CheckedChanged += (_, _) => phValue.Enabled = ph.Checked;
        addH.CheckedChanged += (_, _) => { if (addH.Checked) ph.Checked = false; };
        ph.CheckedChanged += (_, _) => { if (ph.Checked) addH.Checked = false; };
        var canonical = new CheckBox { Text = "Canonicalize atom order", AutoSize = true, ForeColor = TextColor, Location = new Point(500, 78), Checked = true };
        var minimize = new CheckBox { Text = "Energy minimize", AutoSize = true, ForeColor = TextColor, Location = new Point(22, 130) };
        var ffLabel = L("Force field", 300, 133); var ff = new ComboBox { Location = new Point(400, 128), Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, Enabled = false };
        ff.Items.AddRange(new object[] { "MMFF94", "MMFF94s", "UFF", "GAFF", "Ghemical" }); ff.SelectedIndex = 0;
        var stepsLabel = L("Minimization steps", 590, 133); var steps = new NumericUpDown { Minimum = 1, Maximum = 10000, Value = 2500, Width = 105, Location = new Point(750, 128), BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, Enabled = false };
        minimize.CheckedChanged += (_, _) => { ff.Enabled = minimize.Checked; steps.Enabled = minimize.Checked; };
        prep.Controls.AddRange(new Control[] { threeD, addH, ph, phValue, canonical, minimize, ffLabel, ff, stepsLabel, steps });
        var status = new TextBox { Location = new Point(24, 450), Size = new Size(1060, 130), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, BackColor = Color.FromArgb(7, 12, 21), ForeColor = TextColor, Font = new Font("Consolas", 9F), WordWrap = false, Text = "Ready. Enter a ligand name and SMILES." };
        string? lastRunDir = null;
        var load = MakeButton("LOAD GENERATED PDBQT(S)", 240, 42, Panel2, TextColor); load.Location = new Point(24, 610); load.Enabled = false;
        load.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(lastRunDir) || !Directory.Exists(lastRunDir)) return;
            int added = 0;
            foreach (var file in Directory.EnumerateFiles(lastRunDir, "*.pdbqt", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!ValidateMoleculeFile(file, "ligand").Ok) continue;
                if (!ligandList.Items.Contains(file)) { ligandList.Items.Add(file); added++; }
            }
            status.Text += Environment.NewLine + $"Loaded {added} PDBQT file(s) into the Ligands panel.";
        };
        var close = MakeButton("CLOSE", 100, 42, Panel2, TextColor); close.Location = new Point(730, 610); close.Click += (_, _) => f.Close();
        var generate = MakeButton("GENERATE 3D SDF → PDBQT", 270, 42, Purple, Color.White); generate.Location = new Point(840, 610);
        generate.Click += async (_, _) =>
        {
            string ligandName = SafeName(name.Text.Trim()); string smilesText = smi.Text.Trim();
            if (string.IsNullOrWhiteSpace(name.Text.Trim())) { MessageBox.Show("Enter a ligand name.", "Ligand SMILES", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (string.IsNullOrWhiteSpace(smilesText)) { MessageBox.Show("Paste a SMILES string.", "Ligand SMILES", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            generate.Enabled = false; load.Enabled = false;
            try
            {
                string runDir = Path.Combine(ConvertFolder(), "Ligand_SMILES_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(runDir); lastRunDir = runDir;
                string sdf = Path.Combine(runDir, ligandName + ".sdf"); string pdbqt = Path.Combine(runDir, ligandName + ".pdbqt");
                string ops = " --gen3d";
                if (canonical.Checked) ops += " --canonical";
                if (ph.Checked) ops += " -p " + phValue.Value.ToString("0.0", CultureInfo.InvariantCulture); else if (addH.Checked) ops += " -h";
                if (minimize.Checked) ops += " --minimize --ff " + Q(ff.Text) + " --steps " + Decimal.ToInt32(steps.Value);
                status.Text = "Generating compulsory 3D SDF...";
                string smiInput = "-:\"" + smilesText.Replace("\"", "\\\"") + "\"";
                var toSdf = await RunProcess(AppServices.Resolve(Config.OpenBabelPath), $"{smiInput} -O {Q(sdf)}{ops}", AppServices.BaseDir);
                if (toSdf.ExitCode != 0 || !File.Exists(sdf) || new FileInfo(sdf).Length == 0) { string d = string.IsNullOrWhiteSpace(toSdf.Stderr) ? toSdf.Stdout : toSdf.Stderr; status.Text = "3D SDF generation failed." + Environment.NewLine + d.Trim(); return; }
                status.Text = "3D SDF created. Converting to PDBQT...";
                var toPdbqt = await RunProcess(AppServices.Resolve(Config.OpenBabelPath), $"{Q(sdf)} -o pdbqt -O {Q(pdbqt)}", AppServices.BaseDir);
                if (toPdbqt.ExitCode != 0 || !File.Exists(pdbqt) || new FileInfo(pdbqt).Length == 0 || !ValidateMoleculeFile(pdbqt, "ligand").Ok) { string d = string.IsNullOrWhiteSpace(toPdbqt.Stderr) ? toPdbqt.Stdout : toPdbqt.Stderr; status.Text = "PDBQT conversion failed." + Environment.NewLine + d.Trim(); return; }
                File.WriteAllText(Path.Combine(runDir, ligandName + "_preparation.log"), $"Name: {ligandName}{Environment.NewLine}SMILES: {smilesText}{Environment.NewLine}3D SDF: {sdf}{Environment.NewLine}PDBQT: {pdbqt}{Environment.NewLine}Canonicalize: {canonical.Checked}{Environment.NewLine}Add H: {addH.Checked}{Environment.NewLine}pH H: {(ph.Checked ? phValue.Value.ToString("0.0", CultureInfo.InvariantCulture) : "off")}{Environment.NewLine}Minimize: {minimize.Checked}{Environment.NewLine}Force field: {ff.Text}{Environment.NewLine}Steps: {steps.Value}");
                status.Text = $"Finished successfully.{Environment.NewLine}{Environment.NewLine}3D SDF: {sdf}{Environment.NewLine}PDBQT: {pdbqt}{Environment.NewLine}{Environment.NewLine}Click LOAD GENERATED PDBQT(S) to add it to the Ligands panel."; load.Enabled = true;
                MessageBox.Show("Ligand preparation finished. Click LOAD GENERATED PDBQT(S) to add it to the Ligands panel for docking.", "ELB DockTool — Ligand SMILES", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { status.Text = "Preparation failed: " + ex.Message; MessageBox.Show(status.Text, "ELB DockTool — Ligand SMILES", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { generate.Enabled = true; }
        };
        f.Controls.AddRange(new Control[] { title, hint, nameLabel, name, smilesLabel, smi, prep, status, load, close, generate }); f.ShowDialog(this);
    }

    void ReflowSmilesRows(Panel rows, List<(CheckBox Save, TextBox Name, TextBox Smiles)> entries)
    {
        int y = 36;
        foreach (var e in entries)
        {
            e.Save.Location = new Point(12, y + 7); e.Name.Location = new Point(42, y); e.Smiles.Location = new Point(270, y);
            var remove = rows.Controls.OfType<Button>().FirstOrDefault(b => b.Location.Y == e.Name.Location.Y && b.Text == "REMOVE");
            if (remove != null) remove.Location = new Point(842, y);
            y += 44;
        }
    }

    Label L(string text, int x, int y) => new() { Text = text, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Location = new Point(x, y) };

    async Task ConvertAll(ListBox list, ComboBox inputFmt, ComboBox outputFmt, string outDir)
    {
        if (!AppServices.Exists(Config.OpenBabelPath)) { MessageBox.Show("Open Babel is not configured. Set obabel.exe in Settings.", "Converter", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (list.Items.Count == 0) { MessageBox.Show("Upload at least one molecule file.", "Converter", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Directory.CreateDirectory(outDir);
        int done = 0;
        foreach (string input in list.Items)
        {
            string inf = inputFmt.SelectedIndex == 0 ? Path.GetExtension(input).TrimStart('.') : inputFmt.Text;
            string outf = outputFmt.Text; string outFile = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + "." + outf);
            string ops = "";
            if (converterGen3d?.Checked == true) ops += " --gen3d";
            if (converterCanonical?.Checked == true) ops += " --canonical";
            if (converterRemoveH?.Checked == true) ops += " -d";
            else if (converterPh?.Checked == true && converterPhValue != null) ops += " -p " + converterPhValue.Value.ToString("0.0", CultureInfo.InvariantCulture);
            else if (converterAddH?.Checked == true) ops += " -h";
            if (converterMinimize?.Checked == true && converterFf != null && converterSteps != null) ops += " --minimize --ff " + Q(converterFf.Text) + " --steps " + Decimal.ToInt32(converterSteps.Value);
            var r = await RunProcess(AppServices.Resolve(Config.OpenBabelPath), $"-i {inf} {Q(input)} -o {outf} -O {Q(outFile)}{ops}", AppServices.BaseDir);
            if (r.ExitCode == 0 && File.Exists(outFile)) done++;
        }
        MessageBox.Show($"Converted {done} of {list.Items.Count} file(s).\nOutput: {outDir}", "Converter", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    Panel BuildResults()
    {
        var p = PageBase();
        var card = Card("Docking Results", "Review valid docking outputs, open logs, split poses, or convert PDBQT results.");
        card.Height = 700;
        var actions = new FlowLayoutPanel { Location = new Point(22, 86), Size = new Size(1500, 82), AutoSize = false, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0) };
        var refresh = MakeButton("REFRESH RESULTS", 150, 38, Purple, Color.White); refresh.Click += (_, _) => RefreshResults();
        var open = MakeButton("OPEN OUTPUT FOLDER", 180, 38, Panel2, TextColor); open.Click += (_, _) => OpenFolder(AppServices.Resolve(Config.OutputFolder));
        var openLog = MakeButton("OPEN SELECTED LOG", 170, 38, Panel2, TextColor);
        var viewComplex = MakeButton("VIEW COMPLEX", 150, 38, Purple, Color.White);
        // wired after grid is created
        var splitAll = MakeButton("AUTO SPLIT ALL", 160, 38, Panel2, TextColor); splitAll.Click += async (_, _) => await AutoSplitAllResults();
        var toPdb = MakeButton("CONVERT ALL → PDB", 180, 38, Panel2, TextColor); toPdb.Click += async (_, _) => await ConvertResultsFormat("pdb");
        var toMol2 = MakeButton("CONVERT ALL → MOL2", 205, 38, Panel2, TextColor); toMol2.Click += async (_, _) => await ConvertResultsFormat("mol2");
        actions.Controls.AddRange(new Control[] { refresh, open, openLog, viewComplex, splitAll, toPdb, toMol2 });
        var grid = new DataGridView { Name = "resultsGrid", Location = new Point(22, 176), Size = new Size(1500, 430), BackgroundColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, GridColor = Border, BorderStyle = BorderStyle.FixedSingle, AllowUserToAddRows = false, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, EnableHeadersVisualStyles = false, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(9, 15, 25), ForeColor = TextColor, SelectionBackColor = Color.FromArgb(58, 30, 100), SelectionForeColor = Color.White, Font = new Font("Segoe UI", 9.5F), Alignment = DataGridViewContentAlignment.MiddleLeft };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(12, 19, 31), ForeColor = TextColor, SelectionBackColor = Color.FromArgb(58, 30, 100), SelectionForeColor = Color.White };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(25, 35, 52), ForeColor = TextColor, SelectionBackColor = Color.FromArgb(25, 35, 52), SelectionForeColor = TextColor, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };
        grid.Columns.Add("Receptor", "Receptor"); grid.Columns.Add("Ligand", "Ligand"); grid.Columns.Add("Best Score", "Best Score (kcal/mol)"); grid.Columns.Add("Output", "Output File"); grid.Columns.Add("Log", "Log");
        viewComplex.Click += (_, _) => OpenSelectedDockingComplex(grid);
        openLog.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) { MessageBox.Show("Select a docking result row first.", "Open Log", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (grid.SelectedRows[0].Cells.Count <= 4) { MessageBox.Show("The selected result row is incomplete.", "Open Log", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            OpenPath(grid.SelectedRows[0].Cells[4].Value?.ToString() ?? "");
        };
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count && grid.Rows[e.RowIndex].Cells.Count > 4)
                OpenPath(grid.Rows[e.RowIndex].Cells[4].Value?.ToString() ?? "");
        };
        card.Controls.AddRange(new Control[] { actions, grid }); p.Controls.Add(card); return p;
    }

    void OpenSelectedDockingComplex(DataGridView grid)
    {
        if (grid.SelectedRows.Count == 0)
        {
            MessageBox.Show("Select a docking result row first.", "Docking Interpretation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var row = grid.SelectedRows[0];
        string output = row.Cells[3].Value?.ToString() ?? "";
        string receptorName = row.Cells[0].Value?.ToString() ?? "";
        if (!File.Exists(output))
        {
            MessageBox.Show("The selected docking output file could not be found.", "Docking Interpretation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Prefer the original/prepared receptor PDB for interpretation because it
        // contains normal protein coordinates and preserves a clean cartoon/surface
        // representation. The corresponding PDBQT remains the docking input.
        string receptor = Directory.EnumerateFiles(ReceptorsFolder(), "*.pdb", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), receptorName, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(Path.GetFileNameWithoutExtension(f), receptorName + "_prepared", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (string.IsNullOrWhiteSpace(receptor))
            receptor = Directory.EnumerateFiles(ReceptorsFolder(), receptorName + "*.pdb", SearchOption.AllDirectories).FirstOrDefault() ?? "";

        // If no PDB is available, use the receptor PDBQT and let the interpretation
        // form convert it to a display PDB automatically with Open Babel.
        if (string.IsNullOrWhiteSpace(receptor))
            receptor = Directory.EnumerateFiles(ReceptorsFolder(), "*.pdbqt", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), receptorName, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(Path.GetFileNameWithoutExtension(f), receptorName + "_prepared", StringComparison.OrdinalIgnoreCase)) ?? "";
        if (string.IsNullOrWhiteSpace(receptor))
            receptor = Directory.EnumerateFiles(ReceptorsFolder(), "*.pdbqt", SearchOption.AllDirectories).FirstOrDefault(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), receptorName, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (!File.Exists(receptor))
        {
            MessageBox.Show($"Could not locate the receptor structure for '{receptorName}' in the project receptors folder.", "Docking Interpretation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var form = new DockingInterpretationWebForm(receptor, output);
        form.ShowDialog(this);
    }

    void RefreshResults()
    {
        if (!pages.TryGetValue("Results", out var page)) return;
        var grid = page.Controls.Find("resultsGrid", true).FirstOrDefault() as DataGridView; if (grid == null) return;
        grid.Rows.Clear();
        string root = AppServices.Resolve(Config.OutputFolder); if (!Directory.Exists(root)) return;
        foreach (var log in Directory.EnumerateFiles(root, "*_docking.log", SearchOption.AllDirectories))
        {
            if (log.Split(Path.DirectorySeparatorChar).Any(x => x.Equals("split", StringComparison.OrdinalIgnoreCase))) continue;
            string ligand = Path.GetFileNameWithoutExtension(log).Replace("_docking", "");
            string receptor = new DirectoryInfo(Path.GetDirectoryName(log)!).Name;
            string score = "—";
            try { var m = Regex.Match(File.ReadAllText(log), @"(?m)^\s*1\s+(-?\d+(?:\.\d+)?)"); if (m.Success) score = m.Groups[1].Value; } catch { }
            string output = Path.Combine(Path.GetDirectoryName(log)!, ligand + "_out.pdbqt");
            if (File.Exists(output) && HasVinaPoses(output)) grid.Rows.Add(receptor, ligand, score, output, log);
        }
    }

    async Task AutoSplitAllResults()
    {
        if (!AppServices.Exists(Config.VinaSplitPath))
        {
            MessageBox.Show("vina_split.exe is not configured or was not found. Set it in Settings first.", "Split Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string root = AppServices.Resolve(Config.OutputFolder);
        if (!Directory.Exists(root)) { MessageBox.Show("No output folder exists yet.", "Split Results", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var files = Directory.EnumerateFiles(root, "*_out.pdbqt", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(x => x.Equals("split", StringComparison.OrdinalIgnoreCase)))
            .Where(HasVinaPoses).ToList();
        if (files.Count == 0) { MessageBox.Show("No valid docking PDBQT result files were found.", "Split Results", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        int done = 0;
        var errors = new List<string>();
        foreach (var file in files)
        {
            var parent = Path.GetDirectoryName(file)!;
            var dir = Path.Combine(parent, "split");
            Directory.CreateDirectory(dir);
            foreach (var stale in Directory.EnumerateFiles(dir, "*_ligand_*.pdbqt")) { try { File.Delete(stale); } catch { } }
            var r = await RunProcess(AppServices.Resolve(Config.VinaSplitPath), $"--input {Q(file)}", dir);

            // Some vina_split builds honor the working directory; others write beside the input.
            // Normalize both behaviours into the requested split folder.
            var generated = Directory.EnumerateFiles(dir, "*_ligand_*.pdbqt").ToList();
            if (generated.Count == 0)
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                generated = Directory.EnumerateFiles(parent, stem + "_ligand_*.pdbqt").ToList();
                foreach (var g in generated)
                {
                    string target = Path.Combine(dir, Path.GetFileName(g));
                    try { if (File.Exists(target)) File.Delete(target); File.Move(g, target); } catch { }
                }
                generated = Directory.EnumerateFiles(dir, "*_ligand_*.pdbqt").ToList();
            }
            string splitLog = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "_split.log");
            try { File.WriteAllText(splitLog, $"Exit code: {r.ExitCode}{Environment.NewLine}STDOUT:{Environment.NewLine}{r.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{r.Stderr}"); } catch { }
            if (r.ExitCode == 0 && generated.Count > 0) done++;
            else errors.Add($"{Path.GetFileName(file)} — exit code {r.ExitCode}: {(string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }
        RefreshResults();
        if (errors.Count == 0)
            MessageBox.Show($"Split completed successfully for {done} of {files.Count} result file(s).\nSplit poses are inside each receptor's split folder.", "Split Results", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show($"Split completed for {done} of {files.Count} result file(s).\n\n{string.Join("\n", errors.Take(5))}", "Split Results — Check Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    static bool IsDockingResultPdbqt(string file, string root)
    {
        if (!File.Exists(file) || !string.Equals(Path.GetExtension(file), ".pdbqt", StringComparison.OrdinalIgnoreCase))
            return false;

        // Main Vina outputs contain MODEL/REMARK VINA RESULT records.
        if (HasVinaPoses(file)) return true;

        // vina_split creates individual pose files such as AAF_out_ligand_1.pdbqt.
        // These may not contain the original MODEL wrapper, so recognize them by
        // their split naming/location and require actual PDBQT atom records.
        string name = Path.GetFileName(file);
        bool isSplitPose =
            name.Contains("_out_ligand_", StringComparison.OrdinalIgnoreCase) ||
            file.Split(Path.DirectorySeparatorChar).Any(x => x.Equals("split", StringComparison.OrdinalIgnoreCase));

        if (!isSplitPose) return false;

        try
        {
            string text = File.ReadAllText(file);
            return CountPdbqtAtoms(text) > 0;
        }
        catch { return false; }
    }

    List<string> GetAllDockingResultPdbqtFiles(string root)
    {
        return Directory.EnumerateFiles(root, "*.pdbqt", SearchOption.AllDirectories)
            .Where(f => IsDockingResultPdbqt(f, root))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    async Task ConvertResultsFormat(string format)
    {
        if (!AppServices.Exists(Config.OpenBabelPath))
        {
            MessageBox.Show("Open Babel is not configured or was not found. Set/detect obabel.exe in Settings.", "Convert Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!string.Equals(format, "pdb", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(format, "mol2", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Unsupported conversion format.", "Convert Results", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string root = AppServices.Resolve(Config.OutputFolder);
        if (!Directory.Exists(root))
        {
            MessageBox.Show("No output folder exists yet.", "Convert Results", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // IMPORTANT: scan recursively. This includes the receptor's split folder
        // and any deeper subfolders. Only genuine docking-result PDBQT files are
        // sent to Open Babel; logs and unrelated PDBQT files are ignored.
        var files = GetAllDockingResultPdbqtFiles(root);
        if (files.Count == 0)
        {
            MessageBox.Show(
                "No valid docking PDBQT result files were found.\n\n" +
                "The scan includes both normal *_out.pdbqt files and poses created inside split folders.",
                "Convert Results",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        int done = 0;
        var errors = new List<string>();
        string target = format.ToUpperInvariant();

        foreach (var file in files)
        {
            string outFile = Path.Combine(
                Path.GetDirectoryName(file)!,
                Path.GetFileNameWithoutExtension(file) + "." + format);

            var r = await RunProcess(
                AppServices.Resolve(Config.OpenBabelPath),
                $"-ipdbqt {Q(file)} -o{format} -O {Q(outFile)}",
                AppServices.BaseDir);

            if (r.ExitCode == 0 && File.Exists(outFile) && new FileInfo(outFile).Length > 0)
            {
                done++;
            }
            else
            {
                string detail = (string.IsNullOrWhiteSpace(r.Stderr) ? r.Stdout : r.Stderr).Trim();
                errors.Add($"{file} — exit code {r.ExitCode}: {detail}");
            }
        }

        if (errors.Count == 0)
        {
            MessageBox.Show(
                $"Converted {done} of {files.Count} docking PDBQT file(s) to {target}.\n\n" +
                "This includes files inside split folders and any deeper subfolders.",
                "Convert Results",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(
                $"Converted {done} of {files.Count} file(s) to {target}.\n" +
                $"Failed: {errors.Count}\n\n" +
                string.Join("\n", errors.Take(8)),
                "Convert Results — Check Errors",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    Panel BuildSettings()
    {
        var p = PageBase();
        var card = Card("Settings", "Configure AutoDock Vina, Open Babel, and the Python/Meeko environment used for modern receptor preparation.");
        card.Height = 635;

        var vina = Box(620);
        var split = Box(620);
        var babel = Box(620);
        var meekoPython = Box(620);
        var output = Box(620);

        vina.Text = Config.VinaPath;
        split.Text = Config.VinaSplitPath;
        babel.Text = string.IsNullOrWhiteSpace(Config.OpenBabelPath)
            ? AppServices.DetectExecutable("obabel.exe")
            : Config.OpenBabelPath;
        meekoPython.Text = string.IsNullOrWhiteSpace(Config.MeekoPythonPath) ? "AUTO-DETECT (Python 3.10+ with Meeko)" : Config.MeekoPythonPath;
        output.Text = Config.OutputFolder;

        var boxes = new[] { vina, split, babel, meekoPython, output };
        var labels = new[] {
            "AutoDock Vina (vina.exe)",
            "vina_split.exe",
            "Open Babel (obabel.exe)",
            "Python interpreter for Meeko (python.exe)",
            "Default output folder"
        };

        int y = 76;
        for (int i = 0; i < boxes.Length; i++)
        {
            var label = L(labels[i], 22, y);
            boxes[i].Location = new Point(22, y + 23);
            boxes[i].Width = 620;
            card.Controls.Add(label);
            card.Controls.Add(boxes[i]);

            var browse = MakeButton(i == 0 || i == 1 ? "UPLOAD" :
                                    i == 2 ? "AUTO DETECT" :
                                    i == 3 ? "AUTO DETECT" : "BROWSE",
                                    125, 32, Panel2, TextColor);
            browse.Location = new Point(655, y + 23);
            int index = i;

            var clear = MakeButton("CLEAR", 80, 32, Panel2, TextColor);
            clear.Location = new Point(790, y + 23);
            clear.Click += (_, _) =>
            {
                boxes[index].Clear();
                if (index == 0) Config.VinaPath = "";
                else if (index == 1) Config.VinaSplitPath = "";
                else if (index == 2) Config.OpenBabelPath = "";
                else if (index == 3) Config.MeekoPythonPath = "";
                else if (index == 4) Config.OutputFolder = "";

                AppServices.SaveConfig(Config);
                _ = RefreshToolStatusAsync();
            };

            browse.Click += async (_, _) =>
            {
                if (index == 2)
                {
                    var detected = AppServices.DetectExecutable("obabel.exe");
                    if (!string.IsNullOrWhiteSpace(detected))
                        boxes[index].Text = detected;
                    else
                        MessageBox.Show("Open Babel (obabel.exe) was not found automatically. Install Open Babel or browse to obabel.exe.",
                            "Open Babel", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (index == 3)
                {
                    var detected = await ResolveMeekoPythonAsync();
                    if (!string.IsNullOrWhiteSpace(detected.Python))
                    {
                        boxes[index].Text = detected.Python;
                        Config.MeekoPythonPath = detected.Python;
                        AppServices.SaveConfig(Config);
                    }
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(detected.Python)
                            ? "No Python 3.10+ interpreter was found. Install Python 3.14 (64-bit), restart ELB, and use VERIFY PYTHON / MEEKO."
                            : $"Python detected: {detected.Python}\nVersion: {detected.Version}\nMeeko: {(detected.HasMeeko ? "installed" : "not installed")}\n\nIf Meeko is missing, use VERIFY PYTHON / MEEKO or start receptor preparation to see the complete installation and test procedure.",
                        "ELB DockTool — Python / Meeko",
                        MessageBoxButtons.OK,
                        string.IsNullOrWhiteSpace(detected.Python) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
                else if (index == 4)
                {
                    using var d = new FolderBrowserDialog();
                    if (d.ShowDialog() == DialogResult.OK)
                    {
                        boxes[index].Text = d.SelectedPath;
                        Config.OutputFolder = d.SelectedPath;
                        AppServices.SaveConfig(Config);
                        _ = RefreshToolStatusAsync();
                    }
                }
                else
                {
                    string fileName = index == 0 ? "vina.exe" : "vina_split.exe";
                    using var d = new OpenFileDialog
                    {
                        Title = $"Select {fileName}",
                        Filter = $"{fileName}|{fileName}|Executable|*.exe|All files|*.*",
                        CheckFileExists = true,
                        Multiselect = false
                    };
                    if (d.ShowDialog() == DialogResult.OK)
                    {
                        string selectedPath = Path.GetFullPath(d.FileName);
                        boxes[index].Text = selectedPath;
                        if (index == 0) Config.VinaPath = selectedPath;
                        else Config.VinaSplitPath = selectedPath;
                        AppServices.SaveConfig(Config);
                        _ = RefreshToolStatusAsync();
                    }
                }
            };

            card.Controls.Add(browse);
            card.Controls.Add(clear);
            y += 82;
        }

        var settingsActions = new FlowLayoutPanel { Location = new Point(22, 512), Size = new Size(930, 52), WrapContents = false, FlowDirection = FlowDirection.LeftToRight, AutoScroll = false, BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0) };
        var detect = MakeButton("DETECT OPEN BABEL", 180, 42, Panel2, TextColor);
        detect.Click += (_, _) =>
        {
            var detected = AppServices.DetectExecutable("obabel.exe");
            boxes[2].Text = detected;
            MessageBox.Show(string.IsNullOrWhiteSpace(detected)
                ? "Open Babel (obabel.exe) was not found."
                : $"Open Babel detected at:\n{detected}",
                "Open Babel", MessageBoxButtons.OK,
                string.IsNullOrWhiteSpace(detected) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        };

        var save = MakeButton("SAVE SETTINGS", 180, 42, Purple, Color.White);
        save.Click += (_, _) =>
        {
            Config.VinaPath = boxes[0].Text.Trim();
            Config.VinaSplitPath = boxes[1].Text.Trim();
            Config.OpenBabelPath = boxes[2].Text.Trim();
            Config.MeekoPythonPath = boxes[3].Text.Trim().Equals("AUTO-DETECT (Python 3.10+ with Meeko)", StringComparison.OrdinalIgnoreCase) ? "" : boxes[3].Text.Trim();
            Config.OutputFolder = boxes[4].Text.Trim();
            AppServices.SaveConfig(Config);
            _ = RefreshToolStatusAsync();
            MessageBox.Show("Settings saved.", "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var reset = MakeButton("USE DEFAULTS", 150, 42, Panel2, TextColor);
        reset.Click += (_, _) =>
        {
            Config = new AppConfig();
            var detected = AppServices.DetectExecutable("obabel.exe");
            if (!string.IsNullOrWhiteSpace(detected)) Config.OpenBabelPath = detected;
            Config.MeekoPythonPath = ""; // ELB will auto-detect a compatible Python 3.10+ environment.
            AppServices.SaveConfig(Config);
            ShowPage("Settings");
        };

        var verifyMeeko = MakeButton("VERIFY PYTHON / MEEKO", 290, 42, Panel2, TextColor);
        verifyMeeko.Click += async (_, _) =>
        {
            var detected = await ResolveMeekoPythonAsync();
            if (!string.IsNullOrWhiteSpace(detected.Python) && detected.HasMeeko)
            {
                boxes[3].Text = detected.Python;
                Config.MeekoPythonPath = detected.Python;
                AppServices.SaveConfig(Config);
                var prody = await RunProcess(detected.Python, "-c \"import prody; print(getattr(prody, '__version__', 'installed'))\"", AppServices.BaseDir);
                string prodyText = prody.ExitCode == 0 ? OutputOf(prody) : "NOT INSTALLED (needed for mmCIF input)";
                MessageBox.Show(
                    $"Python: {detected.Version}\nExecutable: {detected.Python}\nMeeko: available\nProDy: {prodyText}\n\nELB will use this interpreter for receptor preparation.",
                    "ELB DockTool — Meeko Check", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!string.IsNullOrWhiteSpace(detected.Python))
            {
                boxes[3].Text = detected.Python;
                Config.MeekoPythonPath = detected.Python;
                AppServices.SaveConfig(Config);
                string installTarget = string.IsNullOrWhiteSpace(detected.InstallPrefix) ? detected.Python : detected.InstallPrefix;
                string target = installTarget.StartsWith("py ", StringComparison.OrdinalIgnoreCase) || installTarget.Equals("python", StringComparison.OrdinalIgnoreCase)
                    ? installTarget
                    : Q(installTarget);
                MessageBox.Show(
                    $"Compatible Python found: {detected.Version}\nExecutable: {detected.Python}\nMeeko: NOT INSTALLED\n\n" +
                    "STEP 1 — VERIFY PYTHON\n" +
                    $"{target} --version\n\n" +
                    "STEP 2 — UPGRADE PIP\n" +
                    $"{target} -m pip install --upgrade pip\n" +
                    $"Test: {target} -m pip --version\n\n" +
                    "STEP 3 — INSTALL REQUIRED PACKAGES\n" +
                    $"{target} -m pip install numpy\n" +
                    $"Test: {target} -c \"import numpy; print(numpy.__version__)\"\n\n" +
                    $"{target} -m pip install scipy\n" +
                    $"Test: {target} -c \"import scipy; print(scipy.__version__)\"\n\n" +
                    $"{target} -m pip install rdkit\n" +
                    $"Test: {target} -c \"import rdkit; print(rdkit.__version__)\"\n\n" +
                    $"{target} -m pip install gemmi\n" +
                    $"Test: {target} -c \"import gemmi; print(gemmi.__version__)\"\n\n" +
                    $"{target} -m pip install tqdm\n" +
                    $"Test: {target} -c \"import tqdm; print(tqdm.__version__)\"\n\n" +
                    $"{target} -m pip install prody\n" +
                    $"Test: {target} -c \"import prody; print(prody.__version__)\"\n\n" +
                    $"{target} -m pip install meeko\n" +
                    $"Test: {target} -c \"import meeko; print(meeko.__version__)\"\n\n" +
                    "STEP 4 — VERIFY MEEKO RECEPTOR CLI\n" +
                    $"{target} -m meeko.cli.mk_prepare_receptor --help\n\n" +
                    "The Meeko help page should appear. Then click VERIFY PYTHON / MEEKO again.",
                    "ELB DockTool — Meeko Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show(
                    "No compatible Python 3.10+ interpreter was found.\n\n" +
                    "STEP 1 — VERIFY PYTHON\n" +
                    "python --version\n" +
                    "py --version\n" +
                    "py -0p\n\n" +
                    "STEP 2 — INSTALL PYTHON\n" +
                    "Install Python 3.14 (64-bit), then open a NEW Command Prompt.\n\n" +
                    "STEP 3 — VERIFY PYTHON 3.14\n" +
                    "py -3.14 --version\n\n" +
                    "STEP 4 — UPGRADE PIP\n" +
                    "py -3.14 -m pip install --upgrade pip\n" +
                    "Test: py -3.14 -m pip --version\n\n" +
                    "STEP 5 — INSTALL REQUIRED PACKAGES\n" +
                    "py -3.14 -m pip install numpy\n" +
                    "Test: py -3.14 -c \"import numpy; print(numpy.__version__)\"\n\n" +
                    "py -3.14 -m pip install scipy\n" +
                    "Test: py -3.14 -c \"import scipy; print(scipy.__version__)\"\n\n" +
                    "py -3.14 -m pip install rdkit\n" +
                    "Test: py -3.14 -c \"import rdkit; print(rdkit.__version__)\"\n\n" +
                    "py -3.14 -m pip install gemmi\n" +
                    "Test: py -3.14 -c \"import gemmi; print(gemmi.__version__)\"\n\n" +
                    "py -3.14 -m pip install tqdm\n" +
                    "Test: py -3.14 -c \"import tqdm; print(tqdm.__version__)\"\n\n" +
                    "py -3.14 -m pip install prody\n" +
                    "Test: py -3.14 -c \"import prody; print(prody.__version__)\"\n\n" +
                    "py -3.14 -m pip install meeko\n" +
                    "Test: py -3.14 -c \"import meeko; print(meeko.__version__)\"\n\n" +
                    "STEP 6 — VERIFY MEEKO RECEPTOR CLI\n" +
                    "py -3.14 -m meeko.cli.mk_prepare_receptor --help\n\n" +
                    "The Meeko help page should appear. Then click VERIFY PYTHON / MEEKO again.",
                    "ELB DockTool — Python / Meeko Setup Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        settingsActions.Controls.AddRange(new Control[] { detect, save, reset, verifyMeeko });
        card.Controls.Add(settingsActions);
        p.Controls.Add(card);

        var pdbCard = Card("PDBFixer / OpenMM", "Install, test, and manage PDBFixer/OpenMM.");
        pdbCard.Height = 355;
        var statusTitle = L("Status", 22, 72);
        var statusValue = new Label { Location = new Point(160, 70), Size = new Size(670, 24), ForeColor = pdbFixerStatus.IsUsable ? Green : (pdbFixerStatus.State == "ERROR" ? Red : Color.Gold), Font = new Font("Segoe UI", 10F, FontStyle.Bold), Text = pdbFixerStatus.State + " — " + pdbFixerStatus.Detail };
        pdbFixerStatusValue = statusValue;
        var pythonTitle = L("Python", 22, 108);
        var pythonValue = new Label { Location = new Point(160, 106), Size = new Size(670, 24), ForeColor = TextColor, Text = string.IsNullOrWhiteSpace(pdbFixerStatus.Python) ? "Checking…" : pdbFixerStatus.Python + "  " + pdbFixerStatus.PythonVersion };
        pdbFixerPythonValue = pythonValue;
        var versionsTitle = L("Versions", 22, 144);
        var versionsValue = new Label { Location = new Point(160, 142), Size = new Size(670, 24), ForeColor = TextColor, Text = pdbFixerStatus.IsUsable ? $"PDBFixer {pdbFixerStatus.PdbFixerVersion}   •   OpenMM {pdbFixerStatus.OpenMmVersion}" : (pdbFixerStatus.State == "CHECKING" ? "Checking PDBFixer/OpenMM…" : "PDBFixer/OpenMM not detected") };
        pdbFixerVersionsValue = versionsValue;
        var ownershipTitle = L("Installation", 22, 180);
        var ownershipValue = new Label { Location = new Point(160, 178), Size = new Size(670, 24), ForeColor = Muted, Text = pdbFixerStatus.IsManaged ? "ELB-managed isolated environment (safe to remove here)" : "External/global Python environment (protected from deletion)" };
        pdbFixerOwnershipValue = ownershipValue;
        UpdatePdbFixerStatusControls();

        var installPdb = MakeButton("AUTO DOWNLOAD", 220, 42, Purple, Color.White); installPdb.Location = new Point(22, 230);
        installPdb.Click += async (_, _) => await InstallPdbFixerAsync();
        var manualPdb = MakeButton("MANUAL INSTALL INSTRUCTIONS", 230, 42, Panel2, TextColor); manualPdb.Location = new Point(252, 230);
        manualPdb.Click += async (_, _) => { await RefreshPdbFixerStatusAsync(); ShowPdbFixerDetails("ELB DockTool — Manual PDBFixer setup", PdbFixerManualInstructions(pdbFixerStatus)); };
        var testPdb = MakeButton("TEST INSTALLATION", 175, 42, Panel2, TextColor); testPdb.Location = new Point(492, 230);
        testPdb.Click += async (_, _) => { await RefreshPdbFixerStatusAsync(); ShowPdbFixerDetails("ELB DockTool — PDBFixer test", $"Status: {pdbFixerStatus.State}\r\nPython: {pdbFixerStatus.Python}\r\nPython version: {pdbFixerStatus.PythonVersion}\r\nPDBFixer: {pdbFixerStatus.PdbFixerVersion}\r\nOpenMM: {pdbFixerStatus.OpenMmVersion}\r\nManaged by ELB: {pdbFixerStatus.IsManaged}\r\n\r\nDetails:\r\n{pdbFixerStatus.Detail}", pdbFixerStatus.IsUsable ? MessageBoxIcon.Information : MessageBoxIcon.Warning); await RefreshToolStatusAsync(); };
        var deletePdb = MakeButton("DELETE SOFTWARE", 160, 42, Color.FromArgb(104, 36, 47), Color.White); deletePdb.Location = new Point(677, 230);
        deletePdb.Click += async (_, _) => await DeleteManagedPdbFixerAsync();
        var note = MutedLabel("Delete Software removes only an ELB-created virtual environment. It will never delete a global Python installation."); note.Location = new Point(22, 294);
        pdbCard.Controls.AddRange(new Control[] { statusTitle, statusValue, pythonTitle, pythonValue, versionsTitle, versionsValue, ownershipTitle, ownershipValue, installPdb, manualPdb, testPdb, deletePdb, note });
        p.Controls.Add(pdbCard);
        return p;
    }

    async Task RefreshMeekoAvailabilityAsync()
    {
        try
        {
            var resolved = await ResolveMeekoPythonAsync();
            meekoAvailable = resolved.HasMeeko && !string.IsNullOrWhiteSpace(resolved.Python);
            meekoPythonDisplay = resolved.Python ?? "";
            if (meekoAvailable)
            {
                Config.MeekoPythonPath = resolved.Python;
                AppServices.SaveConfig(Config);
                if (receptorPreparationStatusLabel != null)
                {
                    receptorPreparationStatusLabel.Text = "READY — Meeko available";
                    receptorPreparationStatusLabel.ForeColor = Green;
                }
            }
            else
            {
                if (receptorPreparationStatusLabel != null)
                {
                    receptorPreparationStatusLabel.Text = "SETUP NEEDED — Meeko unavailable";
                    receptorPreparationStatusLabel.ForeColor = Color.Gold;
                }
            }
        }
        catch
        {
            meekoAvailable = false;
            meekoPythonDisplay = "";
            if (receptorPreparationStatusLabel != null)
            {
                receptorPreparationStatusLabel.Text = "SETUP NEEDED — Meeko unavailable";
                receptorPreparationStatusLabel.ForeColor = Color.Gold;
            }
        }
    }

    async Task RefreshToolStatusAsync()
    {
        await RefreshMeekoAvailabilityAsync();
        await RefreshPdbFixerStatusAsync();
        RefreshDashboardStatus();
        // PDBFixer is an optional repair stage; its absence must not block normal docking.
        bool all = ToolEntries().Where(x => !x.name.StartsWith("PDBFixer", StringComparison.OrdinalIgnoreCase)).All(x => x.ok);
        readyLabel.Text = all ? "● READY" : "● SETUP NEEDED";
        readyLabel.ForeColor = all ? Green : Color.Gold;
    }

    void RefreshDashboardStatus()
    {
        if (dashboardStatusFlow == null) return;
        dashboardStatusFlow.Controls.Clear();
        foreach (var (name, ok) in ToolEntries()) dashboardStatusFlow.Controls.Add(StatusChip(name, ok));
    }

    void ClearProjectData()
    {
        var answer = MessageBox.Show(
            "This will permanently delete all ELB DockTool project data: receptors, ligands, converter/minimization files, docking output/results, execution logs, and downloaded phytochemical-library structures.\n\nThe data folders will be recreated empty. Application settings, source files, assets, and Vina/Open Babel executables will NOT be deleted.\n\nContinue?",
            "Clear Project Data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;

        // Stop long-running IMPPAT operations before deleting their working files.
        try { phytochemicalFilterCts?.Cancel(); } catch { }
        try { phytochemicalSendAllCts?.Cancel(); } catch { }
        phytochemicalFilterCts?.Dispose(); phytochemicalFilterCts = null;
        phytochemicalSendAllCts?.Dispose(); phytochemicalSendAllCts = null;

        // Clear every active in-memory selection/list as well as the files on disk.
        dockingReceptorList?.Items.Clear();
        dockingLigandList?.Items.Clear();
        receptorGrids.Clear();
        phytochemicalCompounds.Clear();
        if (phytochemicalGrid != null) phytochemicalGrid.DataSource = null;
        if (vinaMonitorStatus != null) { vinaMonitorStatus.Text = "● READY — Vina has not been started"; vinaMonitorStatus.ForeColor = Green; }
        if (vinaMonitorJobs != null) vinaMonitorJobs.Text = "Jobs: 0 / 0";
        if (vinaMonitorLog != null) vinaMonitorLog.Text = "Vina output and errors will appear here while docking runs.";

        // These are data/workspace folders only. Never include dist, assets,
        // configuration files, or application binaries in CLEAR DATA.
        string[] folders = {
            Path.Combine(AppServices.BaseDir, "receptors"),
            Path.Combine(AppServices.BaseDir, "ligands"),
            Path.Combine(AppServices.BaseDir, "convert"),
            Path.Combine(AppServices.BaseDir, "output"),
            AppServices.Resolve(Config.OutputFolder),
            Path.Combine(AppServices.BaseDir, "phytochemical_library"),
            Path.Combine(AppServices.BaseDir, "logs"),
            Path.Combine(AppServices.BaseDir, "results"),
            Path.Combine(AppServices.BaseDir, "workspace"),
            Path.Combine(AppServices.BaseDir, "temp")
        };

        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
                    }
                    foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                                                  .OrderByDescending(x => x.Length))
                    {
                        try { Directory.Delete(dir, false); } catch { }
                    }
                }
                Directory.CreateDirectory(folder);
            }
            catch { }
        }

        MessageBox.Show("All project data was cleared. Receptors, ligands, converter/minimization files, docking output, logs, and phytochemical downloads are empty and ready for new work.",
            "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshDashboardStatus();
        RefreshResults();
    }

    static void OpenPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = Q(path) });
                return;
            }
            MessageBox.Show("The selected file or folder does not exist:\n\n" + path, "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { MessageBox.Show("Could not open:\n\n" + path + "\n\n" + ex.Message, "ELB DockTool", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static void OpenFolder(string path) => OpenPath(path);
}

public sealed class LogoPanel : Panel
{
    public LogoPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; var c = new Point(38, 37); using var p = new Pen(Color.FromArgb(145, 74, 255), 4); using var p2 = new Pen(Color.FromArgb(74, 205, 255), 2); var pts = new PointF[6]; for (int i = 0; i < 6; i++) { double a = Math.PI / 3 * i - Math.PI / 6; pts[i] = new PointF(c.X + (float)Math.Cos(a) * 27, c.Y + (float)Math.Sin(a) * 27); } e.Graphics.DrawPolygon(p, pts); for (int i = 0; i < 6; i++) e.Graphics.FillEllipse(Brushes.White, pts[i].X - 3, pts[i].Y - 3, 6, 6); e.Graphics.DrawLine(p2, pts[0], pts[2]); e.Graphics.DrawLine(p2, pts[2], pts[4]); e.Graphics.DrawLine(p2, pts[4], pts[0]);
    }
}

public sealed class ProgressForm : Form
{
    readonly Label status = new(); readonly ProgressBar bar = new(); public bool Cancelled { get; private set; }
    public ProgressForm(int total)
    {
        Text = "Docking in progress"; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(520, 150); BackColor = Color.FromArgb(15,23,36); ForeColor = Color.White;
        status.Text = "Preparing…"; status.Location = new Point(22,20); status.AutoSize = true; status.ForeColor = Color.White; bar.Location = new Point(22,58); bar.Size = new Size(476,22); bar.Maximum = Math.Max(1,total); var cancel = new Button { Text="Cancel", Location=new Point(400,98), Size=new Size(98,32) }; cancel.Click += (_,_) => Cancelled=true; Controls.AddRange(new Control[]{status,bar,cancel});
    }
    public void Step(string s) { if (InvokeRequired) { BeginInvoke(new Action(()=>Step(s))); return; } status.Text=s; if (bar.Value<bar.Maximum) bar.Value++; }
}



public static class PasswordService
{
    private static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ELB_DockTool");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "password.hash");
        }
    }

    // Preserves the existing v1.2.0 password on first run, then stores only a hash.
    public static bool Verify(string password)
    {
        try
        {
            if (!File.Exists(FilePath))
                return password == "ELBIN2026";

            byte[] stored = Convert.FromBase64String(File.ReadAllText(FilePath).Trim());
            byte[] actual = Hash(password);
            return CryptographicOperations.FixedTimeEquals(stored, actual);
        }
        catch { return false; }
    }

    public static void Set(string password)
    {
        File.WriteAllText(FilePath, Convert.ToBase64String(Hash(password)));
    }

    private static byte[] Hash(string password)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(password ?? ""));
    }
}

public sealed class LoginForm : Form
{
    readonly Color Bg = Color.FromArgb(4, 8, 17);
    readonly Color PanelBg = Color.FromArgb(12, 18, 31);
    readonly Color Purple = Color.FromArgb(145, 74, 255);
    readonly Color TextColor = Color.FromArgb(242, 245, 252);
    readonly Color Muted = Color.FromArgb(164, 175, 198);
    readonly TextBox password = new();
    readonly PictureBox photo = new();
    readonly Label state = new();

    public LoginForm()
    {
        Text = $"ELB DockTool {Program.AppVersion} — Secure Access";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(860, 500);
        BackColor = Bg;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;
        TrySetApplicationIcon();

        var left = new Panel { Location = new Point(30, 30), Size = new Size(350, 440), BackColor = PanelBg };
        left.Paint += (_, e) => DrawMoleculeBackground(e.Graphics, left.ClientRectangle);

        photo.Size = new Size(250, 315);
        photo.Location = new Point(50, 45);
        photo.SizeMode = PictureBoxSizeMode.Zoom;
        photo.BackColor = Color.FromArgb(7, 12, 21);
        var photoPath = Path.Combine(AppServices.BaseDir, "assets", "profile.jpg");
        try { if (File.Exists(photoPath)) using (var img = Image.FromFile(photoPath)) photo.Image = new Bitmap(img); } catch { }

        var loginLogo = new LogoPanel { Location = new Point(730, 45), Size = new Size(76, 76), BackColor = Color.Transparent };
        Controls.Add(loginLogo);

        var title = new Label { Text = "ELB DOCKTOOL", AutoSize = true, ForeColor = Purple,
            Font = new Font("Segoe UI", 21, FontStyle.Bold), Location = new Point(425, 62) };
        var sub = new Label { Text = "Computer-Aided Drug Design",
            AutoSize = true, ForeColor = TextColor, Font = new Font("Segoe UI", 10.5F),
            Location = new Point(427, 108) };

        var creator = new Label { Text = "Created by ELBIN ROSHAK R", AutoSize = false,
            Size = new Size(250, 30), TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Muted, Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Location = new Point(50, 375) };

        var passLabel = new Label { Text = "Password", AutoSize = true, ForeColor = Muted,
            Location = new Point(427, 180), Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };
        password.Location = new Point(427, 207);
        password.Size = new Size(365, 36);
        password.BackColor = Color.FromArgb(7, 12, 21);
        password.ForeColor = TextColor;
        password.BorderStyle = BorderStyle.FixedSingle;
        password.UseSystemPasswordChar = true;
        password.Font = new Font("Segoe UI", 11F);
        password.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Access(); };

        var access = new Button { Text = "ACCESS DOCKTOOL", Location = new Point(427, 260),
            Size = new Size(365, 46), BackColor = Purple, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand };

        var changePassword = new Button
        {
            Text = "CHANGE PASSWORD",
            Location = new Point(427, 360),
            Size = new Size(175, 34),
            BackColor = PanelBg,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        changePassword.Click += (_, _) => ChangePassword();

        var support = new Button
        {
            Text = "CONTACT SUPPORT",
            Location = new Point(617, 360),
            Size = new Size(175, 34),
            BackColor = PanelBg,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        support.Click += (_, _) => OpenSupportEmail();
        access.FlatAppearance.BorderSize = 0;
        access.Click += (_, _) => Access();

        state.Text = "Make your workflow easy";
        state.AutoSize = true;
        state.ForeColor = Purple;
        state.Location = new Point(427, 320);

        Controls.Add(left);
        left.Controls.Add(photo);
        left.Controls.Add(creator);
        Controls.Add(title);
        Controls.Add(sub);
        Controls.Add(passLabel);
        Controls.Add(password);
        Controls.Add(access);
        Controls.Add(changePassword);
        Controls.Add(support);
        Controls.Add(state);
        password.Select();
    }

    void TrySetApplicationIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppServices.BaseDir, "assets", "ELB_DockTool.ico");
            if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        }
        catch { }
    }

    void Access()
    {
        if (!PasswordService.Verify(password.Text))
        {
            state.Text = "Incorrect password";
            state.ForeColor = Color.FromArgb(255, 92, 108);
            password.SelectAll();
            password.Focus();
            return;
        }

        state.Text = "ACCESS GRANTED";
        state.ForeColor = Color.FromArgb(50, 220, 145);
        DialogResult = DialogResult.OK;
        Close();
    }

    void ChangePassword()
    {
        using var dlg = new Form
        {
            Text = "Change Password",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(430, 275),
            BackColor = PanelBg,
            Font = new Font("Segoe UI", 10F)
        };

        var currentLabel = new Label { Text = "Current password", AutoSize = true, ForeColor = Muted, Location = new Point(25, 25) };
        var current = new TextBox { Location = new Point(25, 50), Size = new Size(375, 34), UseSystemPasswordChar = true, BackColor = Bg, ForeColor = TextColor };

        var newLabel = new Label { Text = "New password", AutoSize = true, ForeColor = Muted, Location = new Point(25, 92) };
        var next = new TextBox { Location = new Point(25, 117), Size = new Size(375, 34), UseSystemPasswordChar = true, BackColor = Bg, ForeColor = TextColor };

        var confirmLabel = new Label { Text = "Confirm new password", AutoSize = true, ForeColor = Muted, Location = new Point(25, 159) };
        var confirm = new TextBox { Location = new Point(25, 184), Size = new Size(375, 34), UseSystemPasswordChar = true, BackColor = Bg, ForeColor = TextColor };

        var change = new Button { Text = "CHANGE PASSWORD", Location = new Point(210, 228), Size = new Size(190, 34), BackColor = Purple, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "CANCEL", Location = new Point(25, 228), Size = new Size(100, 34), BackColor = PanelBg, ForeColor = TextColor, FlatStyle = FlatStyle.Flat };

        change.Click += (_, _) =>
        {
            if (!PasswordService.Verify(current.Text))
            {
                MessageBox.Show(dlg, "Current password is incorrect.", "Password Change", MessageBoxButtons.OK, MessageBoxIcon.Error);
                current.Clear(); current.Focus(); return;
            }
            if (next.Text.Length < 6)
            {
                MessageBox.Show(dlg, "New password must contain at least 6 characters.", "Password Change", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                next.Focus(); return;
            }
            if (next.Text != confirm.Text)
            {
                MessageBox.Show(dlg, "New password and confirmation do not match.", "Password Change", MessageBoxButtons.OK, MessageBoxIcon.Error);
                confirm.Clear(); confirm.Focus(); return;
            }

            PasswordService.Set(next.Text);
            MessageBox.Show(dlg, "Password changed successfully.", "Password Change", MessageBoxButtons.OK, MessageBoxIcon.Information);
            dlg.Close();
        };

        cancel.Click += (_, _) => dlg.Close();
        dlg.Controls.AddRange(new Control[] { currentLabel, current, newLabel, next, confirmLabel, confirm, cancel, change });
        dlg.AcceptButton = change;
        dlg.CancelButton = cancel;
        dlg.ShowDialog(this);
    }


    void OpenSupportEmail()
    {
        const string supportEmail = "elbinroshak2003@gmail.com";

        using var dlg = new Form
        {
            Text = "ELB-DockTool Support",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(560, 430),
            BackColor = PanelBg,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 10F)
        };

        var title = new Label
        {
            Text = "Contact Support",
            AutoSize = true,
            Location = new Point(28, 24),
            ForeColor = TextColor,
            Font = new Font("Segoe UI Semibold", 16F)
        };

        var info = new Label
        {
            Text = "Write your problem, purchase/licensing question, or feature request below.",
            AutoSize = false,
            Location = new Point(28, 60),
            Size = new Size(500, 42),
            ForeColor = Muted
        };

        var toLabel = new Label
        {
            Text = "To",
            AutoSize = true,
            Location = new Point(28, 112),
            ForeColor = Muted
        };

        var emailBox = new TextBox
        {
            Text = supportEmail,
            ReadOnly = true,
            Location = new Point(28, 136),
            Size = new Size(500, 32),
            BackColor = Bg,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle
        };

        var messageLabel = new Label
        {
            Text = "Message",
            AutoSize = true,
            Location = new Point(28, 180),
            ForeColor = Muted
        };

        var messageBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Location = new Point(28, 204),
            Size = new Size(500, 135),
            BackColor = Bg,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle
        };

        var send = new Button
        {
            Text = "SEND",
            Location = new Point(388, 360),
            Size = new Size(140, 38),
            BackColor = Purple,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };

        var cancel = new Button
        {
            Text = "CANCEL",
            Location = new Point(278, 360),
            Size = new Size(100, 38),
            BackColor = PanelBg,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };

        send.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(messageBox.Text))
            {
                MessageBox.Show(
                    dlg,
                    "Please write your message before sending.",
                    "Support",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                messageBox.Focus();
                return;
            }

            string subjectText = $"ELB-DockTool Support - {Program.AppVersion}";
            string bodyText =
                $"ELB-DockTool Support Request\r\n\r\n" +
                $"Version: {Program.AppVersion}\r\n" +
                $"OS: {Environment.OSVersion}\r\n\r\n" +
                $"Message:\r\n{messageBox.Text}";

            string subject = Uri.EscapeDataString(subjectText);
            string body = Uri.EscapeDataString(bodyText);

            // Gmail web compose is used first because it works even when
            // Windows has no default desktop mail application configured.
            string gmailUrl =
                $"https://mail.google.com/mail/?view=cm&fs=1&to={supportEmail}" +
                $"&su={subject}&body={body}";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = gmailUrl,
                    UseShellExecute = true
                });
                dlg.Close();
            }
            catch
            {
                // Fallback to the Windows default mail client.
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"mailto:{supportEmail}?subject={subject}&body={body}",
                        UseShellExecute = true
                    });
                    dlg.Close();
                }
                catch
                {
                    MessageBox.Show(
                        dlg,
                        "Could not open Gmail or the default email application.",
                        "Support",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        };

        cancel.Click += (_, _) => dlg.Close();

        dlg.Controls.AddRange(new Control[]
        {
            title, info, toLabel, emailBox, messageLabel, messageBox, cancel, send
        });

        dlg.AcceptButton = send;
        dlg.CancelButton = cancel;
        dlg.ShowDialog(this);
    }


    static void DrawMoleculeBackground(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(72, 34, 126), 1.3f);
        for (int row = -1; row < 8; row++)
        {
            for (int col = -1; col < 7; col++)
            {
                float x = 25 + col * 62 + ((row & 1) == 0 ? 0 : 30);
                float y = 15 + row * 54;
                var pts = new PointF[6];
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 3 * i;
                    pts[i] = new PointF(x + (float)Math.Cos(a) * 24, y + (float)Math.Sin(a) * 24);
                }
                g.DrawPolygon(pen, pts);
            }
        }
    }
}

public sealed class WelcomeForm : Form
{
    readonly AnimationCanvas canvas;
    readonly System.Windows.Forms.Timer timer = new();
    readonly Stopwatch watch = Stopwatch.StartNew();

    public WelcomeForm()
    {
        Text = $"ELB DockTool {Program.AppVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(1100, 650);
        BackColor = Color.FromArgb(2, 5, 15);
        DoubleBuffered = true;
        ShowInTaskbar = false;
        try
        {
            string iconPath = Path.Combine(AppServices.BaseDir, "assets", "ELB_DockTool.ico");
            if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        }
        catch { }

        canvas = new AnimationCanvas();
        canvas.Dock = DockStyle.Fill;
        Controls.Add(canvas);

        timer.Interval = 16;
        timer.Tick += (_, _) =>
        {
            canvas.Time = watch.Elapsed.TotalSeconds;
            canvas.Invalidate();

            if (watch.Elapsed.TotalMilliseconds >= 5200)
            {
                timer.Stop();
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        Shown += (_, _) => { watch.Restart(); timer.Start(); };
    }
}


public sealed class AnimationCanvas : Panel
{
    public double Time { get; set; }

    // Intro specification:
    // Background #05020D, purple/cyan holographic emitter, 70 particles,
    // 800 ms entrance, 0.92 -> 1.0 scale, 4 px float, 3 s float cycle.
    readonly Random random = new(2026);
    readonly Particle[] particles = new Particle[70];

    struct Particle
    {
        public float X, Y, Size, SpeedY, SpeedX, Life, Phase;
        public int ColorIndex;
    }

    public AnimationCanvas()
    {
        BackColor = Color.FromArgb(5, 2, 13);
        DoubleBuffered = true;

        for (int i = 0; i < particles.Length; i++)
        {
            particles[i] = new Particle
            {
                X = (float)random.NextDouble(),
                Y = (float)random.NextDouble(),
                Size = 1.5f + (float)random.NextDouble() * 2.0f,
                SpeedY = -15f - (float)random.NextDouble() * 25f,
                SpeedX = -5f + (float)random.NextDouble() * 10f,
                Life = 2.5f + (float)random.NextDouble() * 1.5f,
                Phase = (float)random.NextDouble() * 6.28318f,
                ColorIndex = random.Next(3)
            };
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        float w = Math.Max(1, ClientSize.Width);
        float h = Math.Max(1, ClientSize.Height);
        float cx = w / 2f;
        float emitterY = h * 0.79f;

        // Exact background: #05020D.
        using (var bg = new SolidBrush(Color.FromArgb(5, 2, 13)))
            g.FillRectangle(bg, ClientRectangle);

        // Subtle radial purple atmosphere.
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(cx - 390, emitterY - 260, 780, 430);
            using var glow = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(68, 139, 92, 246),
                SurroundColors = new[] { Color.FromArgb(0, 5, 2, 13) }
            };
            g.FillPath(glow, path);
        }

        // Molecular/hexagonal background structure.
        using (var hexPen = new Pen(Color.FromArgb(24, 139, 92, 246), 1f))
        {
            for (int row = -1; row < 14; row++)
            for (int col = -1; col < 19; col++)
            {
                float x = 14 + col * 74 + ((row & 1) == 0 ? 0 : 37);
                float y = 12 + row * 50;
                var pts = new PointF[6];
                for (int k = 0; k < 6; k++)
                {
                    double a = Math.PI / 3 * k;
                    pts[k] = new PointF(
                        x + (float)Math.Cos(a) * 27,
                        y + (float)Math.Sin(a) * 27);
                }
                g.DrawPolygon(hexPen, pts);
            }
        }

        // 70 particles. Source colors: #8B5CF6, #38BDF8, #FFFFFF.
        Color[] particleColors =
        {
            Color.FromArgb(139, 92, 246),
            Color.FromArgb(56, 189, 248),
            Color.White
        };

        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i];

            // Convert JSON px/sec speeds to normalized screen movement.
            float x = p.X * w + (float)(Time * p.SpeedX);
            float y = p.Y * h + (float)(Time * p.SpeedY);

            x %= w; if (x < 0) x += w;
            y %= h; if (y < 0) y += h;

            float particlePulse = 0.45f + 0.55f *
                (float)(0.5 + 0.5 * Math.Sin(Time * 2.2 + p.Phase));
            int alpha = Math.Clamp((int)(70 + 175 * particlePulse), 0, 255);

            using var b = new SolidBrush(Color.FromArgb(alpha, particleColors[p.ColorIndex]));
            g.FillEllipse(b, x, y, p.Size, p.Size);
        }

        // 220 px high light beam, 260 px at top -> 50 px at bottom.
        float beamTop = emitterY - 220;
        using (var beamPath = new GraphicsPath())
        {
            beamPath.AddPolygon(new[]
            {
                new PointF(cx - 130, beamTop),
                new PointF(cx + 130, beamTop),
                new PointF(cx + 25, emitterY),
                new PointF(cx - 25, emitterY)
            });

            using var beamBrush = new LinearGradientBrush(
                new PointF(cx, beamTop),
                new PointF(cx, emitterY),
                Color.FromArgb(0, 168, 85, 247),
                Color.FromArgb(46, 168, 85, 247));

            g.FillPath(beamBrush, beamPath);
        }

        // Hologram emitter rings. Exact radii from the supplied specification.
        double pulse = 0.5 + 0.5 * Math.Sin(Time * Math.PI * 2 * 1.2);
        float pulseScale = 0.98f + 0.08f * (float)pulse;

        DrawGlowEllipse(g, cx, emitterY, 45 * pulseScale, 15 * pulseScale,
            Color.White, 3f, 12f);

        DrawGlowEllipse(g, cx, emitterY, 90 * pulseScale, 28 * pulseScale,
            Color.FromArgb(192, 132, 252), 2f, 8f);

        DrawGlowEllipse(g, cx, emitterY, 140 * pulseScale, 42 * pulseScale,
            Color.FromArgb(126, 34, 206), 2f, 6f);

        // Rotating dashed outer ring, 0.25 rotations/sec.
        float rotation = (float)(Time * 360.0 * 0.25);
        DrawRotatedDashedEllipse(g, cx, emitterY, 210 * pulseScale, 65 * pulseScale,
            Color.FromArgb(56, 189, 248), 1.5f, rotation);

        // Inner bright source / hotspot.
        float hotspotPulse = 1f + 0.08f * (float)Math.Sin(Time * Math.PI * 2 * 1.2);
        using (var hotGlow = new SolidBrush(Color.FromArgb(42, 168, 85, 247)))
            g.FillEllipse(hotGlow, cx - 34 * hotspotPulse, emitterY - 15 * hotspotPulse,
                68 * hotspotPulse, 30 * hotspotPulse);

        using (var hotCore = new SolidBrush(Color.FromArgb(245, 237, 233, 254)))
            g.FillEllipse(hotCore, cx - 6, emitterY - 6, 12, 12);

        // Entrance animation: 800 ms, scale 0.92 -> 1.0, cubic-bezier-like ease-out.
        double reveal = Math.Clamp(Time / 0.8, 0, 1);
        float eased = (float)(1 - Math.Pow(1 - reveal, 3));
        float entranceScale = 0.92f + 0.08f * eased;

        // 4 px vertical float over 3000 ms.
        float floatY = 4f * (float)Math.Sin(Time * Math.PI * 2 / 3.0);

        // Keep the three intro lines clearly separated vertically.
        // WELCOMES must sit below ELB-DOCK rather than overlapping it.
        float titleY = h * 0.14f + floatY;
        float subtitleY = h * 0.34f + floatY;
        float youY = h * 0.46f + floatY;

        // Scale text around its center.
        var state = g.Save();
        g.TranslateTransform(cx, h * 0.30f);
        g.ScaleTransform(entranceScale, entranceScale);
        g.TranslateTransform(-cx, -h * 0.30f);

        DrawGradientGlowText(g, "ELB-DOCK",
            GetIntroFont("Montserrat", 64f, FontStyle.Bold),
            cx, titleY, new[]
            {
                Color.White,
                Color.FromArgb(192, 132, 252),
                Color.FromArgb(126, 34, 206)
            },
            new[]
            {
                (10f, Color.FromArgb(204, 168, 85, 247)),
                (25f, Color.FromArgb(153, 147, 51, 234)),
                (50f, Color.FromArgb(102, 126, 34, 206))
            },
            eased);

        using (var subtitleFont = GetIntroFont("Montserrat", 20f, FontStyle.Bold))
        {
            DrawLetterSpacedGlowText(g, "WELCOMES", subtitleFont, cx, subtitleY,
                8f, Color.FromArgb(230, 233, 213, 255), 0.9f * eased);
        }

        DrawGlowText(g, "YOU",
            GetIntroFont("Montserrat", 72f, FontStyle.Bold),
            cx, youY, Color.White,
            new[]
            {
                (15f, Color.FromArgb(230, 255, 255, 255)),
                (35f, Color.FromArgb(230, 168, 85, 247)),
                (70f, Color.FromArgb(179, 126, 34, 206))
            }, eased);

        g.Restore(state);
    }

    static Font GetIntroFont(string preferred, float size, FontStyle style)
    {
        try
        {
            if (FontFamily.Families.Any(f =>
                string.Equals(f.Name, preferred, StringComparison.OrdinalIgnoreCase)))
                return new Font(preferred, size, style);
        }
        catch { }

        // Montserrat is used whenever installed; fallback keeps the intro from crashing.
        return new Font("Segoe UI", size, style);
    }

    static void DrawGlowEllipse(Graphics g, float cx, float cy,
        float rx, float ry, Color color, float lineWidth, float blur)
    {
        // Layered strokes approximate the requested CSS-style blur while staying
        // native WinForms and requiring no external assets.
        int layers = Math.Max(2, (int)(blur / 2));
        for (int i = layers; i >= 1; i--)
        {
            float expand = i * 1.5f;
            int alpha = Math.Clamp((int)(color.A * (0.08 + 0.10 * (layers - i))), 0, 255);
            using var pen = new Pen(Color.FromArgb(alpha, color), lineWidth + i * 0.65f);
            g.DrawEllipse(pen, cx - rx - expand, cy - ry - expand * 0.35f,
                2 * (rx + expand), 2 * (ry + expand * 0.35f));
        }

        using var core = new Pen(Color.FromArgb(220, color), lineWidth);
        g.DrawEllipse(core, cx - rx, cy - ry, 2 * rx, 2 * ry);
    }

    static void DrawRotatedDashedEllipse(Graphics g, float cx, float cy,
        float rx, float ry, Color color, float lineWidth, float rotation)
    {
        using var pen = new Pen(Color.FromArgb(215, color), lineWidth)
        {
            DashStyle = DashStyle.Custom,
            DashPattern = new[] { 10f, 15f }
        };

        var state = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(rotation);
        g.DrawEllipse(pen, -rx, -ry, 2 * rx, 2 * ry);
        g.Restore(state);
    }

    static void DrawGradientGlowText(Graphics g, string text, Font font, float cx,
        float y, Color[] gradientColors,
        (float blur, Color color)[] shadows, float opacity)
    {
        var size = g.MeasureString(text, font);
        float x = cx - size.Width / 2;

        foreach (var shadow in shadows)
        {
            int layers = Math.Max(2, (int)(shadow.blur / 3));
            for (int i = layers; i >= 1; i--)
            {
                float spread = i * 0.45f;
                int a = Math.Clamp((int)(shadow.color.A * opacity / (layers + 1)), 0, 255);
                using var b = new SolidBrush(Color.FromArgb(a, shadow.color));
                g.DrawString(text, font, b, x - spread, y - spread);
                g.DrawString(text, font, b, x + spread, y + spread);
            }
        }

        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style,
            g.DpiY * font.Size / 72f, new PointF(x, y), StringFormat.GenericDefault);

        if (gradientColors == null || gradientColors.Length == 0)
            gradientColors = new[] { Color.White, Color.White };
        else if (gradientColors.Length == 1)
            gradientColors = new[] { gradientColors[0], gradientColors[0] };

        using var brush = new LinearGradientBrush(
            new PointF(x, y),
            new PointF(x + size.Width, y + size.Height),
            gradientColors[0], gradientColors[^1]);

        var blend = new ColorBlend
        {
            Colors = gradientColors,
            Positions = gradientColors.Length == 3 ? new[] { 0f, 0.5f, 1f } :
                Enumerable.Range(0, gradientColors.Length)
                    .Select(i => (float)i / (gradientColors.Length - 1)).ToArray()
        };
        brush.InterpolationColors = blend;

        using var finalBrush = new SolidBrush(Color.FromArgb(
            Math.Clamp((int)(255 * opacity), 0, 255), Color.White));
        g.FillPath(brush, path);
    }

    static void DrawGlowText(Graphics g, string text, Font font, float cx, float y,
        Color color, (float blur, Color color)[] shadows, float opacity)
    {
        var size = g.MeasureString(text, font);
        float x = cx - size.Width / 2;

        foreach (var shadow in shadows)
        {
            int layers = Math.Max(2, (int)(shadow.blur / 3));
            for (int i = layers; i >= 1; i--)
            {
                float spread = i * 0.35f;
                int a = Math.Clamp((int)(shadow.color.A * opacity / (layers + 1)), 0, 255);
                using var b = new SolidBrush(Color.FromArgb(a, shadow.color));
                g.DrawString(text, font, b, x - spread, y - spread);
                g.DrawString(text, font, b, x + spread, y + spread);
            }
        }

        using var brush = new SolidBrush(Color.FromArgb(
            Math.Clamp((int)(255 * opacity), 0, 255), color));
        g.DrawString(text, font, brush, x, y);
    }

    static void DrawLetterSpacedGlowText(Graphics g, string text, Font font,
        float cx, float y, float letterSpacing, Color color, float opacity)
    {
        var widths = text.Select(c => g.MeasureString(c.ToString(), font).Width).ToArray();
        float total = widths.Sum() + letterSpacing * Math.Max(0, text.Length - 1);
        float x = cx - total / 2;

        using var brush = new SolidBrush(Color.FromArgb(
            Math.Clamp((int)(255 * opacity), 0, 255), color));

        for (int i = 0; i < text.Length; i++)
        {
            g.DrawString(text[i].ToString(), font, brush, x, y);
            x += widths[i] + letterSpacing;
        }
    }
}
