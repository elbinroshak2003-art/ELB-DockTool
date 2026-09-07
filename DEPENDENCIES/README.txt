# ELB DockTool v1.2.0 — Python/Meeko Dependency Guide

The receptor/ligand preparation workflow can use an external Python
environment. ELB DockTool does not re-license Python or its packages.

Required/used components depend on the selected workflow and installed
versions. The main preparation stack includes Python 3.10+, Meeko and its
runtime dependencies. PDBFixer/OpenMM are used for optional receptor repair.

In ELB Settings:
1. Select or AUTO-DETECT a compatible Python interpreter.
2. Use VERIFY PYTHON / MEEKO to test the environment.
3. Use the PDBFixer/OpenMM controls to install or verify the ELB-managed
   isolated environment when required.

Install third-party software only from its official distribution channels.
Each third-party package remains under its own license.
