# XamlRender

`XamlRender` is a WPF rendering utility for loading a XAML file from a real project and either:

- rendering it to a file
- opening it in a preview window

It is designed for cases where the XAML is not just loose markup, but belongs to a compiled WPF project with:

- `x:Class`
- code-behind
- merged resource dictionaries
- third-party control libraries
- app-level resources from `App.xaml`

## What It Supports

- Compiled WPF controls and views via `x:Class`
- Loose XAML when no `x:Class` is present
- Automatic project discovery by walking upward to find the owning `.csproj`
- Project build before rendering
- App resource initialization from `App.xaml`
- Dependency probing from the built output directory
- Diagnostic reporting for WPF/XAML warnings and errors
- Progress reporting during load/build/render

## Output Formats

- `.png`
  - most faithful output path
  - rendered from the final WPF visual tree
  - supersampled for easier inspection when zooming

- `.xps`
  - attempts vector-capable output
  - not fully faithful for all WPF visuals
  - some templated/font-based icons may disappear

- `.svg`
  - convenience format only
  - currently embeds a PNG snapshot inside an SVG container
  - not true vector export

## Usage

Render to a file:

```powershell
./XamlRender.exe input.xaml output.png
./XamlRender.exe input.xaml output.xps
./XamlRender.exe input.xaml output.svg
```

Open a preview window:

```powershell
./XamlRender.exe input.xaml --preview
```

## Console Output

The tool writes progress and diagnostics to `stderr`.

Status messages look like:

```text
[status] Loading 'D:\path\to\View.xaml'.
[status] Locating owning project.
[status] Building project 'D:\path\to\App.csproj'.
[status] Rendering PNG output.
[status] Done.
```

Diagnostics are formatted to be machine-friendly:

```text
D:\path\to\View.xaml(19,10): error: Cannot find resource named 'SomeKey'.
D:\path\to\View.xaml: warning: System.Windows.Data Error: 40 : BindingExpression path error ...
```

This format is intended to be usable later with editor integrations such as Neovim quickfix/jumplist workflows.

## How It Works

For compiled XAML, the renderer:

1. Reads `x:Class` from the input XAML.
2. Walks upward to find candidate `.csproj` files.
3. Chooses the owning project, preferring explicit inclusion of the XAML in the project file.
4. Builds the project with `dotnet build`.
5. Loads the built assembly into the default runtime context.
6. Registers the target output directory for dependency resolution.
7. Initializes `App.xaml` resources when available.
8. Instantiates the compiled root type.
9. Renders or previews the resulting `FrameworkElement`.

If the XAML has no `x:Class`, it falls back to loose XAML loading.

## Limitations

- True arbitrary WPF-to-SVG export is not implemented.
- XPS output is not guaranteed to preserve all WPF visuals.
- Some controls may depend on additional application startup logic beyond `App.xaml`.
- Project ownership detection is heuristic, though explicit project inclusion is preferred when available.
- Preview mode is one-shot only; there is no live reload yet.

## Development

Project file:

- [XamlRender.csproj](D:\source\repos\XamlRender\XamlRender\XamlRender.csproj)

Main implementation:

- [Program.cs](D:\source\repos\XamlRender\XamlRender\Program.cs)

Additional maintainer notes:

- [AGENTS.md](D:\source\repos\XamlRender\AGENTS.md)
