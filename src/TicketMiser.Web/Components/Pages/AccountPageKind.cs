namespace TicketMiser.Web.Components.Pages;

/// <summary>Which of the account pages <see cref="AccountPage"/> draws.</summary>
public enum AccountPageKind
{
    /// <summary>GET /account with no cookie: the sign-in form.</summary>
    SignedOut,

    /// <summary>GET /account with a cookie: the address, its watches, its purchases and savings, and sign-out.</summary>
    SignedIn,

    /// <summary>GET of a sign-in link: one button, because a link scanner's GET must not spend the link.</summary>
    VerifyPrompt,

    /// <summary>A link that is unknown, expired or already used. The same words for all three.</summary>
    VerifyUnknown
}

/// <summary>The one-line notice above the page's form, from the query string after a redirect.</summary>
public enum AccountNotice
{
    None,

    /// <summary>After a sign-in post: the same whether or not the address has an account.</summary>
    LinkSent,

    /// <summary>The post was not an address; nothing was sent.</summary>
    InvalidAddress,

    SignedOut,

    /// <summary>Signed in, after the Log a purchase form's 303.</summary>
    PurchaseLogged,

    /// <summary>Signed in, after a purchase's Delete.</summary>
    PurchaseDeleted
}
