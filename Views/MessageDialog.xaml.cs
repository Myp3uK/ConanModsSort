using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ConanModsSort;

public enum MessageKind { Info, Warning, Error, Question }
public enum MessageButtons { Ok, YesNo, YesNoCancel }
public enum MessageResult { Ok, Yes, No, Cancel }

public partial class MessageDialog : Window
{
    private MessageResult _result = MessageResult.Cancel;
    public MessageResult Result => _result;

    public MessageDialog(string title, string message, MessageButtons buttons, MessageKind kind)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        var (glyph, color) = kind switch
        {
            MessageKind.Error    => ("", Color.FromRgb(0xE0, 0x71, 0x5E)),
            MessageKind.Warning  => ("", Color.FromRgb(0xE0, 0xA0, 0x30)),
            MessageKind.Question => ("", Color.FromRgb(0x3A, 0x7B, 0xD5)),
            _                    => ("", Color.FromRgb(0x3A, 0x7B, 0xD5)),
        };
        IconGlyph.Text = glyph;
        IconGlyph.Foreground = new SolidColorBrush(color);

        bool ok = buttons == MessageButtons.Ok;
        btnOk.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        btnYes.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        btnNo.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        btnCancel.Visibility = buttons == MessageButtons.YesNoCancel ? Visibility.Visible : Visibility.Collapsed;

        // клавиши Enter/Esc — только на видимые кнопки
        if (ok) { btnOk.IsDefault = true; btnOk.IsCancel = true; }
        else
        {
            btnYes.IsDefault = true;
            if (buttons == MessageButtons.YesNoCancel) btnCancel.IsCancel = true;
            else btnNo.IsCancel = true;
        }

        Loaded += (_, _) => SoftChime.Play();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Finish(MessageResult.Ok, true);
    private void Yes_Click(object sender, RoutedEventArgs e) => Finish(MessageResult.Yes, true);
    private void No_Click(object sender, RoutedEventArgs e) => Finish(MessageResult.No, true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(MessageResult.Cancel, false);

    private void Finish(MessageResult result, bool dialogResult)
    {
        _result = result;
        DialogResult = dialogResult;
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    public static MessageResult Show(Window? owner, string title, string message,
        MessageButtons buttons = MessageButtons.Ok, MessageKind kind = MessageKind.Info)
    {
        var dlg = new MessageDialog(title, message, buttons, kind) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }
}
