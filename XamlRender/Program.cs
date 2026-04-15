using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xaml;
using System.Xml;
using System.Xml.Linq;

internal static class Program {
    private static readonly HashSet<string> AssemblyProbeDirectories = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Entry point for the renderer executable.
    /// </summary>
    /// <param name="args">Command-line arguments containing the input XAML path and output PNG path.</param>
    [STAThread]
    private static void Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("Usage: XamlToBitmap.exe input.xaml output.png");
            return;
        }

        string xamlPath = Path.GetFullPath(args[0]);
        string pngPath = Path.GetFullPath(args[1]);

        FrameworkElement element = LoadRootElement(xamlPath);

        int width = 800;
        int height = 600;

        RenderToPng(element, width, height, pngPath);
    }

    /// <summary>
    /// Loads the root framework element from either loose XAML or a compiled WPF control.
    /// </summary>
    /// <param name="xamlPath">The absolute path to the input XAML file.</param>
    /// <returns>The loaded framework element ready for rendering.</returns>
    private static FrameworkElement LoadRootElement(string xamlPath) {
        string? xamlClassName = TryReadXamlClassName(xamlPath);

        if (string.IsNullOrWhiteSpace(xamlClassName)) {
            return LoadLooseXaml(xamlPath);
        }

        string? projectPath = FindOwningProject(xamlPath);
        if (projectPath is null) {
            throw new InvalidOperationException($"Unable to find a WPF project for '{xamlPath}'.");
        }

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

        return assemblyPath;
    }

    /// <summary>
    /// Initializes application-level resources from the owning WPF project before control rendering begins.
    /// </summary>
    /// <param name="projectPath">The path to the owning project file.</param>
    /// <param name="assemblyPath">The path to the built assembly.</param>
    private static void InitializeApplicationResources(string projectPath, string assemblyPath) {
        RegisterAssemblyProbeDirectory(assemblyPath);
        Assembly assembly = LoadAssemblyIntoDefaultContext(assemblyPath);
        string? appXamlPath = FindApplicationDefinitionPath(projectPath);

        if (appXamlPath is not null) {
            string? appClassName = TryReadXamlClassName(appXamlPath);
            if (!string.IsNullOrWhiteSpace(appClassName)) {
                Type? appType = assembly.GetType(appClassName, throwOnError: false, ignoreCase: false);
                if (appType is not null && typeof(Application).IsAssignableFrom(appType)) {
                    Application application = EnsureApplicationInstance(appType);
                    InvokeInitializeComponentIfPresent(application);
                    return;
                }
            }
        }

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
    /// Measures, arranges, and renders a framework element into a PNG file.
    /// </summary>
    /// <param name="element">The visual root to render.</param>
    /// <param name="width">The target bitmap width in pixels.</param>
    /// <param name="height">The target bitmap height in pixels.</param>
    /// <param name="outputPath">The file path for the generated PNG.</param>
    private static void RenderToPng(
        FrameworkElement element,
        int width,
        int height,
        string outputPath
    ) {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        RenderTargetBitmap bitmap = new(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32
        );

        bitmap.Render(element);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
