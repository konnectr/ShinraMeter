using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TCC.UI.Controls.Settings
{
    public partial class CheckboxSetting
    {
        /// <summary>Neutral colour the checkbox falls back to, also the value of an unstyled control.</summary>
        private static readonly Brush DefaultCheckBoxColor = Brushes.LightSlateGray;

        public CheckboxSetting()
        {
            InitializeComponent();
            Loaded += (_, _) => EnsureVisibleCheckBoxColor();
        }

        /// <summary>
        /// Last-resort guard against an invisible control. The whole checked look - the box outline,
        /// its fill and the check mark - is painted with <see cref="CheckBoxColor"/>, while the
        /// unchecked look keeps a hardcoded outline of its own. A colour that never arrived (a style
        /// level brush whose binding did not resolve leaves SolidColorBrush at its default, which is
        /// fully transparent) therefore used to make a checked checkbox vanish completely instead of
        /// merely losing its accent. Checked at Loaded, when the DataContext and the bindings that
        /// feed the style are settled.
        /// </summary>
        private void EnsureVisibleCheckBoxColor()
        {
            var invisible = CheckBoxColor == null
                            || (CheckBoxColor is SolidColorBrush solid && solid.Color.A == 0)
                            || CheckBoxColor.Opacity <= 0;

            if (invisible) { CheckBoxColor = DefaultCheckBoxColor; }
        }

        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        public static readonly DependencyProperty IsOnProperty =
            DependencyProperty.Register("IsOn", typeof(bool), typeof(CheckboxSetting), new PropertyMetadata(false));

        public Brush CheckBoxColor
        {
            get => (Brush)GetValue(CheckBoxColorProperty);
            set => SetValue(CheckBoxColorProperty, value);
        }

        public static readonly DependencyProperty CheckBoxColorProperty =
            DependencyProperty.Register("CheckBoxColor", typeof(Brush), typeof(CheckboxSetting), new PropertyMetadata(DefaultCheckBoxColor));

        public string SettingName
        {
            get => (string)GetValue(SettingNameProperty);
            set => SetValue(SettingNameProperty, value);
        }

        public static readonly DependencyProperty SettingNameProperty =
            DependencyProperty.Register("SettingName", typeof(string), typeof(CheckboxSetting), new PropertyMetadata(""));

        public Geometry SvgIcon
        {
            get => (Geometry)GetValue(SvgIconProperty);
            set => SetValue(SvgIconProperty, value);
        }

        public static readonly DependencyProperty SvgIconProperty =
            DependencyProperty.Register("SvgIcon", typeof(Geometry), typeof(CheckboxSetting));

        public double CheckboxSize
        {
            get => (double)GetValue(CheckboxSizeProperty);
            set => SetValue(CheckboxSizeProperty, value);
        }

        public static readonly DependencyProperty CheckboxSizeProperty =
            DependencyProperty.Register("CheckboxSize", typeof(double), typeof(CheckboxSetting), new PropertyMetadata(18D));

        public bool SwapCheckboxPosition
        {
            get => (bool)GetValue(SwapCheckboxPositionProperty);
            set => SetValue(SwapCheckboxPositionProperty, value);
        }

        public static readonly DependencyProperty SwapCheckboxPositionProperty =
            DependencyProperty.Register("SwapCheckboxPosition", typeof(bool), typeof(CheckboxSetting), new PropertyMetadata(false));

        private void OnMouseButtonDown(object sender, MouseButtonEventArgs e)
        {
            CheckBox.IsChecked = !CheckBox.IsChecked;
        }
    }
}