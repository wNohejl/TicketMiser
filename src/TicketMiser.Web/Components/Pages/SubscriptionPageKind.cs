namespace TicketMiser.Web.Components.Pages;

/// <summary>Which of the subscription pages <see cref="SubscriptionPage"/> draws.</summary>
public enum SubscriptionPageKind
{
    /// <summary>The confirmation link was followed; the address will get the alert.</summary>
    Confirmed,

    /// <summary>The confirmation link matched nothing, or the address has since unsubscribed.</summary>
    ConfirmUnknown,

    /// <summary>GET of an unsubscribe link: one button, because a link scanner must not unsubscribe anyone.</summary>
    UnsubscribePrompt,

    /// <summary>After the one-click POST. The same words whether or not the token matched anything.</summary>
    Unsubscribed
}
