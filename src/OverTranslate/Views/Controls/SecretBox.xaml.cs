using System.Windows;
using System.Windows.Controls;
// UseWindowsForms puts System.Windows.Forms in the implicit usings, so this name collides
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Views.Controls;

/// <summary>
/// A single-line editor for secrets (API keys) that can be typed or pasted in but never read back
/// out: a key that is already saved shows only as <c>sk-••••••••3f2a</c>, with a button that clears
/// it so a new one can be entered.
/// </summary>
/// <remarks>
/// <para>For a computer the key is lent to: whoever uses it can translate with the key but cannot
/// see or copy it from here. While a key is being typed it is in a PasswordBox, which shows dots
/// and refuses copy and cut; once saved and shown again, the key is not in any editable control at
/// all, only the masked form in a TextBlock that cannot be selected.</para>
///
/// <para>There used to be an eye button that showed the key in a TextBox; it is gone on purpose.
/// The settings file keeps the key encrypted for the same reason — see
/// <see cref="Services.SecretStore"/>.</para>
/// </remarks>
public partial class SecretBox : UserControl
{
    /// <summary>The saved key while it is shown masked; empty while one is being typed.</summary>
    private string _saved = "";

    public SecretBox()
    {
        InitializeComponent();
        ShowEditing();
    }

    /// <summary>Raised when the user types, pastes or clears the value.</summary>
    public event EventHandler? SecretChanged;

    /// <summary>
    /// The value. Setting a non-empty one shows it masked, the way a saved key is shown; setting it
    /// does not raise <see cref="SecretChanged"/>.
    /// </summary>
    public string Secret
    {
        get => _saved.Length > 0 ? _saved : MaskedBox.Password;
        set
        {
            var text = value ?? "";
            if (text.Length > 0)
            {
                _saved = text;
                ShowSaved();
            }
            else
            {
                _saved = "";
                ShowEditing();
            }
        }
    }

    /// <summary>
    /// What a saved key shows as: enough of its ends to tell two keys apart, never enough to use.
    /// </summary>
    /// <remarks>
    /// The first three characters are the provider's fixed prefix on the keys this is for (sk-);
    /// the last four are what the provider's own key list shows beside each key. A short value
    /// shows nothing of itself.
    /// </remarks>
    internal static string Mask(string secret) =>
        secret.Length >= 12
            ? $"{secret[..3]}••••••••{secret[^4..]}"
            : "••••••••";

    private void ShowSaved()
    {
        // Out of the editor entirely, so nothing on screen holds the key itself.
        MaskedBox.PasswordChanged -= MaskedBox_PasswordChanged;
        MaskedBox.Password = "";
        MaskedBox.PasswordChanged += MaskedBox_PasswordChanged;

        SavedText.Text = Mask(_saved);
        SavedText.Visibility = Visibility.Visible;
        ClearButton.Visibility = Visibility.Visible;
        MaskedBox.Visibility = Visibility.Collapsed;
    }

    private void ShowEditing()
    {
        MaskedBox.PasswordChanged -= MaskedBox_PasswordChanged;
        MaskedBox.Password = "";
        MaskedBox.PasswordChanged += MaskedBox_PasswordChanged;

        SavedText.Text = "";
        SavedText.Visibility = Visibility.Collapsed;
        ClearButton.Visibility = Visibility.Collapsed;
        MaskedBox.Visibility = Visibility.Visible;
    }

    private void MaskedBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        SecretChanged?.Invoke(this, EventArgs.Empty);

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _saved = "";
        ShowEditing();
        MaskedBox.Focus();
        SecretChanged?.Invoke(this, EventArgs.Empty);
    }
}
