using System.Globalization;
using HandloomFactory.App.Resources.Strings;
using HandloomFactory.Core.Common;

namespace HandloomFactory.App.Services;

/// <summary>Pop-up dialogs, so view models don't depend on pages (same pattern as Travel Vault).</summary>
public sealed class Dialogs
{
    private static Page Page => Shell.Current?.CurrentPage ?? Application.Current!.Windows[0].Page!;

    public Task AlertAsync(string title, string message) => Page.DisplayAlertAsync(title, message, AppResources.Common_OK);

    public Task<bool> ConfirmAsync(string title, string message, string accept, string? cancel = null) =>
        Page.DisplayAlertAsync(title, message, accept, cancel ?? AppResources.Common_Cancel);

    /// <returns>The typed text, or null when cancelled.</returns>
    public Task<string?> PromptAsync(string title, string message, Keyboard? keyboard = null, int maxLength = 50, string initialValue = "") =>
        Page.DisplayPromptAsync(title, message, AppResources.Common_OK, AppResources.Common_Cancel, maxLength: maxLength, keyboard: keyboard ?? Keyboard.Default, initialValue: initialValue);

    public Task ErrorAsync(Error? error) => AlertAsync(AppResources.Common_Check, ErrorText.For(error));

    /// <summary>A short message at the bottom of the screen (Android toast).</summary>
    public static void Toast(string text) => MainThread.BeginInvokeOnMainThread(() =>
        Android.Widget.Toast.MakeText(Platform.AppContext, text, Android.Widget.ToastLength.Short)?.Show());
}

/// <summary>Shell navigation in one place.</summary>
public static class Nav
{
    public static Task GoAsync(string route) => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(route));

    public static Task BackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));

    /// <summary>The owner's tabs, or the worker's own home on a worker's phone.</summary>
    public static Task HomeAsync()
    {
        var session = IPlatformApplication.Current?.Services.GetService<AppHost>()?.Session;
        return GoAsync(session is { IsSignedIn: true, IsAdmin: false } ? "//worker/whome" : "//main/home");
    }

    public static Task LoginAsync() => GoAsync("//login");
}

/// <summary>Turns a use-case error into a sentence for the user.</summary>
public static class ErrorText
{
    public static string For(Error? error)
    {
        var text = error?.Code switch
        {
            ErrorCode.NotAllowed => AppResources.Error_NotAllowed,
            ErrorCode.NotFound => AppResources.Error_NotFound,
            ErrorCode.NameRequired => AppResources.Error_NameRequired,
            ErrorCode.PinMustBeFourDigits => AppResources.Error_PinMustBeFourDigits,
            ErrorCode.PinsDoNotMatch => AppResources.Error_PinsDoNotMatch,
            ErrorCode.WrongPin => AppResources.Error_WrongPin,
            ErrorCode.LockedOut => AppResources.Error_LockedOut,
            ErrorCode.RateInvalid => AppResources.Error_RateInvalid,
            ErrorCode.CountOutOfRange => string.Format(CultureInfo.CurrentCulture, AppResources.Error_CountOutOfRange, Core.Factory.FactoryInfo.DefaultMaxSareesPerDay),
            ErrorCode.FutureDate => AppResources.Error_FutureDate,
            ErrorCode.NoRateOnDate => AppResources.Error_NoRateOnDate,
            ErrorCode.AlreadySetUp => AppResources.Error_AlreadySetUp,
            ErrorCode.PhoneInvalid => AppResources.Error_PhoneInvalid,
            ErrorCode.ImportInvalid => AppResources.Error_ImportInvalid,
            ErrorCode.ImportTooNew => AppResources.Error_ImportTooNew,
            ErrorCode.AlreadyPaid => AppResources.Error_AlreadyPaid,
            ErrorCode.NotPaid => AppResources.Error_NotPaid,
            ErrorCode.NothingToPay => AppResources.Error_NothingToPay,
            ErrorCode.PaidDateInvalid => AppResources.Error_PaidDateInvalid,
            ErrorCode.PinAlreadySet => AppResources.Error_PinAlreadySet,
            ErrorCode.InviteInvalid => AppResources.Error_InviteInvalid,
            ErrorCode.InviteExpired => AppResources.Error_InviteExpired,
            ErrorCode.NoInternet => AppResources.Error_NoInternet,
            ErrorCode.TodaySetByOwner => AppResources.Error_TodaySetByOwner,
            _ => AppResources.Common_Error,
        };

        // Day-entry errors name the worker in Detail.
        return error?.Detail is { Length: > 0 } who && error.Code is ErrorCode.CountOutOfRange or ErrorCode.NoRateOnDate or ErrorCode.FutureDate or ErrorCode.AlreadyPaid or ErrorCode.NothingToPay
            ? string.Format(CultureInfo.CurrentCulture, AppResources.Error_ForWorker, who, text)
            : text;
    }
}

/// <summary>Dates and numbers as shown on screen.</summary>
public static class Show
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    /// <summary>"Mon 23 Nov".</summary>
    public static string Day(DateOnly day) => day.ToString("ddd d MMM", C);

    /// <summary>"Mon 23 Nov 2026".</summary>
    public static string DayWithYear(DateOnly day) => day.ToString("ddd d MMM yyyy", C);

    /// <summary>"Sun 22 Nov – Sat 28 Nov".</summary>
    public static string Range(DateRange range) => $"{Day(range.Start)} – {Day(range.End)}";

    public static string Month(DateOnly day) => day.ToString("MMMM yyyy", C);

    /// <summary>"Su", "Mo"… for report columns.</summary>
    public static string ShortWeekday(DateOnly day) => day.ToString("ddd", C)[..2];

    public static string Sarees(int count) => count == 1 ? AppResources.Common_OneSaree : string.Format(C, AppResources.Common_Sarees, count);

    public static string PerSaree(Money rate) => string.Format(C, AppResources.Common_PerSaree, rate);

    public static string Format(string template, params object?[] args) => string.Format(C, template, args);
}
