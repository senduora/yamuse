using System.Windows;
using System.Windows.Input;

namespace YMusic.Native;

// Keep logical keyboard focus after a click, but show focus chrome only for
// keyboard interaction. Tab is observed before focus moves to the next control.
public static class InputFeedback
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(InputFeedback), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyPropertyKey KeyboardFocusVisiblePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "KeyboardFocusVisible", typeof(bool), typeof(InputFeedback), new PropertyMetadata(false));
    public static readonly DependencyProperty KeyboardFocusVisibleProperty = KeyboardFocusVisiblePropertyKey.DependencyProperty;
    private static bool _keyboardInput = true;

    static InputFeedback()
    {
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyboardInput), true);
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerInput), true);
    }

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetKeyboardFocusVisible(DependencyObject element) => (bool)element.GetValue(KeyboardFocusVisibleProperty);

    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not UIElement element) return;
        if ((bool)args.NewValue)
        {
            element.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyboardInput), true);
            element.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerInput), true);
            element.GotKeyboardFocus += FocusChanged;
            element.LostKeyboardFocus += FocusChanged;
        }
        else
        {
            element.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyboardInput));
            element.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerInput));
            element.GotKeyboardFocus -= FocusChanged;
            element.LostKeyboardFocus -= FocusChanged;
            element.SetValue(KeyboardFocusVisiblePropertyKey, false);
        }
    }

    private static void KeyboardInput(object sender, KeyEventArgs args)
    {
        _keyboardInput = true;
        UpdateFocused(sender as UIElement);
    }
    private static void PointerInput(object sender, MouseButtonEventArgs args)
    {
        _keyboardInput = false;
        UpdateFocused(sender as UIElement);
    }
    private static void UpdateFocused(UIElement? source)
    {
        if (source is not null && GetEnabled(source))
            source.SetValue(KeyboardFocusVisiblePropertyKey, _keyboardInput && source.IsKeyboardFocusWithin);
        if (Keyboard.FocusedElement is UIElement focused && GetEnabled(focused))
            focused.SetValue(KeyboardFocusVisiblePropertyKey, _keyboardInput && focused.IsKeyboardFocusWithin);
    }
    private static void FocusChanged(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (sender is UIElement element)
            element.SetValue(KeyboardFocusVisiblePropertyKey, _keyboardInput && element.IsKeyboardFocusWithin);
    }
}
