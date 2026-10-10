using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Nighty.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Async command that disables itself while running and surfaces exceptions to the log.</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _run;
    private readonly Func<object?, bool>? _can;
    private bool _busy;

    public AsyncCommand(Func<Task> run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }
    public AsyncCommand(Func<object?, Task> run, Func<object?, bool>? can = null) { _run = run; _can = can; }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_busy && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_busy) return;
        _busy = true; Refresh();
        try { await _run(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.Log.Error("Command failed", ex); }
        finally { _busy = false; Refresh(); }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public enum StatusKind { Neutral, Success, Warning, Error, Info }

public sealed class BoolInverseConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool v = value is bool b && b;
        if (p as string == "!") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SocdDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() switch
    {
        "LastInput" => "The key you pressed last wins until you let go of it.",
        "Neutral" => "Holding both keys cancels out, so you stop.",
        _ => "The key you pressed first wins until you let go of it.",
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value != null && !(value is string s && s.Length == 0);
        if (p as string == "!") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>value.ToString() == parameter. Used for tab radio buttons (two-way) and visibility.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool eq = string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);
        if (t == typeof(Visibility)) return eq ? Visibility.Visible : Visibility.Collapsed;
        return eq;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        if (value is not true || p == null) return Binding.DoNothing;
        var target = Nullable.GetUnderlyingType(t) ?? t;
        return target.IsEnum ? Enum.Parse(target, p.ToString()!) : System.Convert.ChangeType(p, target, CultureInfo.InvariantCulture);
    }
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        string key = value is StatusKind k ? k switch
        {
            StatusKind.Success => "SuccessBrush",
            StatusKind.Warning => "WarningBrush",
            StatusKind.Error => "DangerBrush",
            StatusKind.Info => "AccentBrush",
            _ => "MutedBrush"
        } : "MutedBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Shows the selected ComboBox item through its DisplayMemberPath (the custom ComboBox template cannot use WPF's internal selector).</summary>
public sealed class DisplayMemberConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        var item = values.Length > 0 ? values[0] : null;
        var path = values.Length > 1 ? values[1] as string : null;
        if (item == null || item == System.Windows.DependencyProperty.UnsetValue) return "";
        if (string.IsNullOrEmpty(path)) return item.ToString() ?? "";
        object? cur = item;
        foreach (var part in path.Split('.'))
        {
            if (cur == null) break;
            cur = cur.GetType().GetProperty(part)?.GetValue(cur);
        }
        return cur?.ToString() ?? "";
    }
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
