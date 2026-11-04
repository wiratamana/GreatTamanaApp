using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GTA.App;

public sealed class AskQuestionsWindow : Window
{
    private readonly List<QuestionSpec>              _questions;
    private readonly Action<List<QuestionAnswer>>    _onSubmit;
    private readonly Action                          _onCancelled;
    private readonly Action                          _onTimeout;

    // Per-question saved state — index-aligned with _questions.
    private readonly List<string?> _chosenAnswer = new();
    private readonly List<bool>    _isFreeText   = new();

    private int  _currentIndex = 0;
    private bool _finished     = false; // true once Submit, Cancel or Timeout has actually fired

    // Controls rebuilt fresh on every RenderPage() call.
    private readonly StackPanel _pageHost;
    private readonly TextBlock  _pageLabel;
    private readonly TextBlock  _validationLabel;
    private readonly Button     _backButton;
    private readonly Button     _nextButton;
    private readonly Button     _submitButton;

    private readonly TimeSpan          _timeoutDuration;
    private readonly DateTime          _deadlineUtc;
    private readonly DispatcherTimer   _countdownTimer;
    private readonly TextBlock         _countdownLabel;
    private readonly ProgressBar       _countdownBar;

    public AskQuestionsWindow(
        List<QuestionSpec>           questions,
        Action<List<QuestionAnswer>> onSubmit,
        Action                       onCancelled,
        Action                       onTimeout,
        TimeSpan?                    timeout      = null,
        string?                      sessionTitle = null)
    {
        _questions   = questions ?? throw new ArgumentNullException(nameof(questions));
        _onSubmit    = onSubmit  ?? throw new ArgumentNullException(nameof(onSubmit));
        _onCancelled = onCancelled ?? throw new ArgumentNullException(nameof(onCancelled));
        _onTimeout   = onTimeout ?? throw new ArgumentNullException(nameof(onTimeout));
        _timeoutDuration = timeout ?? TimeSpan.FromMinutes(30);

        for (int i = 0; i < _questions.Count; i++)
        {
            _chosenAnswer.Add(null);
            _isFreeText.Add(false);
        }

        Title = string.IsNullOrEmpty(sessionTitle)
            ? "🙋 The assistant has a question"
            : $"🙋 The assistant has a question — {sessionTitle}";
        Width                 = 560;
        Height                = 480;
        MinWidth              = 420;
        MinHeight             = 360;
        Background            = LlmChatWindow.BgMain;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize             = true;

        var titleBlock = new TextBlock
        {
            Text       = "🙋  The assistant needs your input",
            FontSize   = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColText,
        };

        _pageLabel = new TextBlock
        {
            FontSize   = 12,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(0, 4, 0, 0),
        };

        _countdownLabel = new TextBlock
        {
            FontSize   = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = LlmChatWindow.ColAccent,
            Margin     = new Thickness(0, 8, 0, 2),
        };

        _countdownBar = new ProgressBar
        {
            Minimum    = 0,
            Maximum    = _timeoutDuration.TotalSeconds,
            Value      = _timeoutDuration.TotalSeconds,
            Height     = 6,
            Foreground = LlmChatWindow.ColAccent,
            Background = LlmChatWindow.ColDisabled,
        };

        var headerPanel = new StackPanel { Margin = new Thickness(18, 16, 18, 8) };
        headerPanel.Children.Add(titleBlock);
        headerPanel.Children.Add(_pageLabel);
        headerPanel.Children.Add(_countdownLabel);
        headerPanel.Children.Add(_countdownBar);
        DockPanel.SetDock(headerPanel, Dock.Top);

        _validationLabel = new TextBlock
        {
            FontSize     = 11,
            Foreground   = LlmChatWindow.ColRunningRed,
            TextWrapping = TextWrapping.Wrap,
            IsVisible    = false,
            Margin       = new Thickness(18, 0, 18, 4),
        };
        DockPanel.SetDock(_validationLabel, Dock.Bottom);

        _backButton = new Button { Content = "◀  Back", FontSize = 12, Padding = new Thickness(14, 7, 14, 7) };
        UiTheme.StyleButton(_backButton, LlmChatWindow.ColDisabled, LlmChatWindow.ColText,
            disabledBackground: LlmChatWindow.ColDisabled, disabledForeground: LlmChatWindow.ColMuted);
        _backButton.Click += (_, _) => GoBack();

        _nextButton = new Button { Content = "Next  ▶", FontSize = 12, Padding = new Thickness(14, 7, 14, 7) };
        UiTheme.StyleButton(_nextButton, LlmChatWindow.ColAccent, Brushes.White);
        _nextButton.Click += (_, _) => GoNext();

        _submitButton = new Button { Content = "✔  Submit", FontSize = 12, Padding = new Thickness(16, 7, 16, 7) };
        UiTheme.StyleButton(_submitButton, LlmChatWindow.ColDoneBadgeBg, Brushes.White);
        _submitButton.Click += (_, _) => Submit();

        var footer = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Thickness(18, 8, 18, 16),
        };
        footer.Children.Add(_backButton);
        footer.Children.Add(_nextButton);
        footer.Children.Add(_submitButton);
        DockPanel.SetDock(footer, Dock.Bottom);

        _pageHost = new StackPanel { Spacing = 10, Margin = new Thickness(18, 6, 18, 6) };
        var scroll = new ScrollViewer
        {
            Content                       = _pageHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(headerPanel);
        root.Children.Add(footer);
        root.Children.Add(_validationLabel);
        root.Children.Add(scroll);
        Content = root;

        _deadlineUtc = DateTime.UtcNow + _timeoutDuration;
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => UpdateCountdown();

        Closed += (_, _) =>
        {
            _countdownTimer.Stop();
            if (!_finished)
            {
                _finished = true;
                _onCancelled();
            }
        };

        UpdateCountdown();
        _countdownTimer.Start();

        RenderPage(0);
    }

    private void UpdateCountdown()
    {
        TimeSpan remaining = _deadlineUtc - DateTime.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            _countdownTimer.Stop();
            _countdownBar.Value   = 0;
            _countdownLabel.Text  = "⏰ Time is up — no answer received.";
            _countdownLabel.Foreground = LlmChatWindow.ColRunningRed;
            _countdownBar.Foreground   = LlmChatWindow.ColRunningRed;

            if (!_finished)
            {
                _finished = true;
                _onTimeout();
            }
            Close();
            return;
        }

        _countdownBar.Value  = remaining.TotalSeconds;
        _countdownLabel.Text = $"⏳ Time remaining: {remaining:mm\\:ss} — answer before this window closes automatically";

        // Visual urgency cue: calm accent → amber (< 5 min) → pulsing red (< 1 min).
        if (remaining <= TimeSpan.FromMinutes(1))
        {
            bool pulse = DateTime.UtcNow.Second % 2 == 0;
            IBrush pulseBrush = pulse ? LlmChatWindow.ColRunningRed : LlmChatWindow.ColRetryAmber;
            _countdownLabel.Foreground = pulseBrush;
            _countdownBar.Foreground   = pulseBrush;
        }
        else if (remaining <= TimeSpan.FromMinutes(5))
        {
            _countdownLabel.Foreground = LlmChatWindow.ColRetryAmber;
            _countdownBar.Foreground   = LlmChatWindow.ColRetryAmber;
        }
        else
        {
            _countdownLabel.Foreground = LlmChatWindow.ColAccent;
            _countdownBar.Foreground   = LlmChatWindow.ColAccent;
        }
    }

    private void GoBack()
    {
        if (_currentIndex <= 0) return;
        SaveCurrentPageAnswer(requireAnswer: false);
        RenderPage(_currentIndex - 1);
    }

    private void GoNext()
    {
        if (!SaveCurrentPageAnswer(requireAnswer: true)) return;
        if (_currentIndex >= _questions.Count - 1) return;
        RenderPage(_currentIndex + 1);
    }

    private void Submit()
    {
        if (!SaveCurrentPageAnswer(requireAnswer: true)) return;

        var answers = new List<QuestionAnswer>(_questions.Count);
        for (int i = 0; i < _questions.Count; i++)
        {
            answers.Add(new QuestionAnswer
            {
                Question    = _questions[i].Question,
                Answer      = _chosenAnswer[i] ?? "(no answer provided)",
                WasFreeText = _isFreeText[i],
            });
        }

        _finished = true;
        _countdownTimer.Stop();
        _onSubmit(answers);
        Close();
    }

    private CheckBox[]  _currentAnswerBoxes = Array.Empty<CheckBox>();
    private TextBox?    _currentFreeTextBox;

    private void RenderPage(int index)
    {
        _currentIndex = index;
        QuestionSpec q = _questions[index];

        bool isFirst  = index == 0;
        bool isLast   = index == _questions.Count - 1;
        bool multiple = _questions.Count > 1;

        _pageLabel.Text = multiple
            ? $"Question {index + 1} of {_questions.Count}"
            : "One question";

        _backButton.IsVisible   = multiple;
        _backButton.IsEnabled   = multiple && !isFirst;
        _nextButton.IsVisible   = multiple && !isLast;
        _submitButton.IsVisible = isLast;

        _validationLabel.IsVisible = false;

        _pageHost.Children.Clear();

        _pageHost.Children.Add(new TextBlock
        {
            Text         = q.Question,
            FontSize     = 15,
            FontWeight   = FontWeight.Medium,
            Foreground   = LlmChatWindow.ColText,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 6),
        });

        var answersPanel = new StackPanel { Spacing = 4 };
        var boxes = new List<CheckBox>();

        foreach (string answerText in q.Answers)
        {
            var cb = new CheckBox
            {
                Content      = answerText,
                FontSize     = 13,
                Foreground   = LlmChatWindow.ColText,
                IsChecked    = !_isFreeText[index] && string.Equals(_chosenAnswer[index], answerText, StringComparison.Ordinal),
            };
            UiTheme.StyleCheckBox(cb, LlmChatWindow.ColText);
            cb.IsCheckedChanged += (_, _) => OnAnswerBoxChanged(cb, answerText);
            answersPanel.Children.Add(cb);
            boxes.Add(cb);
        }

        _currentAnswerBoxes = boxes.ToArray();
        _pageHost.Children.Add(answersPanel);

        _pageHost.Children.Add(new TextBlock
        {
            Text       = "— or type your own answer —",
            FontSize   = 11,
            Foreground = LlmChatWindow.ColMuted,
            Margin     = new Thickness(0, 8, 0, 2),
        });

        var freeTextBox = new TextBox
        {
            Watermark     = "Type a custom answer…",
            AcceptsReturn = true,
            TextWrapping  = TextWrapping.Wrap,
            MinHeight     = 60,
            MaxHeight     = 120,
            FontSize      = 13,
            Padding       = new Thickness(10, 8, 10, 8),
            Text          = _isFreeText[index] ? _chosenAnswer[index] : null,
        };
        UiTheme.StyleTextBox(freeTextBox, LlmChatWindow.BgAiBubble, LlmChatWindow.ColText,
            borderBrush: LlmChatWindow.ColDivider, focusBorderBrush: LlmChatWindow.ColAccent);
        freeTextBox.TextChanged += (_, _) => OnFreeTextChanged(freeTextBox);

        _currentFreeTextBox = freeTextBox;
        _pageHost.Children.Add(freeTextBox);
    }

    private void OnAnswerBoxChanged(CheckBox changed, string answerText)
    {
        if (changed.IsChecked == true)
        {
            foreach (CheckBox other in _currentAnswerBoxes)
                if (!ReferenceEquals(other, changed))
                    other.IsChecked = false;

            if (_currentFreeTextBox != null && !string.IsNullOrEmpty(_currentFreeTextBox.Text))
                _currentFreeTextBox.Text = string.Empty;

            _chosenAnswer[_currentIndex] = answerText;
            _isFreeText[_currentIndex]   = false;
            _validationLabel.IsVisible   = false;
        }
        else if (string.Equals(_chosenAnswer[_currentIndex], answerText, StringComparison.Ordinal) && !_isFreeText[_currentIndex])
        {
            _chosenAnswer[_currentIndex] = null;
        }
    }

    private void OnFreeTextChanged(TextBox box)
    {
        if (string.IsNullOrEmpty(box.Text)) return;

        // Typing a free-form answer deselects any provided-answer checkbox.
        foreach (CheckBox cb in _currentAnswerBoxes)
            if (cb.IsChecked == true) cb.IsChecked = false;

        _chosenAnswer[_currentIndex] = box.Text;
        _isFreeText[_currentIndex]   = true;
        _validationLabel.IsVisible   = false;
    }

    /// <summary>Persists whatever is currently selected/typed on THIS page into _chosenAnswer/_isFreeText.</summary>
    private bool SaveCurrentPageAnswer(bool requireAnswer)
    {
        string? freeText = _currentFreeTextBox?.Text;
        if (!string.IsNullOrEmpty(freeText))
        {
            _chosenAnswer[_currentIndex] = freeText;
            _isFreeText[_currentIndex]   = true;
        }
        else
        {
            CheckBox? checkedBox = Array.Find(_currentAnswerBoxes, b => b.IsChecked == true);
            if (checkedBox != null)
            {
                _chosenAnswer[_currentIndex] = (string)checkedBox.Content!;
                _isFreeText[_currentIndex]   = false;
            }
            else if (!_isFreeText[_currentIndex])
            {
                _chosenAnswer[_currentIndex] = null;
            }
        }

        if (requireAnswer && string.IsNullOrWhiteSpace(_chosenAnswer[_currentIndex]))
        {
            _validationLabel.Text      = "Please pick one of the provided answers, or type your own, before continuing.";
            _validationLabel.IsVisible = true;
            return false;
        }

        return true;
    }
}
