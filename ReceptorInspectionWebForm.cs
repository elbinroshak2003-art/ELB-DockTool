using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;

namespace ELB_DockTool;

internal sealed class ReceptorInspectionWebForm : Form
{
    readonly string receptorPath;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    string? sessionDir;

    public ReceptorInspectionWebForm(string receptorFile)
    {
        receptorPath = receptorFile;
        Text = $"ELB DockTool {Program.AppVersion} — Receptor Inspection View";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1250, 820);
        MinimumSize = new Size(900, 650);
        BackColor = Color.FromArgb(7, 16, 28);
        Controls.Add(web);
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => Cleanup();
    }

    async Task InitializeAsync()
    {
        try
        {
            if (!File.Exists(receptorPath))
                throw new FileNotFoundException("The current inspected receptor file could not be found.", receptorPath);

            string ext = Path.GetExtension(receptorPath);
            if (!(ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ent", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The receptor inspection viewer currently requires the inspected structure to be in PDB format.");

            sessionDir = Path.Combine(AppServices.BaseDir, "workspace", "receptor_preparation", "inspector_view", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            string pdb = await File.ReadAllTextAsync(receptorPath);
            string json = JsonSerializer.Serialize(pdb);
            await File.WriteAllTextAsync(Path.Combine(sessionDir, "session-data.js"), "window.ELB_RECEPTOR_PDB=" + json + ";");
            await File.WriteAllTextAsync(Path.Combine(sessionDir, "index.html"), BuildHtml());

            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "receptor-inspection.elb-docktool",
                sessionDir,
                CoreWebView2HostResourceAccessKind.Allow);
            web.Source = new Uri("https://receptor-inspection.elb-docktool/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Receptor Inspection View", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    static string BuildHtml() => @"<!doctype html>
<html><head><meta charset='utf-8'>
<script src='https://3Dmol.org/build/3Dmol-min.js' onload='window.ELB_3DMOL_READY=true' onerror='window.ELB_3DMOL_ERROR=true'></script>
<style>
html,body,#viewer{margin:0;width:100%;height:100%;overflow:hidden;background:#ffffff}
#toolbar{position:fixed;z-index:5;top:14px;left:14px;display:flex;gap:8px;align-items:center;background:rgba(8,16,28,.92);padding:9px 11px;border-radius:8px;color:#fff;font:14px Segoe UI,sans-serif;box-shadow:0 4px 18px rgba(0,0,0,.22)}
button{border:1px solid #46546a;background:#182437;color:#fff;padding:7px 12px;border-radius:6px;font-weight:600;cursor:pointer}button:hover{background:#263650}
select{background:#182437;color:#fff;border:1px solid #46546a;padding:7px;border-radius:6px}
#status{position:fixed;z-index:5;right:14px;top:14px;background:rgba(8,16,28,.88);color:#79f2b2;padding:7px 10px;border-radius:6px;font:13px Segoe UI,sans-serif}
</style></head><body>
<div id='viewer'></div>
<div id='toolbar'>
<button onclick='fit()'>FIT</button><button onclick='protein()'>PROTEIN</button>
<label>Colour <select onchange='setColour(this.value)'><option value='spectrum'>Spectrum</option><option value='uniform'>Uniform</option></select></label>
<button onclick='toggleBg()'>WHITE BG</button>
</div><div id='status'>Loading inspected receptor…</div>
<script src='session-data.js'></script>
<script>
let viewer, bgWhite=true;
function init(){
 if(window.ELB_3DMOL_ERROR || typeof $3Dmol==='undefined'){ document.getElementById('status').textContent='3D viewer could not be loaded. Check internet access and reopen View Protein.'; return; }
 viewer=$3Dmol.createViewer(document.getElementById('viewer'),{backgroundColor:'white',antialias:true});
 const pdb=window.ELB_RECEPTOR_PDB || '';
 const model=viewer.addModel(pdb,'pdb');
 if(!model || !model.selectedAtoms || model.selectedAtoms({}).length===0){ document.getElementById('status').textContent='No atoms found in the current inspected PDB.'; return; }
 viewer.setProjection('orthographic');
 protein();
 fit();
 viewer.render();
 document.getElementById('status').textContent='Current inspected structure';
}
function protein(){
 if(!viewer) return;
 viewer.setStyle({}, {cartoon:{hidden:true},stick:{hidden:true},sphere:{hidden:true},line:{hidden:true}});
 viewer.setStyle({hetflag:false}, {cartoon:{color:'spectrum',thickness:0.28,opacity:1}});
 viewer.setStyle({hetflag:true}, {stick:{radius:0.16,colorscheme:'Jmol',opacity:1}});
 viewer.render();
}
function setColour(v){
 if(!viewer) return;
 if(v==='spectrum') viewer.setStyle({hetflag:false}, {cartoon:{color:'spectrum',thickness:0.28,opacity:1}});
 else viewer.setStyle({hetflag:false}, {cartoon:{color:'#6f7f91',thickness:0.28,opacity:1}});
 viewer.setStyle({hetflag:true}, {stick:{radius:0.16,colorscheme:'Jmol',opacity:1}});
 viewer.render();
}
function fit(){
 if(!viewer) return;
 viewer.center({});
 viewer.zoomTo({hetflag:false});
 viewer.zoom(0.88);
 viewer.render();
}
function toggleBg(){bgWhite=!bgWhite;viewer.setBackgroundColor(bgWhite?'white':'#07101c');document.querySelector('#toolbar > button:last-of-type').textContent=bgWhite?'DARK BG':'WHITE BG';viewer.render();}
window.addEventListener('load',init);
</script></body></html>";

    void Cleanup()
    {
        try { if (!string.IsNullOrWhiteSpace(sessionDir) && Directory.Exists(sessionDir)) Directory.Delete(sessionDir, true); } catch { }
    }
}
