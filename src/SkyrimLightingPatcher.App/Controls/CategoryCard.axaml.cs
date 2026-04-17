using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SkyrimLightingPatcher.App.Controls;

public partial class CategoryCard : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<CategoryCard, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<CategoryCard, string>(nameof(Description), string.Empty);

    public static readonly StyledProperty<string> CheckBoxTextProperty =
        AvaloniaProperty.Register<CategoryCard, string>(nameof(CheckBoxText), string.Empty);

    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<CategoryCard, bool>(nameof(IsChecked), false);

    public static readonly StyledProperty<string> EnabledImageProperty =
        AvaloniaProperty.Register<CategoryCard, string>(nameof(EnabledImage), string.Empty);

    public static readonly StyledProperty<string> DisabledImageProperty =
        AvaloniaProperty.Register<CategoryCard, string>(nameof(DisabledImage), string.Empty);

    public CategoryCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string CheckBoxText
    {
        get => GetValue(CheckBoxTextProperty);
        set => SetValue(CheckBoxTextProperty, value);
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public string EnabledImage
    {
        get => GetValue(EnabledImageProperty);
        set => SetValue(EnabledImageProperty, value);
    }

    public string DisabledImage
    {
        get => GetValue(DisabledImageProperty);
        set => SetValue(DisabledImageProperty, value);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
