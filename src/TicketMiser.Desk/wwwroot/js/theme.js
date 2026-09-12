// The desk's appearance, on the browser side.
//
// Three jobs, and only three: read and write the operator's choices in localStorage,
// stamp the resolved appearance onto <html> so the token blocks switch, and report what
// prefers-color-scheme currently says. Everything about *which* theme, accent or size
// should be showing is decided in ThemeService; this file holds no policy, because a
// policy living in two languages is a policy that disagrees with itself.

// Every localStorage touch is guarded. It throws outright — not returns null — in a
// Safari private window and under a "block all cookies" setting, and an appearance
// preference is not worth taking the circuit down for. The desk simply forgets.
export function read(key) {
    try {
        return localStorage.getItem(key);
    } catch {
        return null;
    }
}

export function store(key, value) {
    try {
        localStorage.setItem(key, value);
    } catch {
        /* forgotten, not fatal */
    }
}

export function prefersDark() {
    return window.matchMedia
        ? window.matchMedia('(prefers-color-scheme: dark)').matches
        : true;
}

// The whole switch, in two attributes and two custom properties. Both theme blocks
// weigh (0,1,0) and the light one is written second, so stamping "light" is enough to
// hand the entire desk over; the accent blocks are compound selectors on top of them.
// "apple-dark" and "blue" are set explicitly rather than by removing the attributes so
// that what is showing is always readable in the DOM inspector.
//
// --type-scale multiplies every font-size in the desk's stylesheets; --ui-scale is
// the zoom on <body>. Both land on the root so a component that wants to know can
// read them back with getComputedStyle.
export function apply(theme, accent, typeScale, uiScale) {
    const root = document.documentElement;
    const scaleMoved = root.style.getPropertyValue('--ui-scale') !== String(uiScale);

    root.setAttribute('data-theme', theme);
    root.setAttribute('data-accent', accent);
    root.style.setProperty('--type-scale', String(typeScale));
    root.style.setProperty('--ui-scale', String(uiScale));

    // A new zoom changes how many CSS pixels the desk has without the browser window
    // moving, and the windowing script only re-measures on resize. Telling it the
    // window resized is the truth as far as layout is concerned, and it keeps this
    // module from knowing anything about how the desk lays windows out.
    if (scaleMoved) {
        window.dispatchEvent(new Event('resize'));
    }

    // The resolved paint, kept beside the choices it came from. The inline script in
    // App.razor's <head> stamps this onto <html> before the first frame, so an operator
    // who chose a light or larger desk never sees the dark, unscaled one flash first.
    // It is a snapshot, not policy: whatever this session last painted, verbatim, and
    // the service corrects it within the first render if the machine moved meanwhile.
    store(PAINT_KEY, JSON.stringify({ theme, accent, typeScale, uiScale }));
}

const PAINT_KEY = 'ticketmiser.paint';

// Watch the machine's own preference so System mode keeps meaning "system" rather
// than "whatever system said when the circuit opened". The listener is kept on the
// module so a reconnect can replace it instead of stacking a second one.
let query = null;
let listener = null;

export function watch(dotNetRef) {
    unwatch();

    if (!window.matchMedia) {
        return;
    }

    query = window.matchMedia('(prefers-color-scheme: dark)');
    listener = e => dotNetRef.invokeMethodAsync('OnSystemPreferenceChanged', e.matches);

    // addEventListener over the deprecated addListener, with a fallback because
    // older WebKit only has the latter and would otherwise silently never fire.
    if (query.addEventListener) {
        query.addEventListener('change', listener);
    } else {
        query.addListener(listener);
    }
}

export function unwatch() {
    if (!query || !listener) {
        return;
    }

    if (query.removeEventListener) {
        query.removeEventListener('change', listener);
    } else {
        query.removeListener(listener);
    }

    query = null;
    listener = null;
}
