# ELB DockTool v1.2.0

Windows x64 CADD workflow based on AutoDock Vina.

## Build
1. Install the .NET 8 SDK.
2. Run `BUILD_EXE.bat`.
3. The self-contained executable is written to `publish\ELB_DockTool.exe`.

The bundled `dist\vina.exe` and `dist\vina_split.exe` are required by the existing ELB docking workflow.

## Receptor Preparation
The Molecular Docking page now begins with a dedicated Receptor Preparation stage. The inspector is placed completely on the right side.

- Select PDB or mmCIF.
- PDB inspector groups protein residues, HETATM records by residue/molecule, and water molecules separately.
- HETATM groups are not displayed atom-by-atom in the list.
- Selecting one HETATM group shows exact PDB X/Y/Z coordinates in Angstroms for every atom in that group; the centroid is shown only as a calculated summary and never replaces the individual coordinates.
- Multiple residues, HETATM groups, and waters can be selected across the three lists and removed from a working PDB copy.
- `SELECT ALL WATER` selects all detected water molecules.
- `DELETE SELECTED` never overwrites the original input PDB.
- `RESTORE ORIGINAL` returns to the original receptor.
- Meeko performs Vina receptor parameterization and Gasteiger charge computation.
- Open Babel is not silently substituted for receptor charge assignment.
- PDBQT output is validated before it is added to the existing Receptors panel.

### Meeko setup
ELB uses the Python interpreter configured in Settings.

Verify first:
```
python --version
py --version
py -0p
```

Recommended Python 3.14 (64-bit) setup:
```
py -3.14 --version
py -3.14 -m pip install --upgrade pip
Test: py -3.14 -m pip --version

py -3.14 -m pip install numpy
Test: py -3.14 -c "import numpy; print(numpy.__version__)"

py -3.14 -m pip install scipy
Test: py -3.14 -c "import scipy; print(scipy.__version__)"

py -3.14 -m pip install rdkit
Test: py -3.14 -c "import rdkit; print(rdkit.__version__)"

py -3.14 -m pip install gemmi
Test: py -3.14 -c "import gemmi; print(gemmi.__version__)"

py -3.14 -m pip install tqdm
Test: py -3.14 -c "import tqdm; print(tqdm.__version__)"

py -3.14 -m pip install prody
Test: py -3.14 -c "import prody; print(prody.__version__)"

py -3.14 -m pip install meeko
Test: py -3.14 -c "import meeko; print(meeko.__version__)"

py -3.14 -m meeko.cli.mk_prepare_receptor --help
```

`prody` is required when the receptor input is mmCIF. Meeko can prepare PDB input using its standard PDB reader without ProDy.

If `pip install prody` is not available for the selected Python version, use a compatible Python environment supported by the current ProDy release for mmCIF preparation rather than forcing an unsupported build.

The Settings page contains `VERIFY PYTHON / MEEKO`.


## Licensing
ELB proprietary components are covered by `LICENSE.txt` and `LICENSE_SCOPE.txt`.
Meeko remains a separate third-party LGPL-2.1 component under `THIRD-PARTY-NOTICES/Meeko/`.
IMPPAT remains an external database/resource subject to its own terms.


## Receptor Preparation / Python-Meeko setup
ELB DockTool automatically searches for a compatible Python 3.10+ interpreter and prefers a Windows `py -3.x` installation over a legacy `python` command that may resolve to Python 2.7. If Meeko is missing, the application shows the detected Python version and exact commands for installing `pip`, `meeko`, and optional `prody` for mmCIF input.

The receptor preparation command uses Meeko `mk_prepare_receptor` with `--compute_charges --charge_model gasteiger`. Meeko performs receptor perception and AutoDock atom typing. Polar hydrogens are represented in the resulting PDBQT using the AutoDock `HD` atom type; ELB records the count in the preparation log. The Vina/Meeko documentation describes receptor preparation and notes that missing hydrogens can be added during preparation, while some workflows use a prior hydrogen-optimization step.

Open Babel is not silently substituted for Meeko receptor parameterization or charge assignment.

## License scope and third-party software

**Important:** the ELB DockTool proprietary license applies only to the ELB
DockTool GUI/application components owned by the copyright holder. It does not
license, own, restrict, or replace the licenses of third-party tools, libraries,
databases, data, services, or executables used by the application.

In particular:

- **Meeko** is an independent third-party dependency licensed under **GNU LGPL
  v2.1**. ELB DockTool invokes the user-installed Meeko Python package for
  receptor/ligand preparation; Meeko is not part of the ELB proprietary license.
- **AutoDock Vina** is a separate third-party docking program under its own
  **Apache License 2.0** terms.
- **IMPPAT 2.0** is an independent phytochemical database/resource with its own
  data license and attribution requirements. ELB DockTool retrieves available
  information at runtime and does not claim ownership of the IMPPAT database.
- The adapted/reference **IMPPAT downloader** is separately attributed to
  **Sanjay Valliappan** and remains subject to its applicable MIT license.
- **Python, RDKit, ProDy, Open Babel**, and other external dependencies remain
  under their respective licenses.

See `LICENSE.txt`, `LICENSE_SCOPE.txt`, and `THIRD-PARTY-NOTICES/` for the
component-specific scope and notices.

## Receptor preparation robustness and inspector controls
- PDB/ENT receptor input is copied to a preparation workspace before Meeko is called; the original input is never overwritten.
- Protein `CONECT` records are filtered from the preparation copy when they reference standard `ATOM` records. This prevents stale/over-specified protein connectivity from being interpreted by Meeko as excess inter-residue bonds. `HETATM`-only connectivity records are retained where possible.
- The structure inspector lists residues, grouped HETATM entities, and grouped water molecules.
- Inspector lists use mouse-toggle selection: clicking a selected item again deselects it without requiring Ctrl.
- `SELECT ALL WATER` now explicitly selects every water molecule in the water panel.
- The visible charge note does not claim that the charge assignment is "computed by Meeko"; it simply identifies Gasteiger charge assignment for Vina.

## Live Meeko availability status
- The Receptor Preparation status shows `READY — Meeko available` only after ELB successfully detects a compatible Python 3.10+ interpreter and successfully imports/executes Meeko.
- If Python/Meeko is unavailable, the Receptor Preparation status shows `SETUP NEEDED — Meeko unavailable`; it never displays `READY` merely because a Python executable exists.
- The Dashboard `Tool Status` now includes a dedicated `Meeko` status chip. It is green only when Meeko is actually available and red when it is unavailable.
- The global header `READY` state now requires the same live Meeko availability check in addition to the other configured tool checks.
- Opening Molecular Docking or Settings refreshes the Meeko availability status.

### Dashboard Tool Status
The Tool Status row is responsive and displays AutoDock Vina, vina_split, Open Babel,
and Meeko without clipping the Meeko status on standard Windows window sizes. If the
available width is smaller than the combined status chips, the row can scroll

## PDBFixer / OpenMM dependency management

PDBFixer is an optional receptor-repair dependency. It remains separate from the
ELB executable and does not replace the existing Open Babel polar-hydrogen,
Meeko/Gasteiger, or `delete_bad_res` preparation stages.

The Dashboard displays `PDBFixer — INSTALLED`, `NOT INSTALLED`, or `ERROR` and
shows the detected PDBFixer version when available. It is informational: a
missing PDBFixer installation does not prevent normal docking.

Settings contains a dedicated **PDBFixer / OpenMM** section. It displays the
interpreter path and Python version, PDBFixer and OpenMM versions, diagnostic
status, and whether ELB owns the installation. **Auto Download / Install**
creates an isolated environment at `%APPDATA%\\ELB_DockTool\\PDBFixerEnv` using a
detected Python 3.10+ interpreter, then runs that environment's pip to install
the published `pdbfixer` and `openmm` packages. The result window preserves
installer output and can be copied.

If automatic installation fails, use **Manual Install Instructions**. It gives
an exact interpreter-based command such as:

```text
"C:\\path\\to\\python.exe" -m pip install pdbfixer openmm
```

Use `python -m ...` rather than relying on `pdbfixer.exe` on PATH. A pip warning
that `pdbfixer.exe` is not on PATH does not mean the packages failed to install.

**Delete Software** only removes `%APPDATA%\\ELB_DockTool\\PDBFixerEnv` when that
environment was created and recorded by ELB. An independently/global installed
copy is protected: the application shows a non-destructive manual uninstall
command and never deletes Python itself.

See `THIRD-PARTY-NOTICES/PDBFixer-OpenMM-NOTICE.txt` for attribution and license
information. These packages are not bundled with the ELB executable.

### Receptor repair and cleanup order

For PDB/ENT input, the Receptor Structure Inspector creates a working copy when
you delete selected residues, HETATM groups, waters, or chains. Optional
PDBFixer repair then uses that current working copy. PDBFixer is limited to
missing-heavy-atom repair, optional missing residues, optional nonstandard
replacement, and optional pH-based hydrogen addition. Heterogen/water choices
are deliberately not duplicated in the PDBFixer panel: use the Inspector for
selective deletion, or the subsequent cleanup checkboxes to remove all waters
and/or non-protein HETATM. Open Babel polar-H runs after cleanup, unless
PDBFixer hydrogen addition was selected; Meeko/Gasteiger and optional
`delete_bad_res` are always the final preparation stages.

### Download from RCSB Protein Data Bank

In **Molecular Docking → Receptor Preparation**, enter a four-character RCSB
PDB ID (for example `5KIR`) and select **DOWNLOAD PDB**. ELB retrieves
`https://files.rcsb.org/download/<PDB-ID>.pdb`, saves it under the project
workspace, and loads the downloaded structure into the Structure Inspector.
It can then follow the same inspection, repair, cleanup, and Meeko workflow as
a locally opened PDB file.
horizontally. Meeko status is based on the live Meeko availability check.

### Receptor Preparation UI text cleanup
Removed the stray/incomplete text immediately below the PDB/CIF input control.
It was not required for the receptor-preparation workflow and could overlap the
next row of controls. The main section description remains unchanged.

### Receptor Inspector UI
Simplified inspector headings: RESIDUES, HETATM GROUPS, WATER MOLECULES.
Added a dedicated CHAINS list showing protein chains (A, B, C, etc.). One or more
chains can be selected and removed with DELETE SELECTED; the original receptor
remains untouched and all changes apply to the working copy.

### Receptor cleanup defaults
Crystallographic water removal and non-protein HETATM removal are mandatory cleanup steps in the receptor-preparation workflow and are displayed as permanently enabled controls. The Dashboard workflow mirrors this sequence.
