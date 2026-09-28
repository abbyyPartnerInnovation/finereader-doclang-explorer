# FineReader .NET wrapper

Run ./tools/Setup-FineReader.ps1 from the new solution root. It copies only FREngine.DotNet.Interop.dll from the installed Windows x64 SDK into this directory. No old application files or credentials are used.

The wrapper and native runtime must have matching versions (12.8.2 or later). This proprietary binary is ignored by Git; use it and distribute it only under your ABBYY licence. The native runtime stays in its installed SDK directory.
