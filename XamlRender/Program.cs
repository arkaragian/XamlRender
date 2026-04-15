using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using System.Xaml;
using System.Xml;
using System.Xml.Linq;

internal static class Program {
    private static readonly HashSet<string> AssemblyProbeDirectories = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly string[] WpfDiagnosticSourceNames = [
        "DataBindingSource",
        "DependencyPropertySource",
        "MarkupSource",
        "ResourceDictionarySource",
    ];

    /// <summary>
    /// Entry point for the renderer executable.
    /// </summary>
    /// <param name="args">Command-line arguments containing the input XAML path and output path.</param>
    [STAThread]
    private static int Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("Usage: XamlRender.exe input.xaml output.png|output.xps|output.svg");
            return 1;
        }

        string xamlPath = Path.GetFullPath(args[0]);
        string outputPath = Path.GetFullPath(args[1]);

        using WpfDiagnosticsSession diagnostics = StartDiagnosticsSession(xamlPath);

        try {
            WriteStatus($"Loading '{xamlPath}'.");
            FrameworkElement element = LoadRootElement(xamlPath);
            WriteStatus($"Rendering '{outputPath}'.");
            Render(element, outputPath);
            WriteStatus("Done.");
            return 0;
        }
        catch (Exception exception) {
            diagnostics.ReportException(exception);
            return 1;
        }
    }

    /// <summary>
    /// Loads the root framework element from either loose XAML or a compiled WPF control.
    /// </summary>
    /// <param name="xamlPath">The absolute path to the input XAML file.</param>
    /// <returns>The loaded framework element ready for rendering.</returns>
    private static FrameworkElement LoadRootElement(string xamlPath) {
        string? xamlClassName = TryReadXamlClassName(xamlPath);

        if (string.IsNullOrWhiteSpace(xamlClassName)) {
            WriteStatus("No x:Class found. Using loose XAML loader.");
            return LoadLooseXaml(xamlPath);
        }

        WriteStatus($"Resolved x:Class '{xamlClassName}'.");
        WriteStatus("Locating owning project.");
        string? projectPath = FindOwningProject(xamlPath);
        if (projectPath is null) {
            throw new InvalidOperationException($"Unable to find a WPF project for '{xamlPath}'.");
        }

        WriteStatus($"Using project '{projectPath}'.");
        string assemblyPath = BuildProjectAndGetAssemblyPath(projectPath);
        InitializeApplicationResources(projectPath, assemblyPath);
        return InstantiateCompiledRoot(assemblyPath, xamlClassName);
    }

    /// <summary>
    /// Loads a XAML file directly through the loose-XAML reader pipeline.
    /// </summary>
    /// <param name="path">The path to the XAML file.</param>
    /// <returns>The root framework element created from the markup.</returns>
    private static FrameworkElement LoadLooseXaml(string path) {
        string xaml = File.ReadAllText(path);

        using StringReader stringReader = new(xaml);
        using XmlReader xmlReader = XmlReader.Create(stringReader);

        object root = System.Windows.Markup.XamlReader.Load(xmlReader);

        if (root is not FrameworkElement element) {
            throw new InvalidOperationException("XAML root must be a FrameworkElement.");
        }

        return element;
    }

    /// <summary>
    /// Reads the <c>x:Class</c> value from a XAML root element when present.
    /// </summary>
    /// <param name="xamlPath">The path to the XAML file.</param>
    /// <returns>The fully qualified CLR type name declared by <c>x:Class</c>, or <see langword="null"/>.</returns>
    private static string? TryReadXamlClassName(string xamlPath) {
        using FileStream stream = File.OpenRead(xamlPath);
        XDocument document = XDocument.Load(stream);

        XAttribute? classAttribute = document.Root?.Attributes().FirstOrDefault(
            attribute =>
                attribute.Name.LocalName == "Class"
                && attribute.Name.NamespaceName == XamlLanguage.Xaml2006Namespace
        );

        return classAttribute?.Value;
    }

    /// <summary>
    /// Walks upward from the XAML location to find the project that most likely owns the file.
    /// </summary>
    /// <param name="xamlPath">The path to the XAML file.</param>
    /// <returns>The matching project path, or <see langword="null"/> if none is found.</returns>
    private static string? FindOwningProject(string xamlPath) {
        string xamlDirectory = Path.GetDirectoryName(xamlPath)
            ?? throw new InvalidOperationException("XAML path has no parent directory.");

        DirectoryInfo? current = new(xamlDirectory);
        List<string> candidates = [];

        while (current is not null) {
            candidates.AddRange(
                Directory.EnumerateFiles(current.FullName, "*.csproj", SearchOption.TopDirectoryOnly)
            );
            current = current.Parent;
        }

        string normalizedXamlPath = Path.GetFullPath(xamlPath);

        string? explicitOwner = candidates.FirstOrDefault(
            projectPath => ProjectExplicitlyIncludesXaml(projectPath, normalizedXamlPath)
        );

        if (explicitOwner is not null) {
            return explicitOwner;
        }

        return candidates
            .Where(projectPath => IsUnderProjectDirectory(projectPath, normalizedXamlPath))
            .OrderBy(projectPath => projectPath.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Checks whether a project file explicitly includes the given XAML file in its item list.
    /// </summary>
    /// <param name="projectPath">The path to the project file.</param>
    /// <param name="xamlPath">The normalized XAML file path to match.</param>
    /// <returns><see langword="true"/> when the project explicitly includes the XAML file; otherwise <see langword="false"/>.</returns>
    private static bool ProjectExplicitlyIncludesXaml(string projectPath, string xamlPath) {
        XDocument document = XDocument.Load(projectPath);
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("Project path has no parent directory.");

        IEnumerable<XElement> items = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName is "Page" or "ApplicationDefinition" or "Resource"
            );

        foreach (XElement item in items) {
            XAttribute? includeAttribute = item.Attribute("Include");
            if (includeAttribute is null || string.IsNullOrWhiteSpace(includeAttribute.Value)) {
                continue;
            }

            string candidatePath = Path.GetFullPath(
                Path.Combine(projectDirectory, includeAttribute.Value)
            );

            if (PathsEqual(candidatePath, xamlPath)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a file resides under the directory that contains a project file.
    /// </summary>
    /// <param name="projectPath">The path to the project file.</param>
    /// <param name="filePath">The file path to test.</param>
    /// <returns><see langword="true"/> when the file is located under the project directory; otherwise <see langword="false"/>.</returns>
    private static bool IsUnderProjectDirectory(string projectPath, string filePath) {
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("Project path has no parent directory.");
        string normalizedProjectDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(projectDirectory)
        );
        string normalizedFilePath = Path.GetFullPath(filePath);

        return normalizedFilePath.StartsWith(
            normalizedProjectDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Compares two paths after normalizing them to absolute form.
    /// </summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns><see langword="true"/> when both paths refer to the same location; otherwise <see langword="false"/>.</returns>
    private static bool PathsEqual(string left, string right) {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Builds the owning project and returns the expected path of its primary output assembly.
    /// </summary>
    /// <param name="projectPath">The path to the project file to build.</param>
    /// <returns>The absolute path to the built assembly.</returns>
    private static string BuildProjectAndGetAssemblyPath(string projectPath) {
        WriteStatus($"Building project '{projectPath}'.");
        RunDotnet($"build \"{projectPath}\" -c Debug -nologo");

        XDocument document = XDocument.Load(projectPath);
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("Project path has no parent directory.");

        string targetFramework =
            document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "TargetFramework")
                ?.Value
            ?? throw new InvalidOperationException(
                $"Could not determine TargetFramework for '{projectPath}'."
            );

        string assemblyName =
            document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")
                ?.Value
            ?? Path.GetFileNameWithoutExtension(projectPath);

        string assemblyPath = Path.Combine(
            projectDirectory,
            "bin",
            "Debug",
            targetFramework,
            $"{assemblyName}.dll"
        );

        if (!File.Exists(assemblyPath)) {
            throw new FileNotFoundException(
                $"Built assembly was not found at '{assemblyPath}'.",
                assemblyPath
            );
        }

        WriteStatus($"Build output '{assemblyPath}'.");
        return assemblyPath;
    }

    /// <summary>
    /// Initializes application-level resources from the owning WPF project before control rendering begins.
    /// </summary>
    /// <param name="projectPath">The path to the owning project file.</param>
    /// <param name="assemblyPath">The path to the built assembly.</param>
    private static void InitializeApplicationResources(string projectPath, string assemblyPath) {
        WriteStatus("Initializing application resources.");
        RegisterAssemblyProbeDirectory(assemblyPath);
        Assembly assembly = LoadAssemblyIntoDefaultContext(assemblyPath);
        string? appXamlPath = FindApplicationDefinitionPath(projectPath);

        if (appXamlPath is not null) {
            WriteStatus($"Found application definition '{appXamlPath}'.");
            string? appClassName = TryReadXamlClassName(appXamlPath);
            if (!string.IsNullOrWhiteSpace(appClassName)) {
                Type? appType = assembly.GetType(appClassName, throwOnError: false, ignoreCase: false);
                if (appType is not null && typeof(Application).IsAssignableFrom(appType)) {
                    WriteStatus($"Initializing application type '{appType.FullName}'.");
                    Application application = EnsureApplicationInstance(appType);
                    InvokeInitializeComponentIfPresent(application);
                    return;
                }
            }
        }

        WriteStatus("No compiled application type found. Using default Application instance.");
        _ = Application.Current ?? new Application();
    }

    /// <summary>
    /// Finds the project application definition file when one is declared.
    /// </summary>
    /// <param name="projectPath">The path to the project file.</param>
    /// <returns>The application definition XAML path, or <see langword="null"/> if none is found.</returns>
    private static string? FindApplicationDefinitionPath(string projectPath) {
        XDocument document = XDocument.Load(projectPath);
        string projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("Project path has no parent directory.");

        XElement? applicationDefinition = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "ApplicationDefinition");

        string? includePath = applicationDefinition?.Attribute("Include")?.Value;
        if (!string.IsNullOrWhiteSpace(includePath)) {
            string explicitPath = Path.GetFullPath(Path.Combine(projectDirectory, includePath));
            if (File.Exists(explicitPath)) {
                return explicitPath;
            }
        }

        string conventionalPath = Path.Combine(projectDirectory, "App.xaml");
        return File.Exists(conventionalPath) ? conventionalPath : null;
    }

    /// <summary>
    /// Loads the compiled assembly and creates an instance of the XAML root type.
    /// </summary>
    /// <param name="assemblyPath">The path to the built assembly.</param>
    /// <param name="xamlClassName">The fully qualified root type name from <c>x:Class</c>.</param>
    /// <returns>The instantiated framework element.</returns>
    private static FrameworkElement InstantiateCompiledRoot(string assemblyPath, string xamlClassName) {
        WriteStatus($"Instantiating '{xamlClassName}'.");
        RegisterAssemblyProbeDirectory(assemblyPath);
        Assembly assembly = LoadAssemblyIntoDefaultContext(assemblyPath);
        Type? rootType = assembly.GetType(xamlClassName, throwOnError: false, ignoreCase: false);

        if (rootType is null) {
            throw new InvalidOperationException(
                $"Type '{xamlClassName}' was not found in assembly '{assembly.FullName}'."
            );
        }

        object? instance = Activator.CreateInstance(rootType);
        if (instance is not FrameworkElement element) {
            throw new InvalidOperationException(
                $"Type '{xamlClassName}' does not inherit from FrameworkElement."
            );
        }

        return element;
    }

    /// <summary>
    /// Returns the current application instance or creates one from the provided application type.
    /// </summary>
    /// <param name="appType">The compiled application type.</param>
    /// <returns>The active application instance.</returns>
    private static Application EnsureApplicationInstance(Type appType) {
        if (Application.Current is Application currentApplication) {
            if (!appType.IsInstanceOfType(currentApplication)) {
                throw new InvalidOperationException(
                    $"A different application instance is already active: '{currentApplication.GetType().FullName}'."
                );
            }

            return currentApplication;
        }

        object? instance = Activator.CreateInstance(appType);
        if (instance is not Application application) {
            throw new InvalidOperationException(
                $"Type '{appType.FullName}' does not inherit from Application."
            );
        }

        return application;
    }

    /// <summary>
    /// Calls a generated or user-defined <c>InitializeComponent</c> method when the instance exposes one.
    /// </summary>
    /// <param name="instance">The object whose initialization method should be invoked.</param>
    private static void InvokeInitializeComponentIfPresent(object instance) {
        MethodInfo? initializeComponent = instance
            .GetType()
            .GetMethod(
                "InitializeComponent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null
            );

        initializeComponent?.Invoke(instance, parameters: null);
    }

    /// <summary>
    /// Registers the target assembly output directory so runtime assembly resolution can probe it.
    /// </summary>
    /// <param name="assemblyPath">The path to the built assembly.</param>
    private static void RegisterAssemblyProbeDirectory(string assemblyPath) {
        string assemblyDirectory = Path.GetDirectoryName(assemblyPath)
            ?? throw new InvalidOperationException("Assembly path has no parent directory.");

        if (AssemblyProbeDirectories.Count == 0) {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssemblyFromProbeDirectories;
        }

        AssemblyProbeDirectories.Add(assemblyDirectory);
    }

    /// <summary>
    /// Loads an assembly into the default runtime context, reusing an existing load when possible.
    /// </summary>
    /// <param name="assemblyPath">The path to the assembly file.</param>
    /// <returns>The loaded assembly.</returns>
    private static Assembly LoadAssemblyIntoDefaultContext(string assemblyPath) {
        string normalizedAssemblyPath = Path.GetFullPath(assemblyPath);

        Assembly? existingAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly =>
                !string.IsNullOrWhiteSpace(assembly.Location)
                && PathsEqual(assembly.Location, normalizedAssemblyPath)
            );

        if (existingAssembly is not null) {
            return existingAssembly;
        }

        return Assembly.LoadFrom(normalizedAssemblyPath);
    }

    /// <summary>
    /// Resolves a missing assembly by probing the registered target output directories.
    /// </summary>
    /// <param name="sender">The current application domain.</param>
    /// <param name="args">The resolution request details.</param>
    /// <returns>The loaded assembly, or <see langword="null"/> when the assembly cannot be resolved.</returns>
    private static Assembly? ResolveAssemblyFromProbeDirectories(object? sender, ResolveEventArgs args) {
        AssemblyName requestedAssemblyName = new AssemblyName(args.Name);

        Assembly? alreadyLoaded = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly =>
                AssemblyName.ReferenceMatchesDefinition(
                    assembly.GetName(),
                    requestedAssemblyName
                )
            );

        if (alreadyLoaded is not null) {
            return alreadyLoaded;
        }

        foreach (string probeDirectory in AssemblyProbeDirectories) {
            string candidatePath = Path.Combine(probeDirectory, $"{requestedAssemblyName.Name}.dll");
            if (!File.Exists(candidatePath)) {
                continue;
            }

            return Assembly.LoadFrom(candidatePath);
        }

        return null;
    }

    /// <summary>
    /// Runs the <c>dotnet</c> CLI with a sandbox-friendly first-run configuration.
    /// </summary>
    /// <param name="arguments">The command-line arguments to pass to <c>dotnet</c>.</param>
    private static void RunDotnet(string arguments) {
        ProcessStartInfo startInfo = new("dotnet", arguments) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_CLI_HOME"] = Path.GetTempPath();

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet.");

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0) {
            throw new InvalidOperationException(
                $"dotnet {arguments} failed with exit code {process.ExitCode}.{Environment.NewLine}"
                    + standardOutput
                    + Environment.NewLine
                    + standardError
            );
        }
    }

    /// <summary>
    /// Measures and arranges a framework element using its natural size.
    /// </summary>
    /// <param name="element">The visual root to render.</param>
    /// <returns>The measured size that should be used for rendering.</returns>
    private static Size PrepareElementForRendering(FrameworkElement element) {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Size desiredSize = element.DesiredSize;
        double width = Math.Max(1, Math.Ceiling(desiredSize.Width));
        double height = Math.Max(1, Math.Ceiling(desiredSize.Height));

        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        return new Size(width, height);
    }

    /// <summary>
    /// Computes a raster upscaling factor so small controls remain inspectable when zoomed.
    /// </summary>
    /// <param name="renderSize">The natural render size of the element.</param>
    /// <returns>The scale factor to apply to bitmap rendering.</returns>
    private static double GetRasterRenderScale(Size renderSize) {
        const double targetLongestSidePixels = 2400;
        double longestSide = Math.Max(renderSize.Width, renderSize.Height);

        if (longestSide <= 0) {
            return 1;
        }

        return Math.Max(1, targetLongestSidePixels / longestSide);
    }

    /// <summary>
    /// Renders a framework element to the output format implied by the output file extension.
    /// </summary>
    /// <param name="element">The visual root to render.</param>
    /// <param name="outputPath">The output file path.</param>
    private static void Render(FrameworkElement element, string outputPath) {
        string extension = Path.GetExtension(outputPath);

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)) {
            WriteStatus("Rendering PNG output.");
            RenderToPng(element, outputPath);
            return;
        }

        if (string.Equals(extension, ".xps", StringComparison.OrdinalIgnoreCase)) {
            WriteStatus("Rendering XPS output.");
            RenderToXps(element, outputPath);
            return;
        }

        if (string.Equals(extension, ".svg", StringComparison.OrdinalIgnoreCase)) {
            WriteStatus("Rendering SVG output.");
            RenderToSvg(element, outputPath);
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported output format '{extension}'. Supported formats are .png, .xps, and .svg."
        );
    }

    /// <summary>
    /// Renders a framework element to a bitmap using its natural size.
    /// </summary>
    /// <param name="element">The visual root to render.</param>
    /// <returns>The rendered bitmap.</returns>
    private static RenderTargetBitmap RenderToBitmap(FrameworkElement element) {
        Size renderSize = PrepareElementForRendering(element);
        double renderScale = GetRasterRenderScale(renderSize);
        int width = Math.Max(1, (int)Math.Ceiling(renderSize.Width * renderScale));
        int height = Math.Max(1, (int)Math.Ceiling(renderSize.Height * renderScale));
        double dpi = 96 * renderScale;

        RenderTargetBitmap bitmap = new(
            width,
            height,
            dpi,
            dpi,
            PixelFormats.Pbgra32
        );

        bitmap.Render(element);
        return bitmap;
    }

    /// <summary>
    /// Measures, arranges, and renders a framework element into a PNG file using its natural size.
    /// </summary>
    /// <param name="element">The visual root to render.</param>
    /// <param name="outputPath">The file path for the generated PNG.</param>
    private static void RenderToPng(FrameworkElement element, string outputPath) {
        RenderTargetBitmap bitmap = RenderToBitmap(element);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    /// <summary>
    /// Measures, arranges, and renders a framework element into an XPS document using its natural size.
    /// </summary>
    /// <param name="element">The visual root to render.</param>
    /// <param name="outputPath">The file path for the generated XPS document.</param>
    private static void RenderToXps(FrameworkElement element, string outputPath) {
        Size renderSize = PrepareElementForRendering(element);

        if (File.Exists(outputPath)) {
            File.Delete(outputPath);
        }

        using XpsDocument xpsDocument = new(outputPath, FileAccess.ReadWrite);
        XpsDocumentWriter writer = XpsDocument.CreateXpsDocumentWriter(xpsDocument);

        FixedPage fixedPage = new() {
            Width = renderSize.Width,
            Height = renderSize.Height,
        };
        fixedPage.Children.Add(element);
        FixedPage.SetLeft(element, 0);
        FixedPage.SetTop(element, 0);

        PageContent pageContent = new();
        ((IAddChild)pageContent).AddChild(fixedPage);

        FixedDocument document = new();
        document.Pages.Add(pageContent);

        writer.Write(document);
    }

    /// <summary>
    /// Renders a framework element into an SVG file by embedding a PNG snapshot in an SVG container.
    /// </summary>
    /// <param name="element">The visual root to render.</param>
    /// <param name="outputPath">The file path for the generated SVG document.</param>
    private static void RenderToSvg(FrameworkElement element, string outputPath) {
        RenderTargetBitmap bitmap = RenderToBitmap(element);
        byte[] pngBytes = EncodeBitmapAsPng(bitmap);
        string pngBase64 = Convert.ToBase64String(pngBytes);

        string svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{bitmap.PixelWidth}" height="{bitmap.PixelHeight}" viewBox="0 0 {bitmap.PixelWidth} {bitmap.PixelHeight}">
              <image width="{bitmap.PixelWidth}" height="{bitmap.PixelHeight}" xlink:href="data:image/png;base64,{pngBase64}" />
            </svg>
            """;

        File.WriteAllText(outputPath, svg);
    }

    /// <summary>
    /// Encodes a bitmap into PNG bytes.
    /// </summary>
    /// <param name="bitmap">The bitmap to encode.</param>
    /// <returns>The PNG-encoded byte array.</returns>
    private static byte[] EncodeBitmapAsPng(BitmapSource bitmap) {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Writes a user-visible status line describing the current render stage.
    /// </summary>
    /// <param name="message">The progress message to emit.</param>
    private static void WriteStatus(string message) {
        Console.Error.WriteLine($"[status] {message}");
    }

    /// <summary>
    /// Starts WPF diagnostic capture for warnings and errors.
    /// </summary>
    /// <param name="xamlPath">The XAML path currently being rendered.</param>
    /// <returns>A disposable diagnostic session.</returns>
    private static WpfDiagnosticsSession StartDiagnosticsSession(string xamlPath) {
        WpfDiagnosticsListener listener = new(xamlPath);
        List<TraceSource> attachedSources = [];

        foreach (string sourceName in WpfDiagnosticSourceNames) {
            PropertyInfo? property = typeof(PresentationTraceSources).GetProperty(
                sourceName,
                BindingFlags.Public | BindingFlags.Static
            );

            if (property?.GetValue(null) is not TraceSource traceSource) {
                continue;
            }

            traceSource.Switch.Level = SourceLevels.Warning;
            traceSource.Listeners.Add(listener);
            attachedSources.Add(traceSource);
        }

        return new WpfDiagnosticsSession(listener, attachedSources);
    }
}

internal sealed class WpfDiagnosticsSession : IDisposable {
    private readonly WpfDiagnosticsListener listener;
    private readonly IReadOnlyList<TraceSource> attachedSources;

    /// <summary>
    /// Initializes a diagnostics session for WPF trace messages.
    /// </summary>
    /// <param name="listener">The listener receiving trace events.</param>
    /// <param name="attachedSources">The trace sources that the listener was attached to.</param>
    public WpfDiagnosticsSession(
        WpfDiagnosticsListener listener,
        IReadOnlyList<TraceSource> attachedSources
    ) {
        this.listener = listener;
        this.attachedSources = attachedSources;
    }

    /// <summary>
    /// Reports an exception in quickfix-friendly format when possible.
    /// </summary>
    /// <param name="exception">The exception to report.</param>
    public void ReportException(Exception exception) {
        Exception relevantException = exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception;

        if (relevantException is System.Windows.Markup.XamlParseException xamlParseException) {
            listener.WriteException(xamlParseException);
            return;
        }

        listener.WriteDiagnostic(
            DiagnosticSeverity.Error,
            listener.DefaultFilePath,
            lineNumber: null,
            columnNumber: null,
            relevantException.Message
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        foreach (TraceSource traceSource in attachedSources) {
            traceSource.Listeners.Remove(listener);
        }
    }
}

internal enum DiagnosticSeverity {
    Warning,
    Error,
}

internal sealed class WpfDiagnosticsListener : TraceListener {
    private static readonly Regex FileLineColumnPattern = new(
        @"(?<file>[A-Za-z]:\\[^:\r\n]+\.xaml)\((?<line>\d+),(?<column>\d+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Initializes a listener for WPF trace diagnostics.
    /// </summary>
    /// <param name="defaultFilePath">The XAML file currently being rendered.</param>
    public WpfDiagnosticsListener(string defaultFilePath) {
        DefaultFilePath = defaultFilePath;
    }

    /// <summary>
    /// Gets the default XAML path used when diagnostics do not include a source file.
    /// </summary>
    public string DefaultFilePath { get; }

    /// <inheritdoc />
    public override void Write(string? message) { }

    /// <inheritdoc />
    public override void WriteLine(string? message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        WriteDiagnosticFromText(DiagnosticSeverity.Warning, message);
    }

    /// <inheritdoc />
    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message
    ) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }

        DiagnosticSeverity severity = eventType switch {
            TraceEventType.Critical or TraceEventType.Error => DiagnosticSeverity.Error,
            _ => DiagnosticSeverity.Warning,
        };

        WriteDiagnosticFromText(severity, message);
    }

    /// <summary>
    /// Writes an exception using the best available file and line information.
    /// </summary>
    /// <param name="exception">The exception to report.</param>
    public void WriteException(System.Windows.Markup.XamlParseException exception) {
        string message = exception.InnerException?.Message ?? exception.Message;
        int? lineNumber = exception.LineNumber > 0 ? exception.LineNumber : null;
        int? columnNumber = exception.LinePosition > 0 ? exception.LinePosition : null;

        WriteDiagnostic(
            DiagnosticSeverity.Error,
            DefaultFilePath,
            lineNumber,
            columnNumber,
            message
        );
    }

    /// <summary>
    /// Writes a formatted diagnostic line.
    /// </summary>
    /// <param name="severity">The diagnostic severity.</param>
    /// <param name="filePath">The file path associated with the diagnostic.</param>
    /// <param name="lineNumber">The optional line number.</param>
    /// <param name="columnNumber">The optional column number.</param>
    /// <param name="message">The diagnostic message.</param>
    public void WriteDiagnostic(
        DiagnosticSeverity severity,
        string filePath,
        int? lineNumber,
        int? columnNumber,
        string message
    ) {
        string normalizedMessage = NormalizeMessage(message);
        string location = lineNumber is not null && columnNumber is not null
            ? $"{filePath}({lineNumber},{columnNumber})"
            : filePath;

        Console.Error.WriteLine($"{location}: {severity.ToString().ToLowerInvariant()}: {normalizedMessage}");
    }

    private void WriteDiagnosticFromText(DiagnosticSeverity severity, string message) {
        Match match = FileLineColumnPattern.Match(message);

        if (match.Success) {
            string filePath = match.Groups["file"].Value;
            int lineNumber = int.Parse(match.Groups["line"].Value);
            int columnNumber = int.Parse(match.Groups["column"].Value);
            string cleanMessage = message.Replace(match.Value, string.Empty, StringComparison.Ordinal).Trim();

            WriteDiagnostic(severity, filePath, lineNumber, columnNumber, cleanMessage);
            return;
        }

        WriteDiagnostic(severity, DefaultFilePath, null, null, message);
    }

    private static string NormalizeMessage(string message) {
        return Regex.Replace(message.Trim(), @"\s+", " ");
    }
}
