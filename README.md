# ELB DockTool v1.2.0

Windows desktop molecular docking/CADD application developed in C# using
.NET 8 and Windows Forms.

## Source repository

This package contains the source code and project files needed to build and
review ELB DockTool. Generated build output, IDE caches, backup files,
developer-only fix notes, and personal/sample workspace data are excluded.

## Main components

- Receptor and ligand preparation workflow
- PDB sanity/normalization → PDBFixer/OpenMM → cleanup → Meeko → receptor PDBQT
- AutoDock Vina docking and pose splitting
- IMPPAT 2.0 phytochemical retrieval
- Independent WebView2 + 3Dmol.js docking interpretation viewer
- Receptor inspection viewer
- Interaction and nearby-residue analysis
- PDB/MOL2/CSV/PNG export functions

The interpretation interface is independently authored for ELB DockTool and

## Build

Requirements:
- Windows
- .NET 8 SDK
- Windows desktop targeting support

Run `BUILD_EXE.bat`.

AutoDock Vina executables are external third-party software. The source
repository does not include the Vina binaries; place legally obtained
`vina.exe` and `vina_split.exe` in the location expected by the build script.

The application also uses external Python software for parts of receptor and
ligand preparation. See `DEPENDENCIES/` for the concise dependency guide.

## Web viewer

The interpretation viewer uses Microsoft WebView2 and 3Dmol.js. The current
HTML references 3Dmol.js from its official CDN, so internet access is required
for that rendering library unless the project is changed to use a local copy.

## License

See `LICENSE.txt` and `THIRD-PARTY-NOTICES.md`.
