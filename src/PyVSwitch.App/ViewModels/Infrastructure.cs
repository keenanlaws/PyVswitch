using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using PyVSwitch.Services;

namespace PyVSwitch.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        : this(parameter =>
        {
            execute(parameter);
            return Task.CompletedTask;
        }, canExecute)
    {
    }

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "A command failed.");
            CommandFailed?.Invoke(ex);
        }
    }

    /// <summary>Raised for exceptions a command did not handle itself.</summary>
    public static event Action<Exception>? CommandFailed;
}

public sealed class ToastItem
{
    public required string Message { get; init; }
    public string Tone { get; init; } = "success";
    public string Glyph => Tone switch
    {
        "danger" => Controls.Glyphs.Error,
        "warning" => Controls.Glyphs.Warning,
        "accent" => Controls.Glyphs.Info,
        _ => Controls.Glyphs.Check
    };
}

/// <summary>The one long-running task shown in the activity bar: installs, pip runs, uninstalls.</summary>
public sealed class ActivityViewModel : Observable
{
    private readonly Dispatcher _dispatcher;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cancellation;
    private bool _isVisible;
    private bool _isRunning;
    private bool _isLogOpen;
    private bool _isIndeterminate = true;
    private double _progress;
    private string _title = "";
    private string _detail = "";
    private string _tone = "accent";

    public ActivityViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        CancelCommand = new RelayCommand(_ => _cancellation?.Cancel(), _ => IsRunning);
        DismissCommand = new RelayCommand(_ => IsVisible = false, _ => !IsRunning);
        ToggleLogCommand = new RelayCommand(_ => IsLogOpen = !IsLogOpen);
    }

    public ICommand CancelCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand ToggleLogCommand { get; }

    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public bool IsLogOpen { get => _isLogOpen; set => Set(ref _isLogOpen, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string Tone { get => _tone; private set => Set(ref _tone, value); }
    public string LogText => _log.ToString();
    public bool HasLog => _log.Length > 0;

    public CancellationToken Begin(string title, string detail)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _log.Clear();
        Raise(nameof(LogText));
        Raise(nameof(HasLog));
        Title = title;
        Detail = detail;
        Tone = "accent";
        Progress = 0;
        IsIndeterminate = true;
        IsRunning = true;
        IsVisible = true;
        CommandManager.InvalidateRequerySuggested();
        return _cancellation.Token;
    }

    /// <summary>Safe to call from any thread.</summary>
    public void Append(string line)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_log.Length > 200_000)
            {
                _log.Remove(0, 100_000);
            }

            _log.AppendLine(line);
            Raise(nameof(LogText));
            Raise(nameof(HasLog));
        });
    }

    public void SetStatus(string detail, double? fraction = null)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Detail = detail;
            IsIndeterminate = fraction is null;
            Progress = fraction ?? 0;
        });
    }

    public void Complete(bool success, string message)
    {
        IsRunning = false;
        IsIndeterminate = false;
        Progress = 1;
        Tone = success ? "success" : "danger";
        Detail = message;
        if (!success)
        {
            IsLogOpen = HasLog;
        }

        CommandManager.InvalidateRequerySuggested();
    }
}
