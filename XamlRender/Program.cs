using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xaml;
using System.Xml;
using System.Xml.Linq;

internal static class Program {
    private static readonly List<ProjectAssemblyLoadContext> LoadedContexts = [];

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
    /// Loads the compiled assembly and creates an instance of the XAML root type.
    /// </summary>
    /// <param name="assemblyPath">The path to the built assembly.</param>
    /// <param name="xamlClassName">The fully qualified root type name from <c>x:Class</c>.</param>
    /// <returns>The instantiated framework element.</returns>
    private static FrameworkElement InstantiateCompiledRoot(string assemblyPath, string xamlClassName) {
        ProjectAssemblyLoadContext loadContext = new(assemblyPath);
        LoadedContexts.Add(loadContext);

        Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
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

internal sealed class ProjectAssemblyLoadContext : AssemblyLoadContext {
    private readonly AssemblyDependencyResolver dependencyResolver;

    /// <summary>
    /// Initializes a load context that resolves dependencies relative to a built project assembly.
    /// </summary>
    /// <param name="mainAssemblyPath">The path to the primary assembly whose dependencies should be resolved.</param>
    public ProjectAssemblyLoadContext(string mainAssemblyPath) : base(isCollectible: false) {
        dependencyResolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    /// <summary>
    /// Resolves dependent assemblies using the project output directory.
    /// </summary>
    /// <param name="assemblyName">The assembly name being requested.</param>
    /// <returns>The loaded assembly, or <see langword="null"/> when resolution fails.</returns>
    protected override Assembly? Load(AssemblyName assemblyName) {
        string? path = dependencyResolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}