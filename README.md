# ELB-DockTool v1.2.0

ELB-DockTool is a Windows desktop application developed in C# using .NET 8 and Windows Forms for receptor preparation, ligand preparation, molecular docking with AutoDock Vina, and post-docking result interpretation.

## Download

Download the latest Windows x64 release from the
[ELB-DockTool Releases](https://github.com/elbinroshak2003-art/ELB-DockTool/releases) page.

Under **Assets**, download the Windows release ZIP:

**ELB-DockTool-v1.2.0-win-x64.zip**

Extract the ZIP and run:

**ELB_DockTool.exe**

### Installation

1. Download the Windows x64 release ZIP from the Releases page.
2. Extract the ZIP to a suitable folder.
3. Run `ELB_DockTool.exe`.
4. Open **Settings** and verify the required external tools.
5. Configure Open Babel if it is not automatically detected.
6. Verify Python/Meeko if receptor preparation is required.
7. Verify or install the PDBFixer/OpenMM environment when receptor repair is required.

No source-code or development files are required for normal use.

## Main workflow

1. Load or download a receptor structure (PDB/mmCIF where supported).
2. Inspect and, when desired, edit a working copy in the Receptor Structure Inspector.
3. Optionally run PDBFixer/OpenMM repair on the normalized working copy.
4. Perform the receptor cleanup steps required by the application.
5. Optionally add polar hydrogens with Open Babel.
6. Prepare the receptor PDBQT with Meeko, including Gasteiger charge assignment.
7. Add/prepare ligands and convert them to PDBQT.
8. Define a docking grid for the selected receptor.
9. Run AutoDock Vina.
10. Review docking results, split poses with `vina_split` when required, and convert result structures to PDB or MOL2.
11. Open a selected result with **VIEW COMPLEX** for 3D interpretation.

## Receptor preparation

PDBFixer repair is optional. The application includes controls for missing heavy atoms, missing residues, nonstandard residues, and missing hydrogens. It also provides conditional remedy popups for common downstream Meeko/RDKit preparation failures.

The receptor preparation workflow uses a temporary normalized/sanitized working copy. The original input and inspector-edited receptor are not modified by the PDBFixer repair stage.

## Phytochemical and ligand workflow

The application includes an IMPPAT 2.0 phytochemical retrieval workflow, a molecule/SMILES input workflow, ligand conversion and preparation, and compound-selection tools. The phytochemical interface can provide molecular structures and available molecular-property, drug-likeness, and ADMET-related information returned by the connected IMPPAT service.

## Docking interpretation viewer

The integrated interpretation viewer is independently authored and hosted in the WinForms application through Microsoft WebView2. It uses 3Dmol.js for molecular rendering.

Current interpretation features include:

- 3D protein cartoon representation
- Docked ligand sticks/spheres
- Protein-only and complex views
- Optional molecular surface
- White/dark background
- Rotate, pan, zoom, and fit controls
- Selected-pose or all-poses display
- Selected-ligand highlighting and per-pose ligand colouring
- Standard element-based (Jmol/CPK-style) ligand colouring option
- 4.0 Å strict, 5.0 Å standard, and 6.0 Å relaxed nearby-residue cutoff
- Nearby-residue table with residue, chain, minimum distance, and contact count
- Hydrogen-bond interaction lines and an H-bond interaction table
- Optional highlighting of receptor residues associated with detected H-bonds
- Vina affinity and reported RMSD lower/upper bounds for the selected pose
- CSV export of the displayed H-bond interaction data
- PNG export with user-selected DPI from 72 to 2400
- PDB complex export from the selected receptor/pose workflow

**There is no 2D interaction diagram in the current v1.2.0 viewer.** The previous 2D interaction-diagram interface was removed. The current viewer provides 3D interpretation, nearby-residue analysis, and hydrogen-bond interaction information.

## Results and pose handling

**VIEW COMPLEX** operates on the selected docking result. ELB-DockTool prefers display-friendly PDB receptor/pose structures and can use Open Babel or `vina_split` when conversion/splitting is required. Vina affinity and RMSD lower/upper-bound metadata are read from the docking result records.

## Remedy popups

The receptor-preparation interface provides user-facing remedies for several common failures, including:

- long missing-residue segments when missing-residue reconstruction is enabled;
- Meeko/RDKit valence failures associated with PDBFixer heavy-atom reconstruction;
- PDBFixer/Meeko compatibility failures in complex structures;
- Meeko residue-template matching failures.

The remedy dialogs use **CANCEL**, **DISABLE & CONTINUE**, and **OK** where applicable. The middle action can retry the current preparation with the recommended setting changed; **OK** applies the setting change and stops, requiring the user to click **PREPARE RECEPTOR** again.

## Technology

- C# / .NET 8
- Windows Forms
- Microsoft WebView2
- 3Dmol.js
- AutoDock Vina / `vina_split`
- Meeko
- PDBFixer / OpenMM
- Open Babel (optional workflow components)
- IMPPAT 2.0 integration

## Build

The project targets `net8.0-windows`, `win-x64`, and a self-contained, folder-based deployment (`PublishSingleFile=false`). Build on Windows with the .NET 8 SDK using `BUILD_EXE.bat`.

The current interpretation viewer loads 3Dmol.js from the official 3Dmol CDN. Internet access is therefore required for the viewer unless the HTML is changed to use a locally bundled 3Dmol.js copy.

## Third-party notices

See `THIRD-PARTY-NOTICES/` for the licenses and notices applicable to bundled or integrated third-party components.
