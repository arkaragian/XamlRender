using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

internal static class Program {
    [STAThread]
    private static void Main(string[] args) {
        if (args.Length < 2) {
            Console.WriteLine("Usage: XamlToBitmap.exe input.xaml output.png");
            return;
        }

        string xamlPath = args[0];
        string pngPath = args[1];

        FrameworkElement element = LoadXaml(xamlPath);

        int width = 800;
        int height = 600;

        RenderToPng(element, width, height, pngPath);
    }

    private static FrameworkElement LoadXaml(string path) {
        string xaml = File.ReadAllText(path);

        using StringReader stringReader = new StringReader(xaml);
        using XmlReader xmlReader = XmlReader.Create(stringReader);

        object root = XamlReader.Load(xmlReader);

        if (root is not FrameworkElement element) {
            throw new InvalidOperationException("XAML root must be a FrameworkElement.");
        }

        return element;
    }

    private static void RenderToPng(
        FrameworkElement element,
        int width,
        int height,
        string outputPath
    ) {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        RenderTargetBitmap bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32
        );

        bitmap.Render(element);

        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using FileStream stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
