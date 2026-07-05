using System.Windows;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.6.0 MINOR T2: WPF-backed <see cref="IMessageBoxPrompt"/>.
/// Marshals the modal onto the WPF STA thread (required by
/// <see cref="MessageBox.Show(Window, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxOptions)"/>
/// when an owner <see cref="Window"/> is supplied). Tests inject a
/// fake implementation so the xunit harness never instantiates a WPF
/// modal.
/// </summary>
public sealed class WpfMessageBoxPrompt : IMessageBoxPrompt
{
    /// <inheritdoc />
    public Task<MessageBoxResult> ShowAsync(
        string title,
        string message,
        Window? owner)
    {
        // The caller is typically the WPF dispatcher thread already,
        // but tests may invoke from xunit without an STA context.
        // Dispatcher.InvokeAsync + Task.Unwrap ensures the modal is
        // created on the STA thread that owns the Application's
        // message pump.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // No Application instance (e.g. a unit test that forgot
            // to inject a fake) — fall back to a direct synchronous
            // call. This branch should not fire in production.
            return Task.FromResult(ShowInternal(title, message, owner));
        }
        return dispatcher.InvokeAsync(() => ShowInternal(title, message, owner)).Task;
    }

    private static MessageBoxResult ShowInternal(
        string title,
        string message,
        Window? owner)
    {
        // Yes/No is the only modal we offer — Yes restores, No
        // suppresses future prompts. DefaultResult=No so an
        // accidental Enter keypress doesn't restore without intent.
        return owner is not null
            ? MessageBox.Show(
                owner,
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No)
            : MessageBox.Show(
                message,
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
    }
}