// The game's YES / NO dialog (START.SCN function 244): the screen dimmed twice with SYSTEM.S25
// slot 10, the question (231 終了しますか？, 232 タイトルへ戻りますか？) and the buttons YES 220 and
// NO 210 (+1 highlighted). A right click answers NO. The game asks nothing else: saving over a
// slot, loading and quick load go ahead at once.

using System.Windows;

namespace OpenShiina.Windows;

public partial class PlayerWindow
{
    private const int QuitQuestion = 231, TitleQuestion = 232;

    private TaskCompletionSource<bool>? m_dialog;

    private bool DialogOpen => DialogLayer.Visibility == Visibility.Visible;

    private Task<bool> AskAsync(int question)
    {
        if (m_data.GetSystemFrame(question) == null)
        {
            string text = question == QuitQuestion ? "Quit the game?" : "Return to the title screen?";
            return Task.FromResult(MessageBox.Show(this, text, m_player.Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
        }
        m_dialog?.TrySetResult(false);
        var answer = m_dialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogLayer.Children.Clear();
        AddSystemPicture(DialogLayer, 10);
        AddSystemPicture(DialogLayer, 10);
        AddSystemPicture(DialogLayer, question);
        SystemButton(DialogLayer, 220, () => CloseDialog(true));
        SystemButton(DialogLayer, 210, () => CloseDialog(false));
        DialogLayer.Visibility = Visibility.Visible;
        return answer.Task;
    }

    private void CloseDialog(bool yes)
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        DialogLayer.Children.Clear();
        var answer = m_dialog;
        m_dialog = null;
        Focus();
        answer?.TrySetResult(yes);
    }

    private async void QuitGame()
    {
        if (await AskAsync(QuitQuestion))
            Close();
    }

    private async void BackToTitle()
    {
        if (await AskAsync(TitleQuestion))
        {
            if (OptionLayer.Visibility == Visibility.Visible)
                HideOption();
            if (SaveLayer.Visibility == Visibility.Visible)
                HideSavePage();
            ShowTitle(false);
        }
    }
}
