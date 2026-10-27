const COOKIE_NAME = '.AspNetCore.Culture';
const SUPPORTED = new Set(['en-GB', 'sv-SE', 'es-ES']);

function readCookieLanguage() {
    const entry = document.cookie.split(';').map(value => value.trim()).find(value => value.startsWith(`${COOKIE_NAME}=`));
    if (!entry) return null;
    try {
        const value = decodeURIComponent(entry.substring(COOKIE_NAME.length + 1));
        const match = /^c=[^|]+\|uic=(en-GB|sv-SE|es-ES)$/.exec(value);
        return match ? match[1] : null;
    } catch { return null; }
}

export function updateDocumentLanguage(culture, recoveryText) {
    if (!SUPPORTED.has(culture)) return;
    document.documentElement.lang = culture.substring(0, 2);
    // Keep the one recovery modal and its listeners intact while changing text only.
    const modal = document.getElementById('components-reconnect-modal');
    if (!modal) return;
    modal.lang = culture.substring(0, 2);
    for (const element of modal.querySelectorAll('[data-shell-text]')) {
        const text = recoveryText?.[element.dataset.shellText];
        if (typeof text === 'string') element.textContent = text;
    }
}

export function initializeLanguage(fallback, recoveryByCulture) {
    const culture = readCookieLanguage() ?? (SUPPORTED.has(fallback) ? fallback : 'en-GB');
    updateDocumentLanguage(culture, recoveryByCulture[culture]);
    return culture;
}

export function saveLanguage(culture, recoveryText) {
    if (!SUPPORTED.has(culture)) return { saved: false, refreshNotFound: false };
    const secure = location.protocol === 'https:' ? '; Secure' : '';
    document.cookie = `${COOKIE_NAME}=c=en-GB|uic=${culture}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
    if (readCookieLanguage() !== culture) return { saved: false, refreshNotFound: false };
    updateDocumentLanguage(culture, recoveryText);
    return { saved: true, refreshNotFound: document.querySelector('[data-language-refresh="not-found"]') !== null };
}
