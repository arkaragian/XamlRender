# AGENTS.md

This repository contains a WPF renderer prototype that loads a `.xaml` file and exports it as an image or document.

## Current State

- Main implementation lives in `XamlRender/Program.cs`.
- Command-line parsing now uses `System.CommandLine` (`System.CommandLine` package version `2.0.0` in `XamlRender.csproj`).
- The renderer supports:
  - compiled WPF `UserControl` / `FrameworkElement` loading via `x:Class`
  - loose XAML loading when `x:Class` is absent
  - console diagnostics for WPF/XAML warnings and errors
  - output formats:
    - `.png`
    - `.xps`
    - `.svg`

## Important Behavior

- For compiled controls, the renderer:
  - reads `x:Class`
  - finds the owning `.csproj` by walking upward from the XAML path
  - builds that project with `dotnet build`
  - loads the built assembly into the default runtime context
  - registers the target output directory with `AppDomain.CurrentDomain.AssemblyResolve`
  - initializes app-level resources from `App.xaml` when available
  - instantiates the compiled root type and renders it

- `App.xaml` initialization is important. Without it, theme resources from the host WPF app may be missing.

- The current dependency resolution strategy intentionally avoids a private `AssemblyLoadContext`. WPF compiled XAML/BAML loading behaved better when assemblies were loaded into the default context.

- The renderer now attaches to selected `PresentationTraceSources` during load/render and writes diagnostics to `stderr`.
- Exception reporting is normalized into a quickfix-friendly format when possible:
  - `path(line,column): error: message`
  - `path: warning: message`
- When WPF does not provide file/line information, diagnostics fall back to the input XAML path.

- The CLI shape is currently:
  - `XamlRender.exe render <xaml> <output>`
  - `XamlRender.exe preview <xaml>`

## Output Notes

- `.png`
  - This is the most faithful output path.
  - Rendering uses the element's natural size.
  - Bitmap rendering is supersampled to make small controls easier to inspect when zooming.

- `.xps`
  - This is intended as vector-capable output.
  - It is not fully faithful for all WPF visuals.
  - `Wpf.Ui` `SymbolIcon` content was observed to disappear in XPS even though it rendered correctly in PNG.

- `.svg`
  - This is not true vector export.
  - The current implementation embeds a PNG snapshot inside an SVG container.
  - It exists as a convenience output format only.

## Known Constraints

- True arbitrary WPF-to-SVG export is not implemented and is not realistically available through built-in WPF APIs.
- XPS serialization can lose templated or font-based icon visuals.
- Some controls may still depend on application-specific startup logic beyond `App.xaml`.
- Project ownership detection is heuristic, though it prefers explicit inclusion in the `.csproj`.

## Guidance For Future Changes

- If rendering breaks for a real project, inspect:
  - assembly resolution
  - `App.xaml` resource initialization
  - control constructor side effects
  - template-time resources and third-party control libraries

- If output fidelity matters, prefer improving the `.png` path before expanding `.xps` or `.svg`.

- If you add new output formats, be explicit about whether they are:
  - true vector
  - serialized WPF document output
  - raster wrapped in another container

- Treat diagnostic output as part of the tool contract now. Future changes should preserve machine-readable `stderr` lines because the intended downstream use includes Neovim quickfix/jumplist integration.

- Treat the `System.CommandLine` command surface as part of the tool contract as well. If the CLI evolves, preserve backward compatibility unless there is a strong reason not to.

- If you expand diagnostics further, prefer:
  - stable one-line records
  - explicit severity
  - file/line/column when available
  - avoiding noisy informational trace output by default

- Keep changes in `Program.cs` pragmatic. This repo is currently a focused prototype, not a general rendering framework.
