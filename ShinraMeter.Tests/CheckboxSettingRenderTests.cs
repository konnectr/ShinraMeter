using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShinraMeter.Tests;

/// <summary>
/// Rasterises the real <c>TCC.UI.Controls.Settings.CheckboxSetting</c> and counts the pixels it
/// actually paints, instead of asserting on the xaml source text.
/// <para>
/// The rows of the "Combat notifications" list in the Events tab live inside an
/// <see cref="ItemsControl"/> <see cref="DataTemplate"/>, so their DataContext is a
/// <c>CombatNotificationVM</c> - not the <c>SettingsWindowViewModel</c> the window level
/// <c>Style TargetType="settings:CheckboxSetting"</c> assumes. That style hands the control its
/// <c>CheckBoxColor</c>, and everything the checked state paints (border, box fill, check mark) is
/// derived from it, so a row whose flag starts out true rendered nothing at all. The tests below
/// reproduce exactly that nesting.
/// </para>
/// </summary>
public class CheckboxSettingRenderTests
{
    private const double RowWidth = 220;
    private const double RowHeight = 32;

    [Fact]
    public void CheckedRowInsideAnItemsControl_PaintsItsCheckbox()
    {
        var result = RunOnStaThread(Measure);

        // A row that is off paints the empty square, so it is the baseline for "something is there".
        Assert.True(result.UncheckedPixels > 0,
            $"The unchecked row painted nothing at all ({result.UncheckedPixels} px) - the harness is broken.");

        // The regression: checked from the start inside a DataTemplate painted zero pixels.
        Assert.True(result.CheckedFromStartPixels > 0,
            "A row whose flag is already true when the DataTemplate is applied painted nothing: " +
            $"unchecked={result.UncheckedPixels}px, checked-from-start={result.CheckedFromStartPixels}px, " +
            $"toggled={result.ToggledPixels}px.");

        // A checked box is a filled square plus the mark, so it must be at least as dense as the
        // empty outline - this is what catches "the border survived but the fill/mark did not".
        Assert.True(result.CheckedFromStartPixels >= result.UncheckedPixels,
            $"The checked row is thinner than the unchecked one: checked-from-start={result.CheckedFromStartPixels}px " +
            $"vs unchecked={result.UncheckedPixels}px.");

        // Toggling false -> true (the path the storyboard covers) must look the same as starting true.
        Assert.True(result.ToggledPixels > 0,
            $"The row toggled false -> true painted nothing ({result.ToggledPixels} px).");
        Assert.True(Math.Abs(result.CheckedFromStartPixels - result.ToggledPixels) <= result.ToggledPixels * 0.2,
            $"Starting checked ({result.CheckedFromStartPixels}px) does not look like toggling to checked " +
            $"({result.ToggledPixels}px).");

        // The static usages elsewhere in the tab (no ItemsControl, window VM as DataContext) must
        // keep the accent-coloured look.
        Assert.True(result.StaticCheckedPixels > 0,
            $"The plain checked CheckboxSetting outside the ItemsControl painted nothing ({result.StaticCheckedPixels} px).");
        Assert.True(result.StaticCheckedIsAccentColoured,
            "The plain checked CheckboxSetting lost the accent colour from the window style.");
        Assert.True(result.RowCheckedIsAccentColoured,
            "The checked row inside the ItemsControl is not painted in the accent colour.");
    }

    private sealed record RenderResult(
        int UncheckedPixels,
        int CheckedFromStartPixels,
        int ToggledPixels,
        int StaticCheckedPixels,
        bool StaticCheckedIsAccentColoured,
        bool RowCheckedIsAccentColoured);

    private static RenderResult Measure()
    {
        EnsureApplicationResources();

        var host = (FrameworkElement)XamlReader.Parse(BuildHostXaml());
        var windowVm = new FakeSettingsVm();

        // The real control lives in a Window whose DataContext is the settings view model, and the
        // window level style reaches that view model from the element. Same shape here.
        var window = new Window
        {
            DataContext = windowVm,
            Content = host,
            Width = RowWidth,
            Height = 4 * RowHeight,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };
        window.Show();

        var rows = new List<FakeRowVm> { new(false), new(true), new(false) };
        var list = (ItemsControl)host.FindName("List");
        list.ItemsSource = rows;

        Layout(host);

        // The third row is the false -> true transition the storyboard was written for.
        rows[2].IsOn = true;
        Layout(host);

        var bitmap = Rasterise(host);

        var uncheckedBox = BoxOf(host, list, 0);
        var checkedBox = BoxOf(host, list, 1);
        var toggledBox = BoxOf(host, list, 2);
        var staticBox = BoxOfElement(host, (FrameworkElement)host.FindName("StaticChecked"));

        var result = new RenderResult(
            CountPainted(bitmap, uncheckedBox),
            CountPainted(bitmap, checkedBox),
            CountPainted(bitmap, toggledBox),
            CountPainted(bitmap, staticBox),
            HasAccentPixel(bitmap, staticBox, windowVm.SelfColor),
            HasAccentPixel(bitmap, checkedBox, windowVm.SelfColor));

        window.Close();
        return result;
    }

    /// <summary>
    /// A window that mirrors the Events tab: the window level CheckboxSetting style copied verbatim
    /// out of SettingsWindow.xaml, one plain CheckboxSetting like the static ones, and the
    /// ItemsControl whose DataTemplate holds a CheckboxSetting bound to the row's own flag.
    /// </summary>
    private static string BuildHostXaml()
    {
        return $$"""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:settings="clr-namespace:TCC.UI.Controls.Settings;assembly=ShinraMeter"
                  Background="#FF1B1B1B" Width="{{RowWidth}}">
              <Grid.Resources>
                {{WindowCheckboxStyleFromSettingsWindow()}}
              </Grid.Resources>
              <StackPanel>
                <settings:CheckboxSetting x:Name="StaticChecked" Height="{{RowHeight}}"
                                          IsOn="{Binding StaticFlag, Mode=TwoWay}" />
                <ItemsControl x:Name="List">
                  <ItemsControl.ItemTemplate>
                    <DataTemplate>
                      <Grid Height="{{RowHeight}}" Background="Transparent">
                        <Grid.ColumnDefinitions>
                          <ColumnDefinition Width="*" />
                          <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <settings:CheckboxSetting Grid.Column="1" VerticalAlignment="Center"
                                                  IsOn="{Binding IsOn, Mode=TwoWay}" />
                      </Grid>
                    </DataTemplate>
                  </ItemsControl.ItemTemplate>
                </ItemsControl>
              </StackPanel>
            </Grid>
            """;
    }

    /// <summary>
    /// Reads the live style out of SettingsWindow.xaml so this test keeps testing what the window
    /// really does to every CheckboxSetting it contains.
    /// </summary>
    private static string WindowCheckboxStyleFromSettingsWindow()
    {
        var xaml = File.ReadAllText(SettingsWindowPath());
        var match = Regex.Match(
            xaml,
            "<Style\\s+TargetType=\"settings:CheckboxSetting\".*?</Style>",
            RegexOptions.Singleline);
        Assert.True(match.Success, "SettingsWindow.xaml no longer carries a CheckboxSetting style.");

        // BasedOn points at the default UserControl style, which XamlReader has no key for here.
        return Regex.Replace(match.Value, "\\s+BasedOn=\"\\{StaticResource \\{x:Type settings:CheckboxSetting\\}\\}\"", "");
    }

    private static string SettingsWindowPath()
    {
        return SmokeTests.ProjectPath("DamageMeter.UI", "Windows", "SettingsWindow.xaml");
    }

    private static void EnsureApplicationResources()
    {
        var app = Application.Current ?? new Application();
        if (app.Resources.MergedDictionaries.Count > 0) { return; }

        foreach (var name in new[] { "Misc", "SVG", "Brushes", "Converters", "Styles" })
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/ShinraMeter;component/Resources/{name}.xaml", UriKind.Absolute)
            });
        }
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(RowWidth, double.PositiveInfinity));
        element.Arrange(new Rect(new Point(0, 0), element.DesiredSize));
        element.UpdateLayout();
        Dispatcher.Flush();
    }

    private static RenderTargetBitmap Rasterise(FrameworkElement element)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        Assert.True(width > 0 && height > 0, $"The host laid out to {width}x{height}.");

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    /// <summary>The rectangle of the actual CheckBox inside the n-th row's CheckboxSetting.</summary>
    private static Int32Rect BoxOf(FrameworkElement host, ItemsControl list, int index)
    {
        var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index);
        Assert.NotNull(container);
        var setting = FindDescendant(container, "TCC.UI.Controls.Settings.CheckboxSetting");
        Assert.NotNull(setting);
        return BoxOfElement(host, setting!);
    }

    private static Int32Rect BoxOfElement(FrameworkElement host, FrameworkElement setting)
    {
        var checkBox = (FrameworkElement?)setting.FindName("CheckBox")
                       ?? FindDescendant(setting, "System.Windows.Controls.CheckBox");
        Assert.NotNull(checkBox);

        var origin = checkBox!.TransformToAncestor(host).Transform(new Point(0, 0));
        var w = Math.Max(1, (int)Math.Ceiling(checkBox.ActualWidth));
        var h = Math.Max(1, (int)Math.Ceiling(checkBox.ActualHeight));
        return new Int32Rect((int)Math.Floor(origin.X), (int)Math.Floor(origin.Y), w, h);
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string typeFullName)
    {
        if (root is FrameworkElement fe && fe.GetType().FullName == typeFullName) { return fe; }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindDescendant(VisualTreeHelper.GetChild(root, i), typeFullName);
            if (found != null) { return found; }
        }

        return null;
    }

    private static byte[] Crop(BitmapSource bitmap, Int32Rect rect)
    {
        rect = new Int32Rect(
            Math.Max(0, rect.X),
            Math.Max(0, rect.Y),
            Math.Min(rect.Width, bitmap.PixelWidth - Math.Max(0, rect.X)),
            Math.Min(rect.Height, bitmap.PixelHeight - Math.Max(0, rect.Y)));
        Assert.True(rect.Width > 0 && rect.Height > 0, "The checkbox is outside the rendered area.");

        var stride = rect.Width * 4;
        var pixels = new byte[stride * rect.Height];
        bitmap.CopyPixels(rect, pixels, stride, 0);
        return pixels;
    }

    /// <summary>Pixels that differ from the window background, i.e. pixels the checkbox drew.</summary>
    private static int CountPainted(BitmapSource bitmap, Int32Rect rect)
    {
        var pixels = Crop(bitmap, rect);
        var painted = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Background is #FF1B1B1B; anything visibly lighter is paint.
            if (pixels[i] > 0x33 || pixels[i + 1] > 0x33 || pixels[i + 2] > 0x33) { painted++; }
        }

        return painted;
    }

    private static bool HasAccentPixel(BitmapSource bitmap, Int32Rect rect, Color accent)
    {
        var pixels = Crop(bitmap, rect);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Pbgra32, opaque over a dark ground: close enough to the accent colour counts.
            if (Math.Abs(pixels[i] - accent.B) <= 24 &&
                Math.Abs(pixels[i + 1] - accent.G) <= 24 &&
                Math.Abs(pixels[i + 2] - accent.R) <= 24)
            {
                return true;
            }
        }

        return false;
    }

    private static T RunOnStaThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "The WPF render thread did not finish.");

        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    private static class Dispatcher
    {
        public static void Flush()
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.SystemIdle, new Action(() => { }));
        }
    }

    private sealed class FakeSettingsVm : INotifyPropertyChanged
    {
        /// <summary>Same role as SettingsWindowViewModel.SelfColor: the accent the tab is drawn in.</summary>
        public Color SelfColor { get; } = Color.FromRgb(0xFF, 0x88, 0x22);

        /// <summary>Same role as SettingsWindowViewModel.SelfBrush, which the window style binds to.</summary>
        public Brush SelfBrush => new SolidColorBrush(SelfColor);

        public bool StaticFlag { get; set; } = true;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class FakeRowVm : INotifyPropertyChanged
    {
        private bool _isOn;

        public FakeRowVm(bool isOn) { _isOn = isOn; }

        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value) { return; }
                _isOn = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
