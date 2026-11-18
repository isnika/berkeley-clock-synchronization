using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Berkeley.Desktop.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility.Visible;
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is not true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is not true;
    }
}

/// <summary>
/// So sánh giá trị với parameter, nếu bằng nhau thì trả về Visible, ngược lại Collapsed.
/// Rất tiện cho việc chuyển tab navigation: TabIndex == 0, 1, 2, 3...
/// </summary>
public class EqualsToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return Invert ? Visibility.Visible : Visibility.Collapsed;

        bool equals = value.ToString() == parameter.ToString();
        if (Invert) equals = !equals;

        return equals ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// So sánh tab hiện tại với parameter để đổi màu nền / màu chữ tab đang active.
/// </summary>
public class TabActiveBrushConverter : IValueConverter
{
    public Brush ActiveBrush { get; set; } = (Brush)new BrushConverter().ConvertFrom("#38BDF8")!;
    public Brush InactiveBrush { get; set; } = (Brush)new BrushConverter().ConvertFrom("#64748B")!;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return InactiveBrush;
        return value.ToString() == parameter.ToString() ? ActiveBrush : InactiveBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class TabActiveBgConverter : IValueConverter
{
    public Brush ActiveBrush { get; set; } = (Brush)new BrushConverter().ConvertFrom("#1E293B")!;
    public Brush InactiveBrush { get; set; } = Brushes.Transparent;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return InactiveBrush;
        return value.ToString() == parameter.ToString() ? ActiveBrush : InactiveBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
