using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Windows;

public sealed partial class WdaControlNoticeWindow :
    Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private enum NoticeState { Waiting, Connected, Failed }

    private static WdaControlNoticeWindow? _active;
    private const double WaitingWidth = 500;
    private readonly DispatcherTimer _closeTimer;
    private NoticeState _state = NoticeState.Waiting;
    private string? _failureDetail;
    private int _remainingSeconds = 5;

    public string TitleText => LocalizationService.Get(_state switch
    {
        NoticeState.Waiting => "WdaControlWaitingTitle",
        NoticeState.Failed => "WdaControlFailedTitle",
        _ => "WdaControlConnectedTitle",
    });
    public string BodyText => LocalizationService.Get(_state switch
    {
        NoticeState.Waiting => "WdaControlWaitingBody",
        NoticeState.Failed => "WdaControlFailedBody",
        _ => "WdaControlConnectedBody",
    });
    public string DetailText
    {
        get
        {
            if (_state == NoticeState.Failed && !string.IsNullOrWhiteSpace(_failureDetail))
                return _failureDetail;
            return LocalizationService.Get(_state == NoticeState.Waiting
                ? "WdaControlWaitingDetail"
                : "WdaControlConnectedDetail");
        }
    }
    public string StepOneText => LocalizationService.Get("WdaControlStepOne");
    public string StepTwoText => LocalizationService.Get("WdaControlStepTwo");
    public string StepThreeText => LocalizationService.Get("WdaControlStepThree");
    public string StepFourText => LocalizationService.Get("WdaControlStepFour");
    public string StepFiveText => LocalizationService.Get("WdaControlStepFive");
    public string StatusText => _state switch
    {
        NoticeState.Waiting => LocalizationService.Get("WdaControlWaitingStatus"),
        NoticeState.Failed => LocalizationService.Get("WdaControlFailedStatus"),
        _ => LocalizationService.Format(
            "WdaControlPromptAutoCloseFormat", _remainingSeconds),
    };
    public Visibility WaitingStepsVisibility => _state == NoticeState.Waiting
        ? Visibility.Visible : Visibility.Collapsed;

    private WdaControlNoticeWindow(Window owner)
    {
        Owner = owner;
        DataContext = this;
        InitializeComponent();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _closeTimer.Tick += OnCloseTimerTick;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            LocalizationService.LanguageChanged -= OnLanguageChanged;
            if (ReferenceEquals(_active, this)) _active = null;
        };
    }

    internal static void ShowWaiting(Window owner)
    {
        var window = GetOrCreate(owner);
        window.SetState(NoticeState.Waiting);
        window.Activate();
    }

    internal static void ShowConnected(Window owner)
    {
        var window = GetOrCreate(owner);
        window.SetState(NoticeState.Connected);
        window.Activate();
    }

    internal static void ShowFailure(Window owner, string? detail)
    {
        var window = GetOrCreate(owner);
        window._failureDetail = detail;
        window.SetState(NoticeState.Failed);
        window.Activate();
    }

    internal static bool TryCloseActive()
    {
        if (_active is null) return false;
        _active.Close();
        return true;
    }

    private static WdaControlNoticeWindow GetOrCreate(Window owner)
    {
        if (_active is not null)
        {
            _active.Owner = owner;
            return _active;
        }
        var window = new WdaControlNoticeWindow(owner);
        _active = window;
        window.Show();
        window.ReflowToContent();
        return window;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        if (_state != NoticeState.Connected) return;
        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            Close();
            return;
        }
        OnPropertyChanged(nameof(StatusText));
    }

    private void SetState(NoticeState state)
    {
        _state = state;
        MinWidth = WaitingWidth;
        MaxWidth = WaitingWidth;
        Width = WaitingWidth;
        _remainingSeconds = 5;
        _closeTimer.Stop();
        if (state == NoticeState.Connected) _closeTimer.Start();
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(BodyText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(StepOneText));
        OnPropertyChanged(nameof(StepTwoText));
        OnPropertyChanged(nameof(StepThreeText));
        OnPropertyChanged(nameof(StepFourText));
        OnPropertyChanged(nameof(StepFiveText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(WaitingStepsVisibility));
        SetupStepsPanel.Visibility = WaitingStepsVisibility;
        ReflowToContent();
        Dispatcher.BeginInvoke(ReflowToContent, DispatcherPriority.Render);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(BodyText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(StepOneText));
        OnPropertyChanged(nameof(StepTwoText));
        OnPropertyChanged(nameof(StepThreeText));
        OnPropertyChanged(nameof(StepFourText));
        OnPropertyChanged(nameof(StepFiveText));
        OnPropertyChanged(nameof(StatusText));
        Dispatcher.BeginInvoke(ReflowToContent, DispatcherPriority.Render);
    }

    private void RecenterOverOwner()
    {
        if (Owner is null) return;
        Left = Owner.Left + Math.Max(0, (Owner.ActualWidth - ActualWidth) / 2);
        Top = Owner.Top + Math.Max(0, (Owner.ActualHeight - ActualHeight) / 2);
    }

    private void ReflowToContent()
    {
        if (!IsVisible || Content is not FrameworkElement content) return;
        SizeToContent = System.Windows.SizeToContent.Manual;
        MinWidth = WaitingWidth;
        MaxWidth = WaitingWidth;
        Width = WaitingWidth;
        MinHeight = 0;
        MaxHeight = double.PositiveInfinity;
        content.InvalidateMeasure();
        content.Measure(new Size(WaitingWidth, double.PositiveInfinity));
        var nonClientHeight = Math.Max(0, ActualHeight - content.ActualHeight);
        Height = Math.Max(1, Math.Ceiling(content.DesiredSize.Height + nonClientHeight));
        UpdateLayout();
        RecenterOverOwner();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
