namespace TicketMiser.Web.Components.Pages;

/// <summary>Which of the report list's pages <see cref="ReportSubscriptionPage"/> draws.</summary>
public enum ReportSubscriptionPageKind
{
    /// <summary>GET of a confirmation link: one button, because a link scanner must not confirm anyone.</summary>
    ConfirmPrompt,

    /// <summary>After the button's POST: the address will get the monthly report.</summary>
    Confirmed,

    /// <summary>The confirmation link matched nothing, or the address has since unsubscribed.</summary>
    ConfirmUnknown,

    /// <summary>GET of an unsubscribe link: one button, because a link scanner must not unsubscribe anyone.</summary>
    UnsubscribePrompt,

    /// <summary>After the one-click POST. The same words whether or not the token matched anything.</summary>
    Unsubscribed
}
