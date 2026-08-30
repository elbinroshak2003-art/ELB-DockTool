# ELB-DockTool

ELB-DockTool is a GUI-based molecular docking tool for simplifying AutoDock Vina workflows.

## Main Features

- Receptor PDBQT selection and saving
- Ligand selection and format conversion
- Energy minimization
- Docking grid setup with 3D visualization
- AutoDock Vina docking
- Live docking execution monitoring
- Docking result review
- PDB/MOL2 result conversion

## How to Use

1. **Start ELB-DockTool** and complete password verification.
2. **Molecular Docking → Receptors:** Select or load receptor PDBQT files.
3. **Grid Settings:** Set the docking box and use the 3D view to verify its position.
4. **Ligands:** Select ligand files or load converted PDBQT files.
5. Use **AUTO-CONVERT** or **ENERGY** when required.
6. Set **Vina Search** parameters such as exhaustiveness, modes, energy range and CPU.
7. Run docking and monitor the process in **Vina Execution Monitor**.
8. Open **Results** to view docking scores and output files.
9. Use **Auto Split**, **Convert → PDB**, or **Convert → MOL2** when required.

## Requirements

- AutoDock Vina
- vina_split
- Open Babel

## Important

Prepare receptors and ligands correctly before docking. Verify the docking grid around the biologically relevant binding site.

Docking scores are computational predictions and should not be considered experimental binding affinities.


